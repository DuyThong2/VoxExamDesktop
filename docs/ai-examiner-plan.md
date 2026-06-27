# AI Examiner: Tavus Full Pipeline, one conversation for the whole exam

⚠️ **Superseded multiple times — read in this order:**
1. "One conversation per question" (originally below) is **no longer current** — switched to
   one conversation for the whole exam (recreating per question caused a visible
   disconnect/reconnect flicker each question). See "Why one conversation per exam" below and
   `single-decision-source-plan.md`'s "Correlation key" section for `conversation.overwrite_llm_context`.
2. "Lệnh 3" (upload + `/evaluate/turn`) and "Lệnh 4" (loop control via
   `ShouldContinue`/`ReachedMaxTurns`) further below are superseded by
   `docs/single-decision-source-plan.md` — calling both Tavus's live decision and WPF's own
   `/evaluate/turn` decision created two independent, possibly disagreeing decisions.
3. Java upload-audio (Lệnh 3a as originally written) is also superseded — WPF pushes to S3
   directly now (`TavusFullPipelineExamFlowService.UploadTurnAudioAsync`, AWS SDK), no Java in
   that path at all.

Net effect: the only parts of this file still literally accurate are Lệnh 1/2 (JS bridge
user-speaking relay, turn audio recorder) and the general Full Pipeline framing in Context below.
Everything about conversation lifecycle and persistence has moved to `single-decision-source-plan.md`.

## Context

This supersedes the previous version of this file (echo/TTS-only Tavus persona + WPF-side VAD
+ a single `ShouldFollowupUrl` decision call). That architecture was abandoned. Current,
decided architecture:

- Tavus runs in **Full Pipeline** mode (not echo-only): Tavus does its own STT and, on every
  user turn, calls a Python custom-LLM endpoint (`POST /v1/chat/completions`, see
  `agents/docs/tavus-full-pipeline-plan.md` in the `agents` repo) which decides whether to ask a
  follow-up or close out the question. Tavus's replica speaks that reply autonomously — WPF does
  **not** drive question/follow-up speech via `SpeakAsync`/echo anymore.
- **One Tavus conversation for the whole exam.** WPF creates it once (question 1's context),
  then for each subsequent question sends `conversation.overwrite_llm_context` (see
  `TavusConversationHost.OverwriteContextAsync`) instead of recreating the conversation — see
  "Why one conversation per exam" below.
- WPF pushes each turn's WAV directly to S3 (AWS SDK) and calls Python's `/turns/archive`
  (archival only, no decision) — see `single-decision-source-plan.md`. No Java anywhere in the
  per-turn path.
- Full wire-level request/response shapes for every external call (Tavus, Python) are
  documented in **`docs/external-api-contracts.md`** — this file is the orchestration/build plan;
  that file is the contract reference. Don't duplicate field tables here, link to it.
- The mock `Question` data (`MockExamDataFactory.cs`) that `BuildConversationalContext` reads
  from currently has structural/value bugs relative to Java's real `Question` entity (invented
  `question_type` strings, `Type` missing from the `Question` model entirely) — see
  **`docs/question-model-alignment.md`** for that fix. Apply that one before relying on
  `BuildConversationalContext`'s `question_type` output for anything.

### Why one conversation per exam (was: one per question)

Originally chose one conversation **per question** specifically to avoid needing to update
`conversational_context` mid-conversation. That worked, but recreating the Tavus conversation
between every question caused a visible join/leave flicker each time — bad UX for something the
student sits through 5+ times. Found that Tavus actually supports
`conversation.overwrite_llm_context` (replaces the conversational_context used to generate future
replies, without ending the call) — see `single-decision-source-plan.md`. Switched to one
conversation for the whole exam, using that event between questions instead. The one thing this
reintroduces: Tavus's relayed `messages` history to `/v1/chat/completions` now spans the whole
exam, not just one question — `agents/src/mappers/chat_completion_mapper.py` had to be taught to
scope turn-counting to whatever comes after the *most recent* system message, not the whole
history, or turn_order/turns would bleed across questions. Already fixed there.

