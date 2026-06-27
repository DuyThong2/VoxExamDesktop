# Single decision source: Tavus decides, WPF only archives

✅ Implemented (S3 direct upload, `/turns/archive`, tool-call signal, correlation key below) and
verified building. **Updated since:** the conversation lifecycle this doc assumes ("one Tavus
conversation per question") is no longer current — switched to one conversation for the whole
exam via `conversation.overwrite_llm_context` (UX flicker from recreating per question). See
`ai-examiner-plan.md`'s "Why one conversation per exam" section. Doesn't change anything in this
doc's archive/tool-call/correlation-key design — `<answer_id>` still gets embedded the same way,
just via `OverwriteContextAsync` for questions 2+ instead of a fresh `CreateConversationAsync`.

## Context

Supersedes the "Lệnh 3" (upload + call `/evaluate/turn` for a decision) and "Lệnh 4" (loop control
via `ShouldContinue`/`ReachedMaxTurns`) sections of `ai-examiner-plan.md`. Two problems with that
design, found after it was already implemented and tested against real Tavus docs:

1. **Two independent decision-makers.** Tavus's own live `POST /v1/chat/completions` call (its own
   STT, drives what the avatar actually says) and WPF's separate `POST /evaluate/turn` call
   (Python re-transcribes via Azure STT, decides again) could disagree — no way to know which
   decision is the "real" one. Confirmed real risk, not hypothetical.
2. **Java in the S3 upload path adds a dependency that doesn't exist yet** (`vox` has no
   `upload-audio` controller) and isn't needed for this — WPF can push to S3 directly.

**Decision:** Tavus's live call is the **only** place a follow-up/done decision is made. WPF's
job shrinks to: capture turn audio, push it to S3 itself, and tell Python about it for
archival/Kafka-relay purposes only — WPF never asks Python "should we continue?" a second time.
WPF learns when a question is actually done via a **tool call** Tavus relays from that same live
decision, not by re-deciding or by guessing from spoken text.

