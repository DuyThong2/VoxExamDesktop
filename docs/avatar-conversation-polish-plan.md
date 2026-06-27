> Written 2026-06-26, for codex to implement directly (Claude's role this round was audit/plan
> only, per the project's default working pattern — see `feedback-audit-then-codex-implements`
> in Claude's memory if curious). Builds on the just-shipped audio-only "phone call" avatar mode
> (see `realtime-self-hosted-avatar-plan.md` for that history) — this plan is three independent,
> unrelated-to-each-other bugfixes/features found from one live test run. Implement and verify
> each section independently; none depend on the others.

# Avatar conversation polish (3 fixes from live testing)

## Context

The audio-only avatar ("phone call" mode — TTS only, no rendered video) is live and mostly
working. A live test surfaced three separate problems:

1. The system sometimes re-asks the exact same question right after the student already
   answered it (e.g. asked "What is your name?", student answered, system asked "What is your
   name?" again) — and when it does, it prefixes with a stiff "Sure, I'll repeat the question
   once." The user wants: (a) the actual repeat-after-answering bug fixed, and (b) when a repeat
   *does* legitimately happen — either because the system didn't hear anything, or because the
   student explicitly asks to hear the question again — the phrasing should be natural/soft, and
   the two cases should sound different from each other. When the student explicitly asks to
   repeat, the question should be re-spoken **slowly**, exactly once.
2. There's no visual feedback that the system is hearing the student speak — the user wants the
   camera preview to "chớp chớp" (flicker/pulse/glow) in sync with their own voice while they're
   talking, mirroring the avatar's own speaking-ripple indicator that already exists.

## Section 1 — Stop the spurious repeat, and make the repeat wording human

### 1a. Root cause of the repeat-after-answering bug

This is believed to be a race introduced by this project's own recent fix
(`WaitForAvatarSpeechToFinishAsync` in `RealtimeExamFlowService.cs`, added to stop the
silence-timeout clock from running concurrently with the avatar's own TTS playback).

File: `DesktopApp/VoxOralExam/VoxOralExam.DesktopApp/Services/RealtimeExamFlowService.cs`

Current flow in `RunQuestionAsync`:

```csharp
var questionContext = BuildQuestionContext(question);
await _sessionClient.SendQuestionStartAsync(attemptAnswerId, questionContext, language: "en-US", ct);
await WaitForAvatarSpeechToFinishAsync(ct);

var turnOrder = 1;
var questionDone = false;

while (!questionDone && turnOrder <= maxTurnsPerQuestion)
{
    ...
    var spoke = await WaitForSpeechStartAsync(initialTimeout, ct);
    if (spoke) { ...; await WaitForSpeechEndWithGraceAsync(overallTimeout, gracePeriod, ct); ... }

    var pcmBytes = _recorder!.GetTurnBufferAndReset();
    ...
    var decision = await _sessionClient.SendTurnEndAndWaitAsync(ct);
    ...
    await WaitForAvatarSpeechToFinishAsync(ct);   // added in the previous fix

    questionDone = !decision.ShouldContinue;
    turnOrder++;
}
```

```csharp
private async Task<bool> WaitForSpeechStartAsync(TimeSpan timeout, CancellationToken ct)
{
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    _vadSpeechStartTcs = tcs;
    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var reg = cts.Token.Register(() => tcs.TrySetResult(false));
        cts.CancelAfter(timeout);
        return await tcs.Task;
    }
    finally
    {
        _vadSpeechStartTcs = null;
    }
}

private async Task WaitForAvatarSpeechToFinishAsync(CancellationToken ct)
{
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var startedSpeaking = false;

    void OnSpeakingChanged(bool isSpeaking)
    {
        if (isSpeaking) { startedSpeaking = true; }
        else if (startedSpeaking) { tcs.TrySetResult(true); }
    }

    _avatarClient.OnSpeakingChanged += OnSpeakingChanged;
    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var reg = cts.Token.Register(() => tcs.TrySetResult(false));
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings.AvatarSpeechMaxWaitSeconds)));
        await tcs.Task;
    }
    finally
    {
        _avatarClient.OnSpeakingChanged -= OnSpeakingChanged;
    }
}

private void HandleVadSpeechStart()
{
    if (_recorder is not null && !_recorder.IsTurnActive)
    {
        _recorder.BeginTurnCapture();
    }
    _vadSpeechStartTcs?.TrySetResult(true);
}
```

**The bug:** `_vadSpeechStartTcs` is only non-null *while a `WaitForSpeechStartAsync` call is
in flight*. Between sending `question_start`/receiving a `decision` and the moment
`WaitForSpeechStartAsync` is actually called, there is now a gap — `WaitForAvatarSpeechToFinishAsync`
runs first and does **not** touch `_vadSpeechStartTcs` at all. Voice Live's server-side VAD
listens on the student's mic continuously (`MicAudioStreamer` streams from exam start to exam
end, independent of "turns"), so if the student starts answering quickly — easily possible for a
short/easy question like "What is your name?", especially right as the avatar's trailing audio
is still fading out (the speaking-detection hysteresis in `AvatarWebRtcClient.cs` adds ~300ms
before `OnSpeakingChanged(false)` fires) — `HandleVadSpeechStart` fires while `_vadSpeechStartTcs`
is still `null`. `_vadSpeechStartTcs?.TrySetResult(true)` is then a no-op: the signal is lost.

Consequence: `WaitForSpeechStartAsync` (which starts listening *after* this point, with a fresh,
now-empty `TaskCompletionSource`) waits out its full `initialTimeout`/`SilenceTimeoutAfterRepeatSeconds`
with no signal ever arriving (the real `vad_speech_start` already happened and won't repeat),
returns `spoke=false`, and the code **skips `WaitForSpeechEndWithGraceAsync` entirely** — meaning
WPF sends `turn_end` based on a stale/incomplete view of the turn, rather than properly waiting
for the student's speech to end. Exactly how that cascades into Python's `no_meaningful_speech`
signal (`current_turn_word_count == 0` in `signal_node_config.py`) firing despite the student
having actually spoken was not 100% pinned down with a live log this session (no fresh log was
captured after this hypothesis was formed) — but this race is real, confirmed by code reading, and
is the most direct, evidence-backed candidate. **Fix this first, then re-test live; if "asks the
same question again after I answered" still reproduces with this race closed, capture a fresh
server + WPF log side-by-side (cross-reference `exam_attempt_id` and timestamps, per the existing
note in project memory about this kind of symptom) and re-diagnose from there — don't assume this
one fix is sufficient without re-confirming live.**

### 1a fix: never let `_vadSpeechStartTcs` go unsubscribed between turns

Replace the two `await WaitForAvatarSpeechToFinishAsync(ct);` call sites with a combined wait that
subscribes to `vad_speech_start` *before* (and for the entire duration of) waiting on the avatar,
so a student who starts answering early is captured instead of lost. If the student starts first,
skip the avatar-wait outcome entirely and treat the turn as already started.

Rename the existing `WaitForAvatarSpeechToFinishAsync` body to a private "core" method (logic
unchanged), and add this wrapper:

```csharp
/// <summary>
/// Waits for the avatar to finish speaking, but listens for the student's vad_speech_start the
/// entire time (not just after this returns) so an early/overlapping response is never missed.
/// Returns true if the student already started speaking before the avatar-finished signal arrived
/// (the caller should treat the turn as already started and skip its own WaitForSpeechStartAsync
/// call); false if the avatar simply finished normally (the caller proceeds as before).
/// </summary>
private async Task<bool> WaitForAvatarToFinishOrStudentToStartAsync(CancellationToken ct)
{
    var speechStartTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    _vadSpeechStartTcs = speechStartTcs;
    try
    {
        var avatarFinishedTask = WaitForAvatarSpeechToFinishAsync(ct); // existing method, unchanged
        var winner = await Task.WhenAny(avatarFinishedTask, speechStartTcs.Task);
        return winner == speechStartTcs.Task;
    }
    finally
    {
        _vadSpeechStartTcs = null;
    }
}
```

Update `RunQuestionAsync` to thread the result through both call sites:

```csharp
var questionContext = BuildQuestionContext(question);
await _sessionClient.SendQuestionStartAsync(attemptAnswerId, questionContext, language: "en-US", ct);
var studentAlreadyStarted = await WaitForAvatarToFinishOrStudentToStartAsync(ct);

var turnOrder = 1;
var questionDone = false;

while (!questionDone && turnOrder <= maxTurnsPerQuestion)
{
    ct.ThrowIfCancellationRequested();
    OnStatusChanged?.Invoke($"Dang cho hoc sinh tra loi cau {prompt.QuestionNumber} (turn {turnOrder})...");

    var initialTimeout = TimeSpan.FromSeconds(Math.Max(3,
        turnOrder == 1 ? _settings.InitialSilenceTimeoutSeconds : _settings.SilenceTimeoutAfterRepeatSeconds));
    var overallTimeout = TimeSpan.FromSeconds(Math.Max(15, _settings.QuestionTurnTimeoutSeconds));
    var gracePeriod = TimeSpan.FromSeconds(Math.Max(1, _settings.PostSpeechSilenceGracePeriodSeconds));

    var spoke = studentAlreadyStarted || await WaitForSpeechStartAsync(initialTimeout, ct);
    studentAlreadyStarted = false; // only valid for the wait immediately following an avatar utterance
    if (spoke)
    {
        OnStatusChanged?.Invoke("Hoc sinh dang noi...");
        await WaitForSpeechEndWithGraceAsync(overallTimeout, gracePeriod, ct);
        OnStatusChanged?.Invoke("Hoc sinh da dung noi, dang xu ly...");
    }

    var pcmBytes = _recorder!.GetTurnBufferAndReset();
    if (pcmBytes.Length > 0)
    {
        DispatchArchiveTurn(question, attemptAnswerId, turnOrder, prompt.QuestionText, pcmBytes, ct);
    }

    var decision = await _sessionClient.SendTurnEndAndWaitAsync(ct);
    _sessionClient.SetResumeCheckpoint(attemptAnswerId, turnOrder);
    LocalFileLogger.Info("exam_flow", "decision_received", new
    {
        prompt.QuestionNumber,
        turnOrder,
        decision.ShouldContinue,
        decision.Reason
    });

    if (!string.IsNullOrWhiteSpace(decision.NextPromptText))
    {
        OnTranscriptAppended?.Invoke($"AI: {decision.NextPromptText}");
    }

    studentAlreadyStarted = await WaitForAvatarToFinishOrStudentToStartAsync(ct);

    questionDone = !decision.ShouldContinue;
    turnOrder++;
}
```

(`WaitForAvatarSpeechToFinishAsync` itself — the body that listens to `_avatarClient.OnSpeakingChanged`
— does not need to change at all; only its two call sites change, replaced by the new wrapper.)

### 1b. Soften the wording, and split "didn't hear you" from "you asked to repeat"

File: `agents/src/node/followUpDecisionGraph/FollowUpNode/followup_decision_node_config.py`

Current relevant code:

```python
_QUESTION_REPEAT_PREFIX = "Sure, I'll repeat the question once."

...

def _build_question_repeat_prompt(question: QuestionContext | Dict[str, Any] | None, fallback_prompt: str | None) -> str:
    question_text = str(_question_attr(question, "question_text") or fallback_prompt or "").strip()
    if not question_text:
        return "Sure, I'll repeat the question once."
    return f"{_QUESTION_REPEAT_PREFIX} {question_text}"

...

def _handled_edge_case_decision(state: Dict[str, Any]) -> Dict[str, Any] | None:
    ...
    if _is_repeat_request(transcript):
        if repeated_once:
            return {
                **_state_without_turns(state),
                "status": "completed",
                "decision": {
                    "should_continue": True,
                    "next_prompt_text": "I've repeated the question once. Please answer as best you can.",
                    "reason": "repeat_question_already_used",
                },
            }
        return {
            **_state_without_turns(state),
            "status": "completed",
            "decision": {
                "should_continue": True,
                "next_prompt_text": _build_question_repeat_prompt(question, current_turn.get("prompt_text")),
                "reason": "repeat_question_requested",
            },
        }

    if signals.get("no_meaningful_speech"):
        if repeated_once:
            next_prompt_text = "Take your time. Please answer when you're ready."
            reason = "no_meaningful_speech_after_repeat"
        else:
            next_prompt_text = _build_question_repeat_prompt(question, current_turn.get("prompt_text"))
            reason = "no_meaningful_speech"
        return {
            **_state_without_turns(state),
            "status": "completed",
            "decision": {
                "should_continue": True,
                "next_prompt_text": next_prompt_text,
                "reason": reason,
            },
        }
    ...
```

**The user's ask, precisely:** these are two different situations and should sound different:
- System genuinely didn't hear/catch anything (`no_meaningful_speech`) → acknowledge that softly
  (e.g. "maybe I didn't hear that clearly"), not "Sure, I'll repeat the question."
- Student explicitly asks to repeat (`_is_repeat_request` matched, e.g. "can you repeat that?",
  "I didn't catch the question") → soft acknowledgement + repeat **exactly once**, **spoken
  slowly** (see Section 1c for the "slowly" part — that's a TTS change, not just wording).

Change to:

```python
_REPEAT_QUESTION_PREFIX = "No problem, let me say that again:"
_NO_SPEECH_PREFIX = "Sorry, I may not have caught that clearly."

...

def _build_question_repeat_prompt(question: QuestionContext | Dict[str, Any] | None, fallback_prompt: str | None) -> str:
    question_text = str(_question_attr(question, "question_text") or fallback_prompt or "").strip()
    if not question_text:
        return _REPEAT_QUESTION_PREFIX
    return f"{_REPEAT_QUESTION_PREFIX} {question_text}"


def _build_no_speech_reprompt(question: QuestionContext | Dict[str, Any] | None, fallback_prompt: str | None) -> str:
    question_text = str(_question_attr(question, "question_text") or fallback_prompt or "").strip()
    if not question_text:
        return _NO_SPEECH_PREFIX
    return f"{_NO_SPEECH_PREFIX} {question_text}"
```

And in `_handled_edge_case_decision`, change the `no_meaningful_speech` branch to call the new
helper instead of reusing `_build_question_repeat_prompt`:

```python
    if signals.get("no_meaningful_speech"):
        if repeated_once:
            next_prompt_text = "Take your time. Please answer when you're ready."
            reason = "no_meaningful_speech_after_repeat"
        else:
            next_prompt_text = _build_no_speech_reprompt(question, current_turn.get("prompt_text"))
            reason = "no_meaningful_speech"
```

(Leave the `_is_repeat_request` branch calling `_build_question_repeat_prompt` as-is — that's the
"student explicitly asked" path, which keeps using `_REPEAT_QUESTION_PREFIX`.) Optionally also
soften `"I've repeated the question once. Please answer as best you can."` if it reads stiffly
too (the user didn't call this one out specifically — judgment call, low priority).

**Important: `_handle_turn_end` in `attempt_connection.py` needs to know which case this was**, so
it can request slow speech only for the explicit-repeat-request case (Section 1c) — the `reason`
field already distinguishes them (`"repeat_question_requested"` vs `"no_meaningful_speech"`), so
no new field is needed; just read `decision["reason"]` where noted below.

### 1c. Speak the explicit repeat slowly (SSML), exactly once

"Exactly once" is **already enforced** by the existing `_has_used_repeat_once`/`repeated_once`
check in `followup_decision_node_config.py` — no change needed there. This section is only about
making that one repeat **sound** slower.

File: `agents/src/realtime/tts_client.py` — current:

```python
def _build_synthesizer(voice_name: Optional[str]) -> speechsdk.SpeechSynthesizer:
    load_root_dotenv()
    speech_key = os.getenv("AZURE_SPEECH_KEY")
    speech_region = os.getenv("AZURE_SPEECH_REGION")
    if not speech_key or not speech_region:
        raise RuntimeError("Missing AZURE_SPEECH_KEY/AZURE_SPEECH_REGION in environment variables")

    speech_config = speechsdk.SpeechConfig(subscription=speech_key, region=speech_region)
    speech_config.set_speech_synthesis_output_format(_OUTPUT_FORMAT)
    speech_config.speech_synthesis_voice_name = voice_name or os.getenv("AZURE_TTS_VOICE", "en-US-JennyNeural")
    return speechsdk.SpeechSynthesizer(speech_config=speech_config, audio_config=None)


def synthesize_to_wav(text: str, out_wav_path: Path, *, voice_name: Optional[str] = None) -> Path:
    out_wav_path.parent.mkdir(parents=True, exist_ok=True)
    synthesizer = _build_synthesizer(voice_name)
    result = synthesizer.speak_text_async(text).get()
    ...


async def synthesize_to_wav_async(text: str, out_wav_path: Path, *, voice_name: Optional[str] = None) -> Path:
    return await asyncio.to_thread(synthesize_to_wav, text, out_wav_path, voice_name=voice_name)
```

Add an optional `rate` parameter; when present, synthesize via SSML (`<prosody rate="...">`)
instead of plain text. Azure's SSML `prosody rate` accepts relative percentages like `"-20%"`.
Needs the resolved voice name available outside `_build_synthesizer` to build the `<voice>` tag —
refactor `_build_synthesizer` to also return the resolved voice name:

```python
import xml.sax.saxutils

def _build_synthesizer(voice_name: Optional[str]) -> tuple[speechsdk.SpeechSynthesizer, str]:
    load_root_dotenv()
    speech_key = os.getenv("AZURE_SPEECH_KEY")
    speech_region = os.getenv("AZURE_SPEECH_REGION")
    if not speech_key or not speech_region:
        raise RuntimeError("Missing AZURE_SPEECH_KEY/AZURE_SPEECH_REGION in environment variables")

    resolved_voice = voice_name or os.getenv("AZURE_TTS_VOICE", "en-US-JennyNeural")
    speech_config = speechsdk.SpeechConfig(subscription=speech_key, region=speech_region)
    speech_config.set_speech_synthesis_output_format(_OUTPUT_FORMAT)
    speech_config.speech_synthesis_voice_name = resolved_voice
    return speechsdk.SpeechSynthesizer(speech_config=speech_config, audio_config=None), resolved_voice


def _build_ssml(text: str, voice_name: str, rate: str) -> str:
    escaped_text = xml.sax.saxutils.escape(text)
    return (
        '<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="en-US">'
        f'<voice name="{voice_name}"><prosody rate="{rate}">{escaped_text}</prosody></voice>'
        "</speak>"
    )


def synthesize_to_wav(
    text: str, out_wav_path: Path, *, voice_name: Optional[str] = None, rate: Optional[str] = None
) -> Path:
    out_wav_path.parent.mkdir(parents=True, exist_ok=True)
    synthesizer, resolved_voice = _build_synthesizer(voice_name)
    if rate:
        result = synthesizer.speak_ssml_async(_build_ssml(text, resolved_voice, rate)).get()
    else:
        result = synthesizer.speak_text_async(text).get()

    if result.reason != speechsdk.ResultReason.SynthesizingAudioCompleted:
        cancellation = result.cancellation_details
        raise RuntimeError(
            f"Azure TTS synthesis failed: reason={result.reason} "
            f"details={cancellation.error_details if cancellation else None}"
        )

    out_wav_path.write_bytes(result.audio_data)
    logger.info("[tts_client] synthesized %d bytes -> %s", len(result.audio_data), out_wav_path)
    return out_wav_path


async def synthesize_to_wav_async(
    text: str, out_wav_path: Path, *, voice_name: Optional[str] = None, rate: Optional[str] = None
) -> Path:
    return await asyncio.to_thread(synthesize_to_wav, text, out_wav_path, voice_name=voice_name, rate=rate)
```

File: `agents/src/realtime/avatar_speech.py` — thread `rate` through `speak()`:

```python
async def speak(exam_attempt_id: str, text: str, *, sequence: int, rate: Optional[str] = None) -> None:
    ...
    async with _get_lock(exam_attempt_id):
        try:
            ...
            await tts_client.synthesize_to_wav_async(text, wav_path, rate=rate)
            ...
```

(`from typing import Optional` already needs importing there if not already present — check.)

File: `agents/src/realtime/attempt_connection.py` — thread a `slow` flag through `_speak`, and set
it only for the `repeat_question_requested` reason:

```python
    def _speak(self, text: Optional[str], *, slow: bool = False) -> None:
        if not text:
            return
        self._utterance_sequence += 1
        asyncio.create_task(
            avatar_speech.speak(
                self.exam_attempt_id, text, sequence=self._utterance_sequence,
                rate="-20%" if slow else None,
            )
        )
```

And in `_handle_turn_end`, after computing `decision`:

```python
        next_prompt_text = decision.get("next_prompt_text") or (
            None if decision.get("should_continue") else CLOSING_REPLY
        )
        self._speak(next_prompt_text, slow=decision.get("reason") == "repeat_question_requested")
```

(`_handle_question_start`'s `self._speak(spoken_text)` call stays normal-speed — slow speech only
applies to the explicit repeat-the-question case.)

**Verify:** run a real TTS call with `rate="-20%"` through `synthesize_to_wav` once implemented
and listen to the output — confirm it's noticeably slower without sounding broken/garbled. `-20%`
is a starting guess, not a measured-good value; tune if it sounds off.

## Section 2 — Camera flicker/glow synced to the student's own speaking

Mirrors the avatar's existing speaking-ripple indicator (`AvatarVideoHost.xaml`'s `IsSpeaking`
dependency property, driven by `AvatarWebRtcClient.OnSpeakingChanged`), but for the *student's*
side, driven by the **already-existing, server-confirmed VAD signal** (`HandleVadSpeechStart`/
`HandleVadSpeechEnd` in `RealtimeExamFlowService.cs`) rather than a new amplitude detector — no
need to reinvent speech detection, Voice Live's VAD is already wired end-to-end and reliable.

### 2a. Expose a new event on `IExamFlowService`

File: `DesktopApp/VoxOralExam/VoxOralExam.Core/Interfaces/IExamFlowService.cs` — current:

```csharp
public interface IExamFlowService
{
    event Action<ExamQuestionPrompt>? OnQuestionPresented;
    event Action<string>? OnTranscriptAppended;
    event Action<string>? OnStatusChanged;
    event Action? OnExamCompleted;

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
```

Add one more event:

```csharp
public interface IExamFlowService
{
    event Action<ExamQuestionPrompt>? OnQuestionPresented;
    event Action<string>? OnTranscriptAppended;
    event Action<string>? OnStatusChanged;
    event Action? OnExamCompleted;
    event Action<bool>? OnStudentSpeakingChanged;

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
```

### 2b. Implement it in `RealtimeExamFlowService.cs`

Add the event, raise it from the existing VAD handlers, and reset it to `false` on cleanup so the
UI never gets stuck mid-glow if the exam ends or reconnects mid-utterance:

```csharp
public event Action<bool>? OnStudentSpeakingChanged;

...

private void HandleVadSpeechStart()
{
    if (_recorder is not null && !_recorder.IsTurnActive)
    {
        _recorder.BeginTurnCapture();
    }
    _vadSpeechStartTcs?.TrySetResult(true);
    OnStudentSpeakingChanged?.Invoke(true);
}

private void HandleVadSpeechEnd()
{
    _vadSpeechEndTcs?.TrySetResult(true);
    OnStudentSpeakingChanged?.Invoke(false);
}
```

In `RunAsync`'s `finally` block (where `_sessionClient.OnVadSpeechStart -= HandleVadSpeechStart;`
etc. already unsubscribe), add one line to force the UI glow off on exam end:

```csharp
finally
{
    _sessionClient.OnVadSpeechStart -= HandleVadSpeechStart;
    _sessionClient.OnVadSpeechEnd -= HandleVadSpeechEnd;
    _sessionClient.OnError -= HandleSessionError;
    _sessionClient.OnReconnected -= HandleSessionReconnected;
    OnStudentSpeakingChanged?.Invoke(false);
    _micStreamer.Stop();
    ...
}
```

### 2c. Wire into `ExamViewModel.cs`

Same pattern already used for `AvatarVideoFrame`/`IsAvatarSpeaking` (see `HandleAvatarSpeakingChanged`
in the same file for the exact pattern to copy):

```csharp
private bool _isStudentSpeaking;
...
public bool IsStudentSpeaking
{
    get => _isStudentSpeaking;
    set => SetProperty(ref _isStudentSpeaking, value);
}
```

Subscribe in the constructor (next to the other `_examFlow.On...` subscriptions):

```csharp
_examFlow.OnStudentSpeakingChanged += HandleStudentSpeakingChanged;
```

Unsubscribe in `CleanupCoreAsync` (next to the other `_examFlow.On... -=` lines):

```csharp
_examFlow.OnStudentSpeakingChanged -= HandleStudentSpeakingChanged;
```

Handler (next to `HandleAvatarSpeakingChanged`):

```csharp
private void HandleStudentSpeakingChanged(bool isSpeaking)
{
    Application.Current.Dispatcher.Invoke(() => IsStudentSpeaking = isSpeaking);
}
```

### 2d. Add the glow effect in `ExamWindow.xaml`

The camera preview lives in `Views/ExamWindow.xaml`, inside `<Border x:Name="CameraPreviewBorder">`
→ `<Grid Grid.Row="1" ClipToBounds="True">` (the grid that already hosts the `Image
Source="{Binding CameraPreview}"` and the "Camera off" overlay). Add a pulsing glow border as a
sibling inside that same `Grid`, pure XAML (no code-behind needed — `DataTrigger.EnterActions`/
`ExitActions` with a `BeginStoryboard`/`StopStoryboard` handles starting/stopping the pulse):

```xml
<Border BorderBrush="#22C55E"
        BorderThickness="4"
        CornerRadius="10"
        Opacity="0"
        IsHitTestVisible="False">
    <Border.Style>
        <Style TargetType="Border">
            <Style.Triggers>
                <DataTrigger Binding="{Binding IsStudentSpeaking}" Value="True">
                    <DataTrigger.EnterActions>
                        <BeginStoryboard x:Name="StudentSpeakingGlowStoryboard">
                            <Storyboard RepeatBehavior="Forever">
                                <DoubleAnimation Storyboard.TargetProperty="Opacity"
                                                  From="0.25" To="0.9" Duration="0:0:0.35"
                                                  AutoReverse="True" />
                            </Storyboard>
                        </BeginStoryboard>
                    </DataTrigger.EnterActions>
                    <DataTrigger.ExitActions>
                        <StopStoryboard BeginStoryboardName="StudentSpeakingGlowStoryboard" />
                    </DataTrigger.ExitActions>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </Border.Style>
</Border>
```

Place it after the `Image` (and after/before the "Camera off" `StackPanel`, doesn't matter much —
it's `Opacity="0"` and hit-test-invisible when not speaking, so it won't visually interfere
either way). No new dependency property needed on any control — this binds straight to the
existing `ExamViewModel.IsStudentSpeaking` via the window's normal DataContext.

## Verification checklist

- [ ] `dotnet build VoxOralExam.DesktopApp/VoxOralExam.DesktopApp.csproj` — 0 errors.
- [ ] Python: `uv run python -c "import sys; sys.path.insert(0,'src'); import importlib; importlib.import_module('app')"` from `agents/` — imports cleanly.
- [ ] Run a real `tts_client.synthesize_to_wav(..., rate="-20%")` call and listen to the output WAV — confirm it sounds slower, not broken.
- [ ] Live exam run: ask a short question (e.g. "What is your name?"), answer immediately/quickly — confirm it does **not** re-ask the same question.
- [ ] Live exam run: deliberately stay silent — confirm the reprompt says something like "Sorry, I may not have caught that clearly..." (not "Sure, I'll repeat...").
- [ ] Live exam run: say "can you repeat the question?" — confirm it says something like "No problem, let me say that again:" followed by the question spoken noticeably slower than normal.
- [ ] Live exam run: while answering, confirm the camera preview border pulses/glows in sync with `vad_speech_start`/`vad_speech_end` (it will lag slightly behind the literal first millisecond of speech — that's expected, it's server-VAD-driven, not local amplitude).
- [ ] If the repeat-after-answering bug still reproduces after the Section 1a fix, capture a fresh server (`uv run uvicorn app:app ...`) log **and** WPF's `desktopapp.jsonl` log from the same run, cross-reference by `exam_attempt_id` and timestamp, and re-diagnose — don't assume Section 1a alone is sufficient without a live re-test.