Still considered and rejected earlier: have the Tavus persona itself "know" all questions and walk
through them autonomously — not controllable/observable from WPF, and the Python side still has
no concept of "next question" today. Recreating the conversation per question requires **zero**
new Tavus or Python capability — `CreateConversationAsync`/`JoinAsync`/`EndConversationAsync`/
`BuildConversationalContext` already exist and already work per-conversation; this plan just
calls that same sequence once per `Question` instead of once per exam.

## Already built — don't redo this

| Piece | File | State |
|---|---|---|
| Tavus REST client | `Services/TavusService.cs` | done: `POST /v2/conversations`, `DELETE /v2/conversations/{id}` |
| Conversation request DTO | `VoxOralExam.Core/Dtos/TavusConversationRequest.cs` | done, includes `ConversationalContext` (`conversational_context`) |
| Conversation response DTO | `VoxOralExam.Core/Dtos/StartSessionResponse.cs` | done |
| WebView2 + Daily JS bridge | `Controls/TavusConversationHost.xaml(.cs)` | partially done — see Lệnh 1 below for what's missing |
| `<question_context>` marker builder | `TavusFullPipelineExamFlowService.BuildConversationalContext` | done, builds the exact snake_case JSON Python's `QuestionContext` expects (see `external-api-contracts.md` §1) |
| Per-question metadata on the WPF side | `ExamSessionState.QuestionTypesByQuestionId` / `.DifficultyLevelsByQuestionId` / `.EvaluationGuidesByQuestionId`, populated by `LoadMockExam` from `MockExamDataFactory` | done |
| Question/answer identity | `ExamSessionState.AttemptAnswerIdsByQuestionId` (one GUID per question, stable across all turns of that question) | done |

**Deleted in an earlier pass, not yet rebuilt** (this plan rebuilds the pieces actually still
needed): `LocalFallbackExamFlowService`, `ExamFlowService` router, `IExamLogService`/
`ExamLogService`, `IInterviewApiService`/`InterviewApiService`, `IVADService`/`VADService`,
`IAudioRecorderService`/`AudioRecorderService`, `ShouldFollowupRequest`/`Response`,
`EvaluateTurnRequest`. Do **not** blindly restore all of these — see below for exactly what's
back in scope and what isn't.

## What's in scope for this pass

1. Relay "user started/stopped speaking" from the Daily/Tavus conversation into C#.
2. Capture each turn's mic audio (WPF-side, NAudio) bounded by those events.
3. Upload each turn's WAV to S3 via the Java backend, tagged with the right `attemptAnswerId` /
   `turnOrder`.
4. Call Python's `/evaluate/turn` with that turn's `audioUrl` — **this is the real persistence
   path**, not a separate/optional decision call. Python re-transcribes the uploaded audio,
   decides follow-up-or-done, and — critically — is the one that relays the turn to Java (via a
   Kafka event, see `external-api-contracts.md` §2) once a question is finished. There is no
   direct WPF → Java call to persist `AnswerTurn`; don't add one.
5. Restructure `TavusFullPipelineExamFlowService` into a loop that creates one Tavus conversation
   per question, runs it until that question is done (driven by the `/evaluate/turn` response,
   not by guessing from spoken text), then moves to the next.