Confirmed against Tavus's own docs (not guessed) that this is safe: Full Pipeline mode (what's
already configured) keeps VAD/STT/turn-detection active — switching to echo-mode instead (the
other option considered, where WPF drives Tavus's speech directly) would have **disabled** STT/
VAD, per Tavus's docs ("not recommended if you plan to use the perception or speech recognition
layers, as it is incompatible with them"). So this plan keeps Full Pipeline mode; nothing about
how the conversation is created changes.

## What changes vs. the current implementation

| Today (`TavusFullPipelineExamFlowService.cs`) | This plan |
|---|---|
| `UploadTurnAudioAsync` → `POST {JavaBaseUrl}/api/v1/exam-turns/upload-audio` | `UploadTurnAudioAsync` → push directly to S3 (AWS SDK, no Java) |
| `EvaluateTurnAsync` → `POST {PythonBaseUrl}/evaluate/turn`, decision used for loop control | `ArchiveTurnAsync` (new) → `POST {PythonBaseUrl}/turns/archive` (new endpoint, see `agents/docs/single-decision-source-plan.md`), response carries no decision, fire-and-forget for loop purposes |
| Loop exits when `result.ShouldContinue == false \|\| result.ReachedMaxTurns` | Loop exits when a `conversation.tool_call`/`conversation.toolcall` app-message named `end_question` arrives from Tavus (exact event name TBD — see Open items) |
| `BuildConversationalContext` embeds only `<question_context>` | Also embeds an answer/attempt identifier so Python's live decision endpoint and the new archive endpoint can correlate the same question's turns — see "Correlation key" below |

## Correlation key: how the two Python endpoints talk about the same question

`/turns/archive` (WPF calls this, has `attemptAnswerId` directly) and `/v1/chat/completions`
(Tavus calls this, only gets whatever WPF put in `conversational_context`) need to agree on the
same key to find each other's data on Python's side. Extend `BuildConversationalContext` to also
embed the answer id:

```
<question_context>{...as today...}</question_context>
<answer_id>11111111-1001-1001-1001-111111111111</answer_id>
```

(Same `attemptAnswerId` already used for `UploadTurnAudioAsync`/`ArchiveTurnAsync` — just also
surfaced to Tavus's relayed system message so the live-decision side can read it too.)

## Lệnh 1 — `Services/TavusFullPipelineExamFlowService.cs` / new S3 client: direct upload

Replace `UploadTurnAudioAsync`'s Java multipart call with a direct S3 `PutObject` using
`AWSSDK.S3` (already referenced in `VoxOralExam.DesktopApp.csproj`, currently unused — this plan
finally uses it). Needs real AWS credentials configured somewhere reachable by WPF (env vars,
`appsettings.json`, or an AWS profile) — `AppSettings.S3BucketName`/`S3Region` already exist but
there's no `AccessKey`/`SecretKey` field yet; add what's needed once it's decided how credentials
will actually be supplied (not a code question, a "where do these come from" question — ask the
user, don't invent a default credential source). Key the object path by
`{attemptAnswerId}/turn-{turnOrder}.wav`. Return the resulting object URL the same way
`UploadTurnAudioAsync` does today (just swap the HTTP call for an S3 SDK call).

## Lệnh 2 — `Services/TavusFullPipelineExamFlowService.cs`: replace the decision call with an archive call

Rename/replace `EvaluateTurnAsync`/`BuildEvaluateTurnRequest` with `ArchiveTurnAsync`:

```csharp
private async Task ArchiveTurnAsync(EvaluateTurnRequest request, CancellationToken ct)
{
    // same multipart POST shape as today's EvaluateTurnAsync, just to
    // {PythonBaseUrl}/turns/archive instead of /evaluate/turn, and the response
    // is not used for any decision — fire-and-forget from the loop's perspective
    // (still await it so failures surface, just don't branch on the result).
}
```

Reuse the existing `EvaluateTurnRequest`/`QuestionContextDto` shape as-is (it's already correct —
see `external-api-contracts.md` §3) — only the target path and what the loop does with the
response change.

## Lệnh 3 — `Controls/TavusConversationHost.xaml.cs`: relay the tool-call event

Add a third category of relayed event alongside utterance/speaking events. Tavus's tool-calling
docs (`docs.tavus.io/sections/conversational-video-interface/persona/llm-tool`) say "Tavus does
not execute tool calls on the backend... use event listeners in your frontend to listen for tool
call events" — exactly this bridge. Event name seen in two different forms across Tavus's own
docs (`conversation.tool_call` in one place, `conversation.toolcall` in the interactions-protocol
overview) — **check both** until confirmed live:

```js
if (eventType === 'conversation.tool_call' || eventType === 'conversation.toolcall') {
  const toolName = data.properties?.name || data.name || '';
  if (toolName === 'end_question') {
    postMessage({ type: 'question-ended' });
  }
}
```

```csharp
public event Action? OnQuestionEnded;
// in HandleWebMessageReceived:
case "question-ended":
    OnQuestionEnded?.Invoke();
    break;
```

## Lệnh 4 — `Services/TavusFullPipelineExamFlowService.cs`: loop control via the tool-call signal

Replace the `while (!questionDone && turnOrder <= maxTurnsPerQuestion)` exit condition (currently
`!result.ShouldContinue || result.ReachedMaxTurns`) with waiting on `OnQuestionEnded`, racing
against the existing `maxTurnsPerQuestion`/timeout safety net (keep that net — Tavus could fail
to call the tool, same as any other external dependency):

```csharp
private TaskCompletionSource<bool>? _questionEndedTcs;

private void HandleQuestionEnded()
{
    _questionEndedTcs?.TrySetResult(true);
}
```

Wire/unwire `OnQuestionEnded` the same way `OnUserStartedSpeaking`/`OnUserStoppedSpeaking` are
wired today. Each turn iteration still does: wait for user turn → upload S3 → `ArchiveTurnAsync`
→ but now *also* race a short wait on `_questionEndedTcs` (the tool call could arrive any time
after the avatar finishes speaking its reply, not necessarily lined up with the archive call) —
when it fires, exit the loop and move to the next question.

## Out of scope for this pass

- Persisting the full `AnswerTurn` to Java — still Python's job via Kafka, see
  `agents/docs/single-decision-source-plan.md`. WPF doesn't change what it sends for this beyond
  the correlation key above.
- Deciding where AWS credentials for direct S3 upload come from — flagged above as a question for
  the user, not assumed.

## Verification

- `dotnet build`.
- Manual run: confirm WPF uploads land in S3 directly (no Java in the path at all), confirm
  `/turns/archive` gets called every turn but never influences the loop, confirm the loop only
  ever exits on the tool-call signal or the timeout safety net — never on a WPF-computed decision.
