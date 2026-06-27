# External API contracts (WPF side ground truth)

This repo cannot see the `vox` (Java) or `agents` (Python) source. Treat this file as the
authoritative wire contract for every external call `VoxOralExam.DesktopApp`/`VoxOralExam.Core`
makes, verified against the actual server-side code as of this writing. If a server-side
contract changes, this file is what's out of date — not the C# code.

Active runtime mode: `TavusFullPipelineExamFlowService` is the only `IExamFlowService` now
(`LocalFallbackExamFlowService`/the `ExamFlowService` router were deleted). It creates one Tavus
conversation per exam question, captures each turn's audio itself, and calls both Java (S3
upload) and Python (`/evaluate/turn`) per turn — see `docs/ai-examiner-plan.md`.

## 1. Tavus (tavusapi.com)

Auth: header `x-api-key: {AppSettings.TavusApiKey}`. Client: `TavusService.cs` implementing
`ITavusService`. Field names below verified against Tavus's own docs
([Create Conversation](https://docs.tavus.io/api-reference/conversations/create-conversation),
[Interactions Protocol overview](https://docs.tavus.io/sections/conversational-video-interface/interactions-protocols/overview))
— not guessed.

### Create conversation

- `POST https://tavusapi.com/v2/conversations`
- Called from: `TavusFullPipelineExamFlowService`, once per question.
- Request — `TavusConversationRequest` (`VoxOralExam.Core/Dtos/TavusConversationRequest.cs`), JSON:

  | C# property | wire name | type |
  |---|---|---|
  | `ReplicaId` | `replica_id` | string |
  | `PersonaId` | `persona_id` | string |
  | `ConversationName` | `conversation_name` | string |
  | `ConversationalContext` | `conversational_context` | string? |
  | `Properties.MaxCallDuration` | `properties.max_call_duration` | int — **nested under `properties`**, not top-level. WPF used to send a top-level `max_call_duration_seconds`, which Tavus's real schema doesn't have at all; fixed. |

  Tavus also supports `custom_greeting` (string, spoken once a participant joins) — not used by
  WPF today, available if useful later.

- Response — `StartSessionResponse`:

  | C# property | wire name | type |
  |---|---|---|
  | `ConversationId` | `conversation_id` | string |
  | `ConversationUrl` | `conversation_url` | string |
  | `Status` | `status` | string |
  | `CreatedAt` | `created_at` | DateTimeOffset? |

### End conversation

- `DELETE https://tavusapi.com/v2/conversations/{conversationId}` — no request/response body,
  only status code is checked (`EnsureSuccessStatusCode`).

### WebView2 + Daily JS bridge (`Controls/TavusConversationHost.xaml.cs`)

Not HTTP — an in-process bridge between C# and the embedded Daily JS SDK page.

**C# → JS** (`TavusConversationHost` methods → `window.tavusBridge.*`):

| C# method | JS call | Effect |
|---|---|---|
| `JoinAsync(url)` | `joinConversation(url)` | joins the Daily room |
| `SpeakAsync(text)` | `speak(text)` | sends `conversation.echo` app-message (`properties: { modality: 'text', text, done: true }`, per Tavus's Interactions Protocol docs) — used at the start of every question to make the replica read the instruction+question, since the custom LLM (`layers.llm.base_url`) only ever responds to a user message and never speaks proactively |
| `OverwriteContextAsync(context)` | `overwriteContext(context)` | sends `conversation.overwrite-context` app-message (`properties: { context }`) to swap in the next question's context on the same long-lived conversation. Was wrongly named `conversation.overwrite_llm_context` (invented, not a real Tavus event) until fixed — Tavus silently failing to recognize it is the likely cause of `system.shutdown` events observed mid-question-2+ |
| `InterruptAsync()` | `interrupt()` | sends `conversation.interrupt` app-message |
| `LeaveAsync()` | `leaveConversation()` | leaves the Daily room |

**JS → C#** (`postMessage` → `TavusConversationHost` events) — real Daily app-message
`event_type` values per Tavus's Interactions Protocol docs (fetched directly, not guessed; the
detail pages for each event's exact JSON payload didn't fully render when checked, so the
`role`/`speech` field names below carry slightly lower confidence than the event names
themselves — confirm with a live session before depending on them further):

| JS `type` | C# event | Real Tavus `event_type` |
|---|---|---|
| `utterance` | `OnUtteranceFinal(string text, string role)` | `conversation.utterance` (no `.final` suffix — that was wrong before). Payload has a `properties.role` field (`"replica"` confirmed, `"user"` presumed) used to tell student vs. avatar speech apart — previously impossible. There's also `conversation.utterance.streaming` for partial/interim transcripts, not currently relayed. |
| `replica-started-speaking` | `OnReplicaStartedSpeaking()` | `conversation.replica-started-speaking` (hyphenated — was wrongly `_started_speaking` with underscores before, meaning this event likely never matched anything real) |
| `replica-stopped-speaking` | `OnReplicaStoppedSpeaking()` | `conversation.replica-stopped-speaking` (same hyphen fix) |
| `user-started-speaking` | `OnUserStartedSpeaking()` | `conversation.user-started-speaking` (hyphenated, per the official overview page — one search result suggested a dotted `conversation.user.started_speaking` form instead; the overview page is the more authoritative source, but this is still worth confirming live before trusting it fully) |
| `user-stopped-speaking` | `OnUserStoppedSpeaking()` | `conversation.user-stopped-speaking` |
| `connection-state-changed` | `OnConnectionStateChanged(string state)` | from Daily's own `joined-meeting`/`left-meeting`/`error`, not a Tavus conversation event |

`conversation.overwrite-context` and `conversation.echo` (both *sendable* interactions, see
`OverwriteContextAsync`/`SpeakAsync` above) are now actively used, one Tavus conversation spans
the whole exam (not one per question) with context swapped mid-call between questions.

## 2. Java backend (`vox`) — base URL `AppSettings.JavaBaseUrl`

Client: `ExamLogService.cs` implementing `IExamLogService` (or whatever it's rebuilt as). Auth:
`Authorization: Bearer {ExamSessionState.CurrentUser.AccessToken}` if present.

**WPF only calls one Java endpoint directly — the S3 upload proxy below.** There is no direct
WPF → Java call to persist `AnswerTurn`. That persistence happens through Python instead: WPF
sends the uploaded `audioUrl` to Python's `/evaluate/turn` (§3), and Python — when it decides a
question is done — publishes a Kafka event (`AnswerTurnsRecordedEvent`, topic
`answer-turns-recorded`, see `agents/src/infra/message_broker/publishers/exam_publisher.py`)
carrying every turn for that answer (`audio_url`/`transcript`/`duration_seconds`/`word_count`/
etc. — see `agents/src/events/answer_turns_recorded.py`). Java is meant to consume that topic and
persist `AnswerTurn` rows itself. **As of this writing `vox` has no consumer for this topic at
all** (only an unrelated `DummyUserEventConsumer.java` exists) — so today, even once everything
else here works, no `AnswerTurn` actually lands in the database until Java adds that consumer.
This is Java-repo work, not something WPF or `agents` needs to do.

### Upload turn audio

- `POST {JavaBaseUrl}/api/v1/exam-turns/upload-audio`
- Request: `multipart/form-data` — `attemptAnswerId` (string GUID), `turnOrder` (string int),
  `file` (the turn's WAV, 16kHz mono PCM16, filename `turn-{turnOrder}.wav`, content-type
  `audio/wav`).
- Response (expected): JSON `{ "audioUrl": string }`. If the body isn't valid JSON, WPF falls
  back to treating the raw response body (minus surrounding quotes) as the URL — so a bare
  JSON string response also works.
- ⚠️ This endpoint does not exist server-side in `vox` yet (no matching `@RestController` found
  as of this writing) — calling it today returns 404.

## 3. Python backend (`agents`) — base URL `AppSettings.PythonBaseUrl`

Client: `InterviewApiService.cs` implementing `IInterviewApiService`.

### Evaluate turn / follow-up decision — ✅ exists server-side, contract confirmed

- `POST {PythonBaseUrl}/evaluate/turn` (real route: `agents/src/controller/followup_controller.py`)
- Request — built from `EvaluateTurnRequest` (`VoxOralExam.Core/Dtos/EvaluateTurnRequest.cs`),
  sent as `multipart/form-data` (**not JSON** — this is a FastAPI `Form(...)` endpoint):

  | Form field | Source C# property | Type |
  |---|---|---|
  | `audio_ref` | `AudioRef` | string (the same S3 `audioUrl` from §2) |
  | `answer_id` | `AnswerId` | Guid → string |
  | `turn_order` | `TurnOrder` | int → string |
  | `prompt_text` | `PromptText` | string — must be the literal text spoken for *this* turn (the main question text on turn 1, or the previous response's `NextPromptText` on follow-up turns) — **never** `Question.PromptText`, which is an unrelated static admin label on the question bank entity |
  | `language` | `Language` | string, e.g. `"en"` |
  | `question` | `Question` (`QuestionContextDto`, JSON-serialized) | see table below — **snake_case JSON keys**, unlike everything else Python returns (camelCase) |

  `QuestionContextDto` JSON shape (must match Python's `QuestionContext` pydantic model exactly):

  | C# property | wire name | type |
  |---|---|---|
  | `QuestionText` | `question_text` | string |
  | `QuestionType` | `question_type` | string, e.g. `"short_answer"`, `"long_answer"`, `"opinion"`, `"description"`, `"read_aloud"` — lowercase snake_case values |
  | `DifficultyLevel` | `difficulty_level` | string, `"easy"`/`"medium"`/`"hard"` |
  | `DurationSeconds` | `duration_seconds` | int |
  | `MinResponseSeconds` | `min_response_seconds` | int |
  | `MaxResponseSeconds` | `max_response_seconds` | int |
  | `EvaluationGuide` | `evaluation_guide` | string (free text; Python's real schema is actually a structured object — WPF currently flattens it to one string, acceptable since the field is optional/best-effort context) |

- Response — `ShouldFollowupResponse` (`VoxOralExam.Core/Dtos/ShouldFollowupResponse.cs`), JSON,
  **camelCase** (deserialized with `PropertyNameCaseInsensitive = true`, which only ignores case —
  it does not need to bridge snake_case, because the Python response really is camelCase):

  | C# property | wire name | type |
  |---|---|---|
  | `TurnOrder` | `turnOrder` | int |
  | `Transcript` | `transcript` | string |
  | `PromptText` | `promptText` | string? |
  | `CurrentTurn` | `currentTurn` | `EvaluatedTurnDto?` (nested, see below) |
  | `ShouldContinue` | `shouldContinue` | bool |
  | `NextPromptText` | `nextPromptText` | string? |
  | `Reason` | `reason` | string |
  | `ReachedMaxTurns` | `reachedMaxTurns` | bool |

  `EvaluatedTurnDto` (nested in `currentTurn`):

  | C# property | wire name | type |
  |---|---|---|
  | `AnswerId` | `answerId` | Guid |
  | `TurnOrder` | `turnOrder` | int |
  | `TurnType` | `turnType` | enum |
  | `PromptText` | `promptText` | string? |
  | `AudioUrl` | `audioUrl` | string |
  | `Transcript` | `transcript` | string |
  | `DurationSeconds` | `durationSeconds` | int |
  | `WordCount` | `wordCount` | int |
  | `AnsweredAt` | `answeredAt` | DateTimeOffset |

**This call is the real persistence trigger** (see §2 — Python relays to Java via Kafka when
`shouldContinue` goes `false`), so WPF must call this after every turn's S3 upload, not just for
the decision text. Use `ShouldContinue`/`ReachedMaxTurns` from the response as the authoritative
signal for "is this question's Tavus conversation done" — more reliable than guessing from
whatever the avatar says out loud.

⚠️ **Known race condition, not yet resolved:** Tavus's own live `/v1/chat/completions` call (see
below) decides follow-up-or-not in real time using Tavus's own STT, and the avatar speaks that
decision immediately. This `/evaluate/turn` call is a *separate*, slower decision (WPF waits for
upload, then Python re-transcribes via its own Whisper-based STT) that could disagree with what
Tavus already decided and said out loud — e.g. the avatar might already be mid-follow-up by the
time this call resolves `shouldContinue=false`, and WPF tearing down the conversation at that
point would cut the avatar off mid-sentence. Watch for this during manual testing; no mitigation
designed yet.

### Chat completions — WPF does **not** call this directly

- `POST {PythonBaseUrl}/v1/chat/completions` is called by **Tavus itself** (Tavus's Custom LLM
  integration), never by WPF. Listed here only because WPF is responsible for supplying enough
  context for it to work: Python recovers per-question context by scanning the `messages` array
  for a system message containing `<question_context>{...}</question_context>`, where `{...}` is
  the exact same snake_case JSON shape as the `question` field above. **WPF does not currently
  send this anywhere** — `TavusConversationRequest` has no `conversational_context` field yet
  (see Open items).

## Open items (need confirmation, not yet safe to guess)

1. ~~Exact Tavus/Daily `event_type` string for "user started speaking" / "user stopped
   speaking"~~ — looked up against Tavus's official docs, applied in §1
   (`conversation.user-started-speaking`/`-stopped-speaking`, hyphenated). One search result
   suggested a different dotted form instead, so this is "best available evidence," not
   100% confirmed — verify against a real session's raw `app-message` payloads before fully
   trusting it. Same caveat applies to the `role`/`speech` field names inside the `utterance`
   event payload (Tavus's schema detail pages didn't render fully when checked).
2. ~~Whether Tavus supports updating `conversational_context` mid-conversation~~ — resolved: one
   Tavus conversation is created per exam question instead (see
   `docs/ai-examiner-plan.md`), so this never comes up.
3. The Tavus-live-decision vs. WPF's-own-`/evaluate/turn`-decision race condition noted in §3 —
   no mitigation designed yet, needs observing in a real manual run.
4. The single Java endpoint in §2 (`upload-audio`) doesn't exist server-side yet, and `vox` has
   no Kafka consumer for the `answer-turns-recorded` topic either — both are Java-repo gaps, not
   WPF or `agents` work.