**Explicitly out of scope for this pass** (not requested yet — don't add back): a direct WPF →
Java REST call to persist `AnswerTurn` (superseded by Python's Kafka relay — see Lệnh 3), VAD-based
turn detection (Tavus's own turn detection is the source of truth now), barge-in/interrupt
handling.

## Lệnh 1 — `Controls/TavusConversationHost.xaml.cs` + embedded JS: relay user speaking events

Today the embedded HTML's `app-message` listener (inside `wireEvents`, `BuildHtml()`) only
handles `conversation.utterance.final`, `conversation.replica_started_speaking`,
`conversation.replica_stopped_speaking`. Add two more branches:

```js
if (eventType === 'conversation.user_started_speaking') {
  postMessage({ type: 'user-started-speaking' });
}
if (eventType === 'conversation.user_stopped_speaking') {
  postMessage({ type: 'user-stopped-speaking' });
}
```

⚠️ **The exact `event_type` strings above are a best guess from the naming pattern of the
existing replica events — not yet confirmed against real Tavus/Daily docs or a live session.**
Before relying on this in a real run: join a real conversation, log every raw `app-message`
payload (e.g. temporarily `postMessage({ type: 'debug', raw: JSON.stringify(data) })` and dump it
in `HandleWebMessageReceived`), speak into the mic, and read back the actual `event_type` Tavus
sends. Fix the string literals once confirmed — this is isolated to this one file.

In `HandleWebMessageReceived`'s `switch`, add matching cases:

```csharp
case "user-started-speaking":
    OnUserStartedSpeaking?.Invoke();
    break;
case "user-stopped-speaking":
    OnUserStoppedSpeaking?.Invoke();
    break;
```

And two new public events on `TavusConversationHost`:

```csharp
public event Action? OnUserStartedSpeaking;
public event Action? OnUserStoppedSpeaking;
```

## Lệnh 2 — turn audio recorder

`NAudio` is already a referenced package (`VoxOralExam.DesktopApp.csproj`) — no new dependency.
Add a small concrete class, e.g. `Services/TurnAudioRecorder.cs` (no interface needed; nothing
else consumes this anymore now that `LocalFallback` is gone):

```csharp
public class TurnAudioRecorder
{
    public Task StartAsync(CancellationToken ct);   // opens NAudio.Wave.WaveInEvent, 16kHz mono 16-bit, shared mode
    public Task StopAsync();                         // closes the device
    public byte[] GetTurnBufferAndReset();            // returns accumulated PCM16 since last call, clears buffer
}
```

- Sample rate/format must match what `WaveFileWriter` will encode: 16kHz, mono, 16-bit PCM.
- "Shared mode" matters because WebView2/Daily is also holding the mic open for the live Tavus
  call at the same time — don't request exclusive access.
- Pre-roll/tail: buffer continuously while the recorder is running (don't start/stop the device
  itself per turn — that's slow and risks clipping). On `OnUserStartedSpeaking`, mark a
  "turn start" offset roughly 300-500ms before the event fired (i.e. keep a small rolling
  pre-buffer); on `OnUserStoppedSpeaking`, cut the turn buffer roughly 300-700ms after the event
  fires (delay the cut slightly, don't snapshot instantly) so the WAV doesn't clip the first/last
  word. A simple ring buffer of the last ~1s feeding into the turn buffer accomplishes this.
- `StartAsync` is called once per **question** (when its Tavus conversation joins), not once per
  turn — `GetTurnBufferAndReset()` is what demarcates individual turns.

## Lệnh 3 — upload to S3 via Java, then call Python's `/evaluate/turn`

Two calls per turn, sequential (the second needs the first's result) — bring both back, they
were deleted along with `LocalFallbackExamFlowService` but the shapes are still fully documented
in `external-api-contracts.md` §2-3 and were correct before deletion; restore them as-is.

**3a. Upload audio** — a method directly on `TavusFullPipelineExamFlowService` (or a small
`TurnUploadService` if you'd rather keep it separate/testable) is enough, no need for the full
old `IExamLogService` interface:

```csharp
Task<string> UploadTurnAudioAsync(byte[] wavBytes, Guid attemptAnswerId, int turnOrder, CancellationToken ct)
```

- `POST {AppSettings.JavaBaseUrl}/api/v1/exam-turns/upload-audio`, `multipart/form-data`:
  `attemptAnswerId` (string GUID), `turnOrder` (string int), `file` (`turn-{turnOrder}.wav`,
  `audio/wav`).
- Response: JSON `{ "audioUrl": string }`, with a fallback to treating a bare quoted-string body
  as the URL directly (mirrors the old `ExamLogService.UploadTurnAudioAsync` exactly — copy that
  implementation, it was correct).
- Attach the bearer token from `ExamSessionState.CurrentUser?.AccessToken` if present, same as
  every other Java call in this app.
- ⚠️ This Java endpoint does not exist server-side yet (`vox` has no matching controller as of
  this writing) — calling it will 404 until the Java side implements it. Build the WPF side
  correctly anyway; this is tracked as a Java-side gap, not a WPF bug.

**3b. Evaluate the turn** — restore `EvaluateTurnRequest`/`QuestionContextDto`
(`VoxOralExam.Core/Dtos/EvaluateTurnRequest.cs`) and `ShouldFollowupResponse`/`EvaluatedTurnDto`
(`VoxOralExam.Core/Dtos/ShouldFollowupResponse.cs`) exactly as documented in
`external-api-contracts.md` §3, and a method to call it:

```csharp
Task<ShouldFollowupResponse> EvaluateTurnAsync(EvaluateTurnRequest request, CancellationToken ct)
```

- `POST {AppSettings.PythonBaseUrl}/evaluate/turn`, `multipart/form-data` — exact fields in
  `external-api-contracts.md` §3 (`audio_ref` = the `audioUrl` from 3a, `answer_id` =
  `attemptAnswerId`, `turn_order`, `prompt_text`, `language`, `question` = the
  `QuestionContextDto` built the same way `BuildConversationalContext` already does, just
  re-used for this call instead of only for the Tavus system message).
- **This call is not optional/decision-only — it's the real persistence trigger.** Python relays
  to Java via Kafka once it decides the question is done (see `external-api-contracts.md` §2).
  Skipping this call means the turn's S3 audio exists but nothing downstream ever learns about
  it.
- Use the response's `ShouldContinue`/`ReachedMaxTurns` in the loop below to decide whether this
  question needs another turn or is finished — see Lệnh 4.

## Lệnh 4 — restructure `TavusFullPipelineExamFlowService.RunAsync` into a per-question loop

Replace the current body (which creates one conversation, joins, then
`await Task.Delay(Timeout.Infinite, ct)`) with:

```csharp
private async Task RunAsync(CancellationToken ct)
{
    _host = _hostRegistry.CurrentHost ?? throw new InvalidOperationException(...);
    EnsureSessionInitialized();
    WireHost(_host); // existing utterance/replica-speaking wiring, plus the two new user-speaking events

    var recorder = new TurnAudioRecorder();

    for (_sessionState.QuestionIndex = 0; _sessionState.QuestionIndex < _sessionState.Questions.Count; _sessionState.QuestionIndex++)
    {
        ct.ThrowIfCancellationRequested();

        var prompt = PresentCurrentQuestion(); // existing — raises OnQuestionPresented
        var question = _sessionState.CurrentQuestion!;
        var attemptAnswerId = _sessionState.AttemptAnswerIdsByQuestionId[question.Id];

        OnStatusChanged?.Invoke($"Dang tao phien Tavus cho cau {prompt.QuestionNumber}/{prompt.TotalQuestions}...");
        _conversation = await _tavusService.CreateConversationAsync(new TavusConversationRequest
        {
            PersonaId = _settings.TavusPersonaId,
            ReplicaId = _settings.TavusReplicaId,
            ConversationName = $"{_sessionState.ExamTitle} - Q{prompt.QuestionNumber}",
            MaxCallDurationSeconds = Math.Max(300, question.MaxResponseSeconds * (MaxTurnsPerQuestion + 1)),
            ConversationalContext = BuildConversationalContext(prompt) // existing method, unchanged
        }, ct);

        await _host.JoinAsync(_conversation.ConversationUrl);
        await recorder.StartAsync(ct);

        var turnOrder = 1;
        var questionDone = false;

        while (!questionDone && turnOrder <= MaxTurnsPerQuestion)
        {
            await WaitForUserTurnAsync(ct); // OnUserStartedSpeaking -> OnUserStoppedSpeaking, with a per-question timeout safety net

            var wavBytes = EncodeWav(recorder.GetTurnBufferAndReset()); // reuse EncodeWav from the old LocalFallbackExamFlowService — same NAudio WaveFileWriter approach
            var audioUrl = await UploadTurnAudioAsync(wavBytes, attemptAnswerId, turnOrder, ct);
            var evaluateRequest = BuildEvaluateTurnRequest(question, attemptAnswerId, turnOrder, /* prompt text actually spoken this turn */, audioUrl);
            var result = await EvaluateTurnAsync(evaluateRequest, ct);
            OnStatusChanged?.Invoke($"Da luu turn {turnOrder} cua cau {prompt.QuestionNumber}: {audioUrl}");

            questionDone = !result.ShouldContinue || result.ReachedMaxTurns;
            turnOrder++;
        }

        await recorder.StopAsync();
        await _host.LeaveAsync();
        try { await _tavusService.EndConversationAsync(_conversation.ConversationId, CancellationToken.None); } catch { }
    }

    OnStatusChanged?.Invoke("Da hoan thanh bai van dap.");
    OnExamCompleted?.Invoke();
}
```

Notes on the sketch above (fill in exact helper bodies — `WaitForUserTurnAsync`, `EncodeWav` —
following the same `TaskCompletionSource`/`Task.WhenAny` pattern the old, now-deleted
`LocalFallbackExamFlowService.WaitForTurnEndAsync` used, just swap the VAD events for the new
`OnUserStartedSpeaking`/`OnUserStoppedSpeaking`):

- Loop control now comes from `/evaluate/turn`'s response (`ShouldContinue`/`ReachedMaxTurns`),
  not from guessing at spoken text — this is strictly better than the closing-line text-matching
  approach considered earlier, since it's a structured signal already being computed for the
  Kafka-relay purpose anyway (Lệnh 3b). **But see the race-condition risk flagged in
  `external-api-contracts.md` §3**: this decision is computed *separately* from (and slower than)
  Tavus's own live `/v1/chat/completions` decision that's actually driving what the avatar says.
  The two can disagree — e.g. the avatar may already be mid-follow-up (because Tavus's live
  pipeline decided to continue) at the moment this slower call resolves `ShouldContinue=false`
  and the code above tears the conversation down. No mitigation designed yet; watch for this
  specifically in manual testing — if it happens often, the fix is more likely "wait for
  `OnReplicaStoppedSpeaking` before acting on `questionDone`" than redesigning the decision flow.
- `MaxTurnsPerQuestion` and a per-question wall-clock timeout (inside `WaitForUserTurnAsync`) are
  safety nets in case `/evaluate/turn` never resolves or a speaking event is dropped — add both
  as new `AppSettings` fields (e.g. `MaxTurnsPerQuestion` default 3, matching Python's own
  `MAX_TURNS` constant in `agents/src/node/followUpDecisionGraph/constants.py` — keep these two
  numbers in sync manually across repos) rather than hardcoding magic numbers.
- `_host` (the single `TavusConversationHost`/WebView2 control) is reused across all 5 questions
  — only the underlying Tavus *conversation* is recreated per question; don't rebuild/renavigate
  the WebView2 control itself between questions.

## Out of scope for this pass

- A direct WPF → Java REST call to persist `AnswerTurn` — superseded by Python's Kafka relay
  (Lệnh 3b); don't add `POST /api/v1/exam-turns` back.
- Fetching the real exam paper/questions from Java — `ExamSessionState` keeps being populated via
  `MockExamDataFactory` for now.
- Barge-in/interrupt (`InterruptAsync`) — not wired into this loop; Tavus's own turn detection is
  assumed to handle the AI not talking over the student under Full Pipeline.
- Making the Java upload-audio endpoint actually exist server-side — `vox`-side work, tracked in
  `external-api-contracts.md`.

## Verification

- `dotnet build` on `VoxOralExam.Core` and `VoxOralExam.DesktopApp` — must stay green.
- Confirm the real Tavus `app-message` `event_type` strings for user-started/stopped-speaking
  (Lệnh 1) before trusting turn boundaries in a live run.
- Manual run with a real Tavus persona in Full Pipeline mode pointed at a real (or locally
  stubbed) `/v1/chat/completions`: confirm a new Tavus conversation is created per question (5
  conversations over one exam, not 1), each question's `<question_context>` reflects that
  question (not question 1's, reused), each turn's WAV reaches the Java upload endpoint (or a
  local stub of it) tagged with the right `attemptAnswerId`/`turnOrder`, and the loop correctly
  advances to the next question once the closing line is detected.
