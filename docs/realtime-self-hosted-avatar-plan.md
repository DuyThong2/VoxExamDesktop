> Saved 2026-06-24, revised same day after further design discussion (per-attempt connection
> lifetime, decoupled decision/archive paths, durable idempotency). This supersedes
> `ai-examiner-plan.md`'s Tavus-based design (kept in place for history) and is itself the
> **local-only** half of a two-part plan: deployment (Helm/EKS/GPU node groups) is a separate
> plan to be written later, once this one's phases work end-to-end on local/dev hardware.

# Replace Tavus with self-hosted realtime avatar pipeline (Vox Oral Exam)

## Context

Vox Oral Exam currently uses Tavus (a hosted conversational-avatar vendor) as the AI examiner: WPF embeds Tavus via WebView2 + Daily JS SDK, driven by `TavusFullPipelineExamFlowService.cs`. Earlier in this session, a prior debugging effort fixed a blocking Tavus crash, and the project briefly stayed on Tavus — but the user then reviewed a generic self-hosted realtime-avatar architecture doc (Azure Voice Live + LangGraph + Azure TTS + LivePortrait + MuseTalk + aiortc WebRTC + EKS GPU) and decided to actually build it, dropping Tavus as the avatar vendor for good (motivated by Tavus's free-tier quota not fitting real exam lengths, and wanting full control over avatar/decision behavior).

Two hard constraints were set by the user while scoping this:
1. **Never touch the `vox` Java repo.** It is read-only reference only.
2. **The turn structure/flow must stay exactly as currently implemented** — no new Java fields/endpoints, no presigned-URL invention.

Fresh code reading (this session) confirmed Java actually has **zero persistence/endpoints** for `ExamAttempt`/`AttemptAnswer`/`AnswerTurn` today — only unwired domain classes. So the real existing turn flow is: WPF records+uploads WAV to S3 directly (static credentials) → POSTs to Python's `/turns/archive` → Python's `archive_graph` stores it → Kafka `answer-turns-recorded` event (currently unconsumed by anyone). This plan preserves that flow's *shape* byte-for-byte (same fields, same endpoint contracts); it does not invent Java work.

**Intended outcome (updated 2026-06-24):** the user confirmed they no longer use Tavus at all and it's fine to remove it outright — so this plan does **not** keep a Tavus/Realtime dual-mode fallback switch. `RealtimeExamFlowService` is the sole `IExamFlowService` implementation; Tavus-specific code (`TavusFullPipelineExamFlowService.cs`, `TavusService.cs`, `TavusConversationHost.xaml(.cs)`, `tavus-host.html`, `ITavusService.cs`, `agents/src/controller/tavus_controller.py`, Tavus-specific DTOs/config) is **deleted**, not kept dormant, once its reusable pieces (turn upload/archive logic, archived-turns catch-up + Kafka-publish logic) are extracted into the new shared classes named below.

**Further refined the same day, after more design discussion with the user:**
- **No reconnect gaps between questions, unlike Tavus's per-question conversation restarts.** The avatar WebRTC connection and the realtime WebSocket are each opened **once per exam attempt** and stay open for the whole exam — switching questions is an in-band control message, never a reconnect. This directly avoids the exact problem that made Tavus's "fresh conversation per question" fallback visibly janky.
- **Decision-making (Path A) and archival/Kafka-publishing (Path B) are decoupled async paths**, confirmed with the user: Path A never blocks on S3/archive; Path B runs per turn (not batched at question-end) and must be **idempotent and durable** so a dropped connection or retried call never produces duplicate Kafka events or duplicate archived turns, and a reconnect can always determine the true last-archived turn from durable state (Postgres), not from in-memory guesswork.

Scope: `DesktopApp\VoxOralExam` (WPF) and `agents` (Python) only. `vox` (Java) is read-only reference and is **never modified** — anywhere this plan would normally need a new Java endpoint/field/table, it is either redesigned to avoid that need or explicitly called out as an out-of-scope gap, never as an implementation step.

**This is the "local" plan only.** Everything below targets getting the new pipeline running and verified on local/dev hardware (and, where GPU-bound, on whatever GPU is reachable for testing). Kubernetes/Helm/EKS deployment, GPU node groups, and production scaling are explicitly **out of scope here** and will be their own separate plan written later, once the local pipeline (through Phase 6) actually works end-to-end. Nothing in this plan should be blocked on or shaped around deployment concerns yet.

## Ground truth this plan is built on (verified by fresh code reading)

- Turn flow today (and whose *shape* is preserved exactly): WPF `TurnAudioRecorder` (NAudio,
  16kHz/16-bit mono, pre-roll buffer) captures one turn → `EncodeWav` → upload to S3 via static
  long-lived credentials in `appsettings.json` (`AwsAccessKeyId`/`AwsSecretAccessKey`, no
  presigned URLs) → `POST {PythonBaseUrl}/turns/archive` (multipart: `audio_ref`, `answer_id`,
  `turn_order`, `prompt_text`, `language`, `question`) → Python's `archive_graph`
  (`transcribe_turn_node` using sync Azure STT `utils/speech_client.transcribe`, then
  `append_turn_node`) appends to a Postgres-checkpointed `turns` list keyed by
  `thread_id=answer_id`. This path is implemented in
  `agents/src/controller/archive_controller.py` and `agents/src/node/followUpDecisionGraph/graphConfig.py`.
  **Update:** `append_turn_node` gets one small additive change in Phase 2 (idempotency guard,
  see below) — everything else about this path (endpoint shape, fields, S3 credential model)
  stays exactly as today.
- Follow-up decision logic to reuse as-is: `agents/src/node/followUpDecisionGraph/FollowUpNode/followup_decision_node_config.py`
  (edge cases: repeat-request, no-meaningful-speech, hesitation-only, `MAX_TURNS=3` hard stop,
  else GPT-4o JSON decision) fed by `agents/src/node/followUpDecisionGraph/SignalNode/signal_node_config.py`
  (`prepare_turn_signals_node`), wired together today only via the stateless
  `build_text_followup_graph()` in `graphConfig.py`. State shape is `FollowUpGraphState`
  (`agents/src/node/followUpDecisionGraph/GraphState.py`): needs `current_turn`, `turns`
  (previous turns for this question), `question` (`QuestionContext`), `turn_order`. Output is
  `decision = {should_continue, next_prompt_text, reason}` — `next_prompt_text` (or
  `CLOSING_REPLY` from `mappers/chat_completion_mapper.py` when stopping) is exactly the TTS
  input text for the next avatar utterance. This decision logic itself is **not** rewritten —
  only how/when it's invoked changes.
- **Decision path and archive/Kafka path are intentionally decoupled** — two independent paths
  off the same `speech_end`/turn-boundary event:
  - **Path A (decision, fast, never waits on S3):** the realtime live transcript (Azure Voice
    Live, Phase 3) comes from the streamed mic audio directly — it does not depend on the S3
    upload at all. `decide_next_step()` runs the instant the live transcript is ready and
    returns the decision immediately; the avatar can already be speaking the next prompt while
    Path B below is still in flight for the turn that just ended. Path A holds no durable state
    of its own — if interrupted mid-turn by a disconnect, it simply restarts cleanly for that
    turn after reconnect; it is never the source of truth.
  - **Path B (archive + Kafka, slower, async, per-turn, must be idempotent/durable):** WPF
    finalizes the turn's WAV only once the turn ends (not streamed continuously) → uploads to S3
    (one upload per turn) → `POST /turns/archive` → Python's `archive_graph` downloads from S3
    and runs its own (slower, archival-quality) Azure STT → turn becomes "archived". Every turn
    triggers its own Kafka publish as soon as Path B's archive completes for that turn — not
    batched at the end of the question. Both the archive-append and the Kafka-publish must be
    **idempotent by `(answer_id, turn_order)`** and the "already published" fact must be stored
    **durably** (in the same Postgres checkpoint state `archive_graph` already uses — not a
    Python-process-local in-memory set, since that would be lost on reconnect or process
    restart). Path B's existing catch-up/retry logic (`_wait_for_archived_turns`) is reused, just
    invoked after every turn instead of only at the question's end, running as a background task
    that never blocks Path A's response to the client.
- **Connection lifetime: one WebSocket + one avatar WebRTC connection per exam attempt, not per
  question.** The realtime WebSocket is keyed by `exam_attempt_id` (not `answer_id`) and stays
  open for the whole exam; switching questions is an in-band `question_start`/`question_end`
  control message, never a reconnect. `RealtimeExamSession` is still one instance per
  `answer_id`/question (turn_order still resets to 1 per question, confirmed correct with the
  user), but it now lives *inside* a longer-lived per-attempt connection object rather than being
  the connection itself. The avatar WebRTC connection (the part the student actually sees/hears)
  is opened once at exam start and never re-negotiated between questions — this is the direct fix
  for the exact kind of visible interruption Tavus's "new conversation per question" fallback
  used to cause.
- **Reconnect/resume must be possible without data loss or duplication.** Durable state lives in
  two places only: WPF's own `ExamSessionState` (question list, current index, current
  `answer_id`/`turn_order` — survives a transport reconnect since it's just in-memory app state,
  not socket-bound) and Python's Postgres-checkpointed `archive_graph` state (which turns are
  archived/published for a given `answer_id` — survives even a Python process restart). On
  reconnect, the client sends a `resume` message with its believed current
  `(exam_attempt_id, answer_id, turn_order)`; the server answers with the durable
  `last_archived_turn_order` for that `answer_id` (queried from the checkpoint, not guessed), so
  both sides realign before continuing. The WAV-upload/`/turns/archive` HTTP calls are already
  resilient to socket drops by nature (plain HTTP, not WebSocket-dependent) — only the *live*
  transcript/decision for an in-flight turn needs to restart cleanly after a reconnect.
- WPF DI today (`App.xaml.cs`): `IExamFlowService` is registered as
  `services.AddSingleton<TavusFullPipelineExamFlowService>(); services.AddSingleton<IExamFlowService>(sp => sp.GetRequiredService<TavusFullPipelineExamFlowService>());`
  gated by `settings.EnableTavus`/`ExamViewModel.IsTavusEnabled`. Per the updated intended outcome,
  this becomes a direct registration of `RealtimeExamFlowService` as the sole `IExamFlowService`
  — no mode switch, since Tavus is deleted rather than kept dormant.
- Proctoring (`Services/ScreenProctoringService.cs` + `Services/CameraService.cs` +
  `Infrastructure/WebRtcClient.cs`, sendonly camera→Python) and Python's `controller/webrtc.py`
  (aiortc recvonly + synchronous `ultralytics.YOLO` per frame, single global `yolo_model` loaded
  at import time, `FRAME_SKIP`-gated) are fully independent of the exam-flow/avatar pipeline —
  they share no classes today and must keep running unchanged and concurrently.
- Dependency reality (checked directly in `agents/.venv`, **updated 2026-06-25**): a real NVIDIA
  GPU is actually present on the dev machine (`nvidia-smi` confirmed: RTX 2000 Ada Generation,
  ~8GB VRAM, driver 595.71 supporting CUDA 13.2) — the earlier `torch==2.12.0+cpu`/
  `torch.cuda.is_available() == False` finding was just because the CPU-only torch wheel
  happened to be installed, not a hardware limitation. Open Question 2 (where to run the Phase 0
  PoC) is resolved: locally, on this machine's GPU, once torch is swapped for a CUDA build.
  `azure-ai-voicelive[aiohttp]` is now installed (added via `uv add`, see below).
- `agents/pyproject.toml` already has `aiortc>=1.9.0`, `azure-cognitiveservices-speech>=1.50.0`
  (sync only), `boto3`, `langgraph`+`langgraph-checkpoint-postgres`, `aiokafka`. Net-new
  dependencies needed: Azure Voice Live SDK/REST surface (realtime STT+VAD — confirm exact
  package name during Phase 0, do not assume naming without checking what's actually published
  when this phase starts), an Azure TTS streaming call (the same `azure-cognitiveservices-speech`
  package supports synthesis, just unused today), a LivePortrait runtime, a MuseTalk runtime, and
  whatever GPU-accelerated torch build the actual GPU pod needs (separate from the CPU build in
  the dev venv).
- WPF target is `net10.0-windows`; `System.Net.WebSockets.ClientWebSocket` needs no new NuGet
  package. `SIPSorcery`/`SIPSorcery.VP8`/`SIPSorceryMedia.FFmpeg` are already referenced and
  reusable for the new avatar-receiving (recvonly) WebRTC connection.
- Found but explicitly out of scope: a `vox-streaming` Go repo (aiortc-style WebRTC
  broadcaster/recorder) and an `infrastructure/` repo exist under `d:\semester9` but are not
  referenced anywhere by `DesktopApp`, `vox`, or `agents` source (confirmed zero references) and
  were not named by the user as in-scope — not used anywhere in this plan; flagged in Open
  Questions in case the user actually wants to repurpose it instead of building a new aiortc
  publisher in `agents`.

## Out-of-scope gaps explicitly NOT fixed by this plan (Java-shaped, flagged not solved)

- Java has zero JPA entities/repositories/REST/GraphQL endpoints for `ExamAttempt`/
  `AttemptAnswer`/`AnswerTurn`, and zero Kafka consumer for `answer-turns-recorded`. This plan
  does not add any of that. The new pipeline keeps publishing the same event to the same topic
  (now once per turn instead of once per question, see Phase 2); what (if anything) ever
  consumes it on the Java side remains a separate future task.
- No presigned-URL ticket endpoint is introduced anywhere (Java or Python) for S3 uploads — WPF
  keeps using its existing static AWS credentials exactly as today.
- Deployment (Dockerfile changes, Helm charts, EKS GPU node groups, NVENC encoder config,
  readiness/liveness probes, autoscaling) is deferred entirely to a future second plan. This plan
  stops at "runs correctly on local/dev hardware, verified manually phase by phase."
  **Carry this forward to that future plan (flagged 2026-06-25, user explicitly intends EKS for
  deployment):** the Phase 0 LivePortrait/MuseTalk dependency setup on this Windows dev machine
  involved a long chain of manual, ad-hoc fixes (OpenMMLab `mim install` chain for
  mmengine/mmcv/mmdet/mmpose, a prebuilt-wheel override for `mmcv` since the pinned version has
  no wheel for this torch/cuda/python combo, Windows directory junctions for weight paths,
  Windows-specific Unicode/DLL workarounds) currently captured only as README instructions, not
  as a reproducible Dockerfile/build script. Before any EKS deployment work starts, this needs
  to become an actual Dockerfile (ideally starting from an official PyTorch+CUDA base image
  rather than layering CUDA onto a generic Linux base by hand) — don't assume the Windows
  command sequence ports over as-is: junctions need to become symlinks, and Linux may hit
  different (not necessarily fewer) dependency build issues than Windows did. The
  `mmcv==2.2.0`/`cu121`/`torch2.3.0` prebuilt wheel override is confirmed to also have a Linux
  (`manylinux1_x86_64`) build under the same index, so that specific fix should carry over.

---

## Phase 0 — Risk-reduction PoC spike (no integration with exam flow yet)

Goal: prove, in isolation, that (a) Azure Voice Live realtime STT+VAD can be driven from Python
at all, and (b) LivePortrait + MuseTalk can run together to produce a talking-head video stream
in something close to realtime on whatever hardware is actually available. Both are entirely new
surfaces with zero existing code in this codebase — nothing in later phases should assume either
"just works" until this phase produces a passing, measured demo.

**New files (under a clearly throwaway/spike namespace):**
- `agents/spikes/voice_live_poc.py` — standalone script (not a FastAPI route) that opens a
  realtime session against Azure Voice Live (verify the exact current API/SDK name at the start
  of this phase — naming/availability may have shifted), streams a canned 16kHz mono WAV (same
  format `TurnAudioRecorder` produces) chunk-by-chunk, and logs partial/final transcripts + VAD
  start/stop events with timestamps.
- `agents/spikes/avatar_render_poc.py` — standalone script that, given one driving audio clip
  and one reference avatar photo, runs LivePortrait (motion/expression) then MuseTalk (lip-sync)
  and writes an output video file, logging per-stage and end-to-end wall-clock time and peak
  GPU/CPU memory.
- `agents/spikes/requirements-poc.txt` (or a dedicated `poc` dependency group in
  `pyproject.toml`) — pins LivePortrait/MuseTalk's own dependencies (these projects often pin
  specific torch/onnxruntime/cuda versions that may conflict with the main app's torch — resolve
  or explicitly isolate this; see Open Questions on environment isolation).

**Reused:** only the WAV format convention (16kHz/16-bit mono) from `TurnAudioRecorder.cs` —
nothing functional, this phase is intentionally isolated.

**Net-new:** Azure Voice Live client wiring, LivePortrait inference wrapper, MuseTalk inference
wrapper, GPU/CPU benchmarking harness.

**Verification before moving to Phase 1:**
- Voice Live PoC: feed 3-5 sample WAVs (including one with a mid-utterance pause and one with
  background noise) and confirm partial transcripts arrive incrementally and VAD end-of-speech
  fires within an acceptable latency band (define the band as part of this phase's exit
  criteria, e.g. "<500ms after actual silence" — no number is fixed yet).
- Avatar PoC: measure end-to-end latency from "audio ready" to "first video frame ready" for a
  ~10 second utterance, and sustained FPS for the full clip, on the actual target GPU (not a
  laptop CPU) once accessible. Record results in `agents/spikes/poc-results.md` so Phase 2/3
  sizing is grounded in real numbers.
- Explicit go/no-go gate: if LivePortrait+MuseTalk combined cannot sustain at least
  conversational frame rate (informally ~15-20fps; exact threshold is the user's call — see Open
  Questions) on the available GPU, stop and reconsider the avatar rendering approach (e.g.
  simpler viseme-driven 2D animation) before building further phases around it.
- **Quantization is an acceptable mitigation to try before declaring infeasibility**, if RAM or
  GPU VRAM turns out to be insufficient for full-precision inference: fp16 conversion for the
  `.pth` checkpoints (`appearance_feature_extractor`, `motion_extractor`, `spade_generator`,
  `warping_module`, `stitching_retargeting_module`) and int8 (dynamic or static) quantization for
  the `.onnx` models (`landmark.onnx`, the InsightFace `buffalo_l` detector) via onnxruntime's
  quantization tools are both reasonable to attempt within Phase 0 itself — this is a mitigation
  step in the PoC, not a reason to skip the go/no-go gate or a separate phase of its own.

---

## Phase 1 — WPF: remove Tavus, extract reusable turn-capture/upload code, scaffold the new flow service

Goal: delete Tavus-specific code (its reusable pieces extracted first), and stand up
`RealtimeExamFlowService` as the sole `IExamFlowService` implementation — as a stub for now,
since real WebSocket/WebRTC/mic-streaming logic is built in Phase 5.

**New files:**
- `VoxOralExam.DesktopApp/Services/TurnAudioUploader.cs` — extracted from
  `TavusFullPipelineExamFlowService.UploadTurnAudioAsync`/`EncodeWav`/`CreateS3Client`/
  `BuildS3ObjectUrl`, same static-credential `IAmazonS3` construction, same object key shape
  (`{attemptAnswerId:D}/turn-{turnOrder}.wav`). Exposes `EncodeWav(byte[] pcm)` and
  `UploadTurnAudioAsync(byte[] wav, Guid attemptAnswerId, int turnOrder, CancellationToken ct)`.
- `VoxOralExam.DesktopApp/Services/TurnArchiveClient.cs` — extracted from
  `TavusFullPipelineExamFlowService.ArchiveTurnAsync`/`BuildEvaluateTurnRequest`, posting the
  exact same multipart shape to `{PythonBaseUrl}/turns/archive` using the existing
  `EvaluateTurnRequest` DTO (`VoxOralExam.Core.Dtos`) — no shape changes.
- `VoxOralExam.DesktopApp/Services/RealtimeExamFlowService.cs` — implements
  `VoxOralExam.Core.Interfaces.IExamFlowService` (same 4 events, same
  `StartAsync`/`StopAsync` signature). For this phase, `StartAsync` raises
  `OnStatusChanged("Realtime exam flow not yet implemented")` then throws
  `NotImplementedException` — an intentional stub. Takes `TurnAudioUploader` and
  `TurnArchiveClient` as constructor dependencies so Phase 5 can build on them.

**Deleted files (reusable pieces extracted above first):**
`Services/TavusFullPipelineExamFlowService.cs`, `Services/TavusService.cs`,
`Services/TavusConversationHostRegistry.cs`, `Services/TavusConversationLeaseStore.cs`,
`Controls/TavusConversationHost.xaml(.cs)`, `Assets/Web/tavus-host.html`,
`VoxOralExam.Core/Interfaces/ITavusService.cs`, and any Tavus-only request DTO (e.g.
`TavusConversationRequest`) confirmed unused elsewhere by a solution-wide grep first.

**Modified files:**
- `VoxOralExam.DesktopApp/App.xaml.cs` — remove Tavus DI registrations; register
  `IExamFlowService` directly to `RealtimeExamFlowService` (no switch); register
  `TurnAudioUploader`/`TurnArchiveClient` as singletons.
- `VoxOralExam.DesktopApp/State/AppSettings.cs` + `appsettings.json` — remove `EnableTavus`,
  `TavusApiKey`, `TavusPersonaId`, `TavusReplicaId`. Leave S3/Java/Python URLs, camera, and turn
  timing settings untouched. (Settings for the new WebSocket/avatar-WebRTC endpoints are added in
  Phase 5 once their exact paths are finalized — no point stubbing names now that would just get
  renamed.)
- `VoxOralExam.DesktopApp/ViewModels/ExamViewModel.cs` — remove `IsTavusEnabled` and any
  Tavus-specific visibility flags. No new avatar-UI flag invented yet — that's Phase 5's job once
  there's an actual avatar control to bind to.
- `VoxOralExam.DesktopApp/Views/ExamWindow.xaml` — remove the `TavusConversationHost` element
  (and its `xmlns:controls` mapping if nothing else in the file uses it).

**Verification before moving to Phase 2:**
- Solution-wide grep for "Tavus" returns no remaining references outside docs/history.
- `dotnet build` succeeds cleanly.
- App starts, `RealtimeExamFlowService.StartAsync`'s `NotImplementedException` is caught and
  surfaced as a clean status/error rather than crashing the app.
- Exercise `TurnAudioUploader`/`TurnArchiveClient` in isolation against a local/staging S3
  bucket + the existing `/turns/archive` endpoint to confirm the extraction didn't change wire
  behavior.

---

## Phase 2 — Python: realtime session manager skeleton + per-attempt WebSocket endpoint + durable idempotent turn-publishing

Goal: stand up the new "brain" of the realtime flow as a FastAPI WebSocket endpoint scoped to
**one connection per exam attempt** (not per question), prove the message-passing skeleton and
the reused decision-graph call, and build the idempotent/durable turn-publishing primitives that
fault tolerance depends on — all before wiring real Voice Live/TTS/avatar rendering.

**New files:**
- `agents/src/realtime/__init__.py` — new package, sibling to `controller/`.
- `agents/src/realtime/attempt_connection.py` — `AttemptConnection` class: **one instance per
  `exam_attempt_id`, living for the whole exam.** Owns the WebSocket lifecycle, and creates/tears
  down a `RealtimeExamSession` per question as `question_start`/`question_end` control messages
  arrive, routing `transcript_chunk`/`turn_end` messages to whichever session is currently
  active. Also handles the `resume` message (see below) by answering with durable state rather
  than guessing from memory.
- `agents/src/realtime/session.py` — `RealtimeExamSession` class: one instance per
  `answer_id`/question (created/destroyed by `AttemptConnection`, `turn_order` restarting at 1
  per question — confirmed correct with the user). Exposes
  `decide_next_step(transcript: str, word_count: int) -> dict` that builds
  `FollowUpGraphState`-shaped input directly from its own held state (question context,
  turn_order, accumulated turns — no OpenAI-message-format parsing needed, unlike Tavus's
  endpoint) and invokes the existing `text_followup_graph` verbatim. This call must return
  immediately on the live transcript — it must **never** await Path B (archive/Kafka) work.
- `agents/src/realtime/turn_publisher.py` — extracted from `tavus_controller`'s
  `_wait_for_archived_turns`/`_publish_archived_turns`/`_build_answer_turn_payload`, reusing the
  exact same `archive_graph.get_state(...)` catch-up-retry tuning and the same
  `publish_answer_turns_recorded`, but redesigned for the new requirements:
  - Triggered after **every** turn (fire-and-forget background task kicked off by
    `RealtimeExamSession` right after `decide_next_step` returns, not awaited by it), not just
    when `should_continue=False`.
  - **Idempotent and durable by `(answer_id, turn_order)`**: before publishing, check a durable
    "already published" marker (stored in the same Postgres checkpoint state `archive_graph`
    already maintains — e.g. a `published_turn_orders` field alongside the existing `turns`
    list, not an in-memory Python set) so a retry or a session recreated after reconnect never
    double-publishes. After a successful publish, persist that marker the same way.
  - Exposes a `get_last_archived_turn_order(answer_id) -> int` helper (reads the durable
    checkpoint state) — this is what answers the `resume` handshake in `attempt_connection.py`
    and, fully, in Phase 6.
- `agents/src/controller/realtime_controller.py` — new FastAPI router
  (`APIRouter(prefix="/realtime", tags=["Realtime"])`) with
  `@router.websocket("/attempts/{exam_attempt_id}")` — **one WebSocket per exam attempt, not per
  question.** Accepts the connection, creates an `AttemptConnection`, and for this phase handles a
  placeholder protocol: `{"type": "question_start", "answer_id": ..., "question": {...}}` (opens
  a new `RealtimeExamSession` inside the connection), `{"type": "transcript_chunk", "text": ...}`
  / `{"type": "turn_end"}` (routed to the active session, mirroring Phase 2's old per-question
  design but now inside one long-lived connection), and `{"type": "resume", "answer_id": ...,
  "turn_order": ...}` (answered with `{"type": "resume_ack", "last_archived_turn_order": ...}` via
  `turn_publisher.get_last_archived_turn_order`). Real binary audio streaming replaces the
  text-only transcript protocol in Phase 3; the connection-lifetime and resume-handshake shapes
  established here do not change later.
- `agents/src/app.py` — add `app.include_router(realtime_controller.router)` (additive).

**Modified files:**
- `agents/src/node/followUpDecisionGraph/graphConfig.py` (`append_turn_node`) — add a small,
  additive idempotency guard: if a turn with the incoming `turn_order` already exists in the
  checkpointed `turns` list for this `thread_id`, don't append a duplicate (upsert semantics
  instead of blind-append). This is the one deliberate exception to "archive_graph doesn't
  change" — it doesn't alter the endpoint contract or field shapes, it only makes a retried
  `/turns/archive` call safe.

**Reused:** `text_followup_graph` (verbatim), `archive_graph` (verbatim except the one
idempotency guard above), `events.AnswerTurnsRecordedEvent` + `publish_answer_turns_recorded`
(verbatim), `node.state_models.QuestionContext` (verbatim).

**Net-new:** the per-attempt WebSocket transport, `AttemptConnection`'s session
creation/teardown per question, durable idempotent turn-publish tracking, the `resume` message
skeleton.

**Verification before moving to Phase 3:**
- A standalone test client connects once to `/realtime/attempts/{exam_attempt_id}`, sends two
  sequential `question_start` messages (simulating two questions) over the **same** connection
  with synthetic transcripts/turn_ends in between, and confirms decisions come back correctly for
  both without any reconnect.
- Confirm a duplicate `turn_end`/retry for the same `(answer_id, turn_order)` does not produce a
  second Kafka publish or a second archived turn entry (test by replaying the same `/turns/archive`
  call twice and the same retry path twice).
- Confirm sending `resume` mid-test returns the correct `last_archived_turn_order` derived from
  the actual checkpoint state, not from in-memory assumptions.
- Confirm this WebSocket endpoint running alongside `/webrtc/offer` (proctoring) and
  `/turns/archive` doesn't regress either.

---

## Phase 3 — Python: real Azure Voice Live (STT+VAD) integration into the session

Goal: replace Phase 2's placeholder text protocol with real streamed audio in, driving Azure
Voice Live, so the session manager reacts to actual speech instead of synthetic test messages.

**New files:**
- `agents/src/realtime/voice_live_client.py` — wraps whatever Azure Voice Live SDK/REST/
  WebSocket surface Phase 0 validated, exposing an async generator/callback API: push raw PCM16
  audio chunks in, receive partial transcripts, final transcripts, and speech-start/speech-end
  VAD events out. Productionized version of `agents/spikes/voice_live_poc.py`.
- `agents/src/realtime/audio_ingest.py` — adapter that receives binary audio frames over the
  per-attempt WebSocket (binary frames, routed by `attempt_connection.py` to whichever
  `RealtimeExamSession` is currently active, same as text control messages) and feeds them into
  `voice_live_client.py`, replacing Phase 2's synthetic `transcript_chunk` message with real
  `partial_transcript` / `vad_speech_start` / `vad_speech_end` server→client events plus binary
  audio client→server frames.

**Modified files:**
- `agents/src/controller/realtime_controller.py` / `attempt_connection.py` — branch on binary vs.
  text frames (binary = audio chunk → `audio_ingest`, routed to the active session; text =
  control messages: `question_start`/`question_end`/`resume`).
- `agents/src/realtime/session.py` — receives VAD speech-end + accumulated final transcript from
  `voice_live_client.py` instead of a manual `turn_end` message, then proceeds into
  `decide_next_step(...)` exactly as Phase 2 did.

**Reused:** everything from Phase 2 unchanged, including the per-attempt connection lifetime and
the idempotent turn-publishing. WAV-format convention from `TurnAudioRecorder.cs` (16kHz/16-bit
mono) is the contract for what WPF streams — confirm Azure Voice Live's expected input format
matches or document the needed resample step.

**Net-new:** Azure Voice Live client integration, binary audio framing protocol over the
WebSocket.

**Verification before moving to Phase 4:**
- Using a temporary console-based audio streamer (mic or pre-recorded WAV) over the new binary
  protocol, confirm partial transcripts arrive, VAD correctly detects end-of-speech, and the
  final decision matches manual expectation for: a normal answer, a "repeat the question"
  utterance, a silent/no-speech turn, and a max-turns-reached scenario — all within one
  connection across simulated multiple questions.
- Compare latency from end-of-speech to decision-ready against Phase 0's measured VAD latency to
  confirm no material regression.
- Simulate a mid-turn disconnect/reconnect: confirm the `resume` handshake correctly reports the
  last archived turn, the interrupted turn's live STT/VAD restarts cleanly, and no duplicate
  archive/Kafka events result.
- Confirm `/turns/archive`'s own independent Azure STT transcription (unchanged, file-based) and
  the realtime live transcript can disagree without breaking anything — these are intentionally
  two separate transcription passes (live decision speed vs. archived record quality). Document
  this so nobody "fixes" it later by accident.

---

## Phase 4 — Python: TTS + LivePortrait + MuseTalk rendering pipeline wired into the session

Goal: turn `decision.next_prompt_text` into an actual talking-head video+audio stream, reusing
Phase 0's PoC code as the starting point for production wiring, with explicit GPU/queue
scheduling so this can run alongside YOLO proctoring without starving it.

**New files:**
- `agents/src/realtime/tts_client.py` — Azure TTS streaming synthesis (same
  `azure-cognitiveservices-speech` package already in `pyproject.toml`, previously used only for
  STT) — takes `next_prompt_text`, returns/streams PCM audio for the avatar to "speak".
- `agents/src/realtime/avatar_renderer.py` — production version of
  `agents/spikes/avatar_render_poc.py`: given the TTS audio stream, drives LivePortrait then
  MuseTalk to produce a video frame stream in near-realtime (chunked vs. whole-utterance,
  decided by Phase 0's latency numbers).
- `agents/src/realtime/gpu_scheduler.py` — explicit serialization/queueing between the avatar
  render pipeline and the existing YOLO proctoring inference (`controller/webrtc.py`'s
  `process_video_track`), since both compete for the same GPU. Concretely: a bounded worker
  queue (or `asyncio.Semaphore`) so avatar rendering (latency-sensitive, user-facing) and YOLO
  detection (already throttled via `FRAME_SKIP`) don't deadlock or starve each other on a
  single-GPU pod. Exact scheduling policy depends on Phase 0's measured GPU headroom (see Open
  Questions).
- `agents/src/realtime/avatar_webrtc.py` — a **separate** aiortc `RTCPeerConnection` (publisher;
  Python sends video+audio to WPF this time), opened **once per exam attempt and never
  re-negotiated between questions** (this is the piece that makes the no-reconnect-gap guarantee
  real for what the student actually sees/hears) — distinct from `controller/webrtc.py`'s
  existing proctoring connection (which stays receiving WPF's camera). New router endpoint
  `agents/src/controller/avatar_webrtc_controller.py` with `POST /avatar/webrtc/offer`, parallel
  in shape to the existing `/webrtc/offer` but reversed media direction and a separate
  `pcs`/`session_map` registry so cleanup logic for one never touches the other.

**Modified files:**
- `agents/src/realtime/session.py` — after `decide_next_step` returns, push `next_prompt_text`
  through `tts_client` → `avatar_renderer` → the exam attempt's `avatar_webrtc` peer
  connection's outbound tracks (the same connection across all questions in this attempt).
- `agents/src/app.py` — register the new avatar WebRTC router; add the new avatar peer
  connections to shutdown cleanup as a separate function so the existing proctoring shutdown
  path is untouched.

**Reused:** Phase 0's LivePortrait/MuseTalk wrapper code (promoted from spike to production), the
existing `aiortc` dependency, the session-keyed registry + cleanup lifecycle pattern already
proven in `controller/webrtc.py` (copied as a pattern, not by modifying that file).

**Net-new:** TTS streaming client, avatar rendering pipeline, GPU scheduling policy, second
aiortc peer-connection type (attempt-scoped, not question-scoped).

**Verification before moving to Phase 5:**
- Standalone: feed a fixed `next_prompt_text` through `tts_client` → `avatar_renderer` and
  confirm a valid video+audio stream with acceptable latency (compare against Phase 0's
  benchmark).
- Concurrency: run the avatar renderer and the YOLO proctoring loop simultaneously under
  synthetic load and confirm neither frame rate collapses, and `gpu_scheduler.py` prevents
  deadlock under sustained load.
- WebRTC-level: using a throwaway browser/test client, connect to `/avatar/webrtc/offer` once,
  then simulate multiple question transitions over the same connection and confirm video/audio
  never stalls or renegotiates between them.

---

## Phase 5 — WPF: persistent avatar WebRTC client + mic streaming + the real RealtimeExamFlowService

Goal: build the actual `RealtimeExamFlowService` that replaces the Phase 1 stub, wiring a
**single** WebSocket connection (mic audio out, decision events in) and a **single** WebRTC
connection (avatar video+audio in) that both persist for the whole exam attempt — without
touching the proctoring `WebRtcClient`/`CameraService` at all.

**New files:**
- `VoxOralExam.DesktopApp/Infrastructure/RealtimeSessionClient.cs` — wraps a
  `System.Net.WebSockets.ClientWebSocket` connection opened **once per exam attempt** to
  `/realtime/attempts/{examAttemptId}`. Sends `question_start` whenever `ExamSessionState`
  advances to a new question, binary mic audio frames (Phase 3's format) during turns, and
  `resume` on reconnect with the last known `(answer_id, turn_order)`. Receives JSON control
  events (`partial_transcript`, `vad_speech_start`, `vad_speech_end`, `decision`, `resume_ack`)
  and raises C# events mirroring the `TaskCompletionSource`-based wait/signal pattern the old
  Tavus flow service used internally. Owns its own reconnect/retry loop (Phase 6 fleshes out the
  policy) — reconnecting this socket never requires reconnecting the avatar WebRTC connection
  below.
- `VoxOralExam.DesktopApp/Infrastructure/AvatarWebRtcClient.cs` — a separate SIPSorcery
  `RTCPeerConnection`, recvonly for video+audio, opened **once at exam start and held open for
  every question in the attempt** (mirrors `WebRtcClient.cs`'s offer/answer structure against the
  new avatar WebRTC offer endpoint, but consumes inbound tracks and renders/plays them instead of
  encoding and sending). Explicitly not a modification of `Infrastructure/WebRtcClient.cs`.
- `VoxOralExam.DesktopApp/Services/MicAudioStreamer.cs` — extends/wraps the existing
  `TurnAudioRecorder` capture loop to additionally emit chunked PCM frames continuously (not
  just whole-turn buffers) to `RealtimeSessionClient`. `TurnAudioRecorder.cs`'s existing
  pre-roll/turn-buffer logic stays for archival upload (Phase 1's `TurnAudioUploader` still needs
  a whole-turn WAV for `/turns/archive`); this class adds the continuous streaming side. Leaning
  toward extending `TurnAudioRecorder` with a `StreamChunkAvailable` event so there is exactly one
  NAudio capture device opened, not two — confirm when this phase starts (see Open Questions).
- `VoxOralExam.DesktopApp/Services/RealtimeExamFlowService.cs` — implements `IExamFlowService`
  (same events/signature), replacing Phase 1's stub. Opens `RealtimeSessionClient` and
  `AvatarWebRtcClient` **once** at exam start (not per question), then for each question in
  `ExamSessionState`'s list: sends `question_start`, runs the turn loop via `MicAudioStreamer`,
  and on `should_continue=False` moves to the next question over the **same** connections. Still
  uses `TurnAudioUploader`/`TurnArchiveClient` from Phase 1 unchanged for the
  S3-upload-then-`/turns/archive` step per turn — this is the one piece of the turn pipeline that
  must stay byte-identical to today's shape per the hard constraint.
- `VoxOralExam.DesktopApp/Controls/AvatarVideoHost.xaml` (+ `.xaml.cs`) — new lightweight WPF
  control hosting the rendered avatar video for the whole exam (no Tavus host to coexist with
  anymore — it was deleted in Phase 1).

**Modified files:**
- `VoxOralExam.DesktopApp/App.xaml.cs` — register the new classes as singletons.
- `VoxOralExam.DesktopApp/Views/ExamWindow.xaml` — add `AvatarVideoHost`, visible whenever the
  exam is active.
- `VoxOralExam.DesktopApp/appsettings.json` / `AppSettings.cs` — add the real
  `RealtimeWebSocketUrl` (e.g. `ws://{PythonBaseUrl}/realtime/attempts`) and
  `AvatarWebRtcOfferPath` (e.g. `/avatar/webrtc/offer`) settings now that Phase 4 has finalized
  the actual endpoint shapes.

**Reused:** `IExamFlowService` (unchanged), `TurnAudioRecorder` (extended, not replaced),
`TurnAudioUploader`/`TurnArchiveClient` from Phase 1 (verbatim), SIPSorcery WebRTC stack already
referenced in the `.csproj`, DI/config patterns from `App.xaml.cs`.

**Net-new:** the attempt-scoped WebSocket client, the persistent recvonly avatar WebRTC client,
the streaming-mic adapter, the real flow-service implementation, the new avatar video XAML
control.

**Verification before moving to Phase 6:**
- Run one full exam attempt with **multiple questions** end-to-end manually over a single
  WebSocket + single avatar WebRTC connection: confirm there is no visible/audible interruption
  between questions (no reconnect, no re-join) — this is the concrete test for the
  no-Tavus-style-gap requirement.
- For each turn: confirm the S3 key shape and `/turns/archive` payload are byte-identical in
  shape to what the old Tavus flow produced, and confirm a Kafka publish fires per turn (not
  batched) with the correct `turn_order`.
- Confirm proctoring (camera preview + YOLO events) continues working unaffected throughout, on
  its own independent WebRTC connection.
- Kill and restart the WPF app mid-exam (simulating a crash) and confirm `RealtimeSessionClient`'s
  reconnect + `resume` handshake correctly picks up from the last archived turn without
  duplicating or losing any turn.

---

## Phase 6 — End-to-end hardening and cutover readiness

Goal: close the gap between "works in a manual demo" and "safe to use for a real graded exam,"
without changing any of the turn-persistence contract, and fully exercise the fault-tolerance
behavior designed into Phases 2/5.

**Concrete actions (mostly modifications, few new files):**
- Flesh out `RealtimeSessionClient`'s reconnect/retry policy (backoff, max attempts, user-visible
  status) and the full `resume` handshake client-side logic (compare server's
  `last_archived_turn_order` against local state, skip re-archiving already-archived turns,
  restart live STT/VAD cleanly for whatever turn was interrupted).
- Add the same timeout/silence-handling semantics WPF already has today
  (`InitialSilenceTimeoutSeconds`, `SilenceTimeoutAfterRepeatSeconds`, `QuestionTurnTimeoutSeconds`,
  `MaxTurnsPerQuestion` from `AppSettings.cs`) into `RealtimeExamFlowService`, reusing the same
  config keys.
- Define and implement a clear failure/fallback policy for "avatar rendering pipeline is
  unhealthy mid-exam" (GPU OOM, MuseTalk crash) — at minimum, fall back to audio-only TTS
  playback rather than freezing the exam, but the exact behavior is the user's call (see Open
  Questions).
- Load-test `gpu_scheduler.py` under realistic concurrent-student load (the expected concurrency
  number is currently unknown — needs to come from the user/infra side before this phase can be
  sized properly).
- **Fault-injection test suite specifically for the idempotency/durability design:** kill the
  Python process mid-turn and restart it, confirm `turn_publisher.py`'s durable
  published-turn-order tracking survives and no duplicate/missing Kafka events result; drop the
  WebSocket mid-turn repeatedly and confirm `resume` always converges to the correct state; retry
  the same `/turns/archive` call multiple times and confirm `append_turn_node`'s idempotency
  guard holds.
- Final acceptance test: run several full exams (multiple questions, multiple follow-up
  scenarios, at least one repeat-question scenario, one max-turns scenario, at least one
  injected mid-exam disconnect) and confirm the per-turn Kafka `answer-turns-recorded` payloads
  are correct, complete, and free of duplicates.

**Verification:** the acceptance test above (including the fault-injection cases) is the gate
for considering this pipeline ready for a real graded exam.

---

## Open questions (the user's call, not silently decided)

1. **Avatar expression/gesture schema** — extend `followup_decision_node`'s output with
   expression/gesture fields, or keep the decision node untouched and derive avatar expression
   separately in `avatar_renderer.py`? This plan assumes the latter (lowest-risk, reuses the node
   verbatim) unless told otherwise.
2. ~~Where to run the Phase 0 LivePortrait/MuseTalk PoC~~ — **RESOLVED 2026-06-25**: the dev
   machine has a real GPU (RTX 2000 Ada, ~8GB VRAM, confirmed via `nvidia-smi`), so the PoC runs
   locally on it once torch is swapped for a CUDA build (the earlier CPU-only torch was just a
   wrong install, not a hardware gap). 8GB VRAM is modest, though — if LivePortrait+MuseTalk
   together don't fit, that's a real constraint to size against (see quantization mitigation
   above) rather than a sign the approach is wrong.
3. **Acceptable avatar render latency/frame-rate threshold** — Phase 0's go/no-go gate needs a
   concrete number (this plan suggests ~15-20fps as a strawman). Is a brief "thinking" pause
   acceptable while rendering, or must it be fully real-time?
4. **GPU scheduling policy between avatar rendering and YOLO proctoring** — should avatar always
   preempt proctoring, round-robin, or should proctoring auto-throttle while a render is in
   flight? Depends on Phase 0's GPU headroom numbers, but the policy itself is a product call.
5. **Failure-mode behavior for avatar pipeline crashes mid-exam** — audio-only TTS fallback (no
   video) was suggested as a safe default; alternatives are a student-facing error state or
   pausing the exam for proctor intervention. Real exam-integrity implications either way.
6. **Exact Azure realtime STT/VAD product/SDK name and packaging** — "Azure Voice Live" naming/
   availability should be re-verified at the start of Phase 0 against current Azure docs/SDK
   contents, not assumed from this plan's description.
7. **The `vox-streaming` Go repo** — exists at `d:\semester9\vox-streaming` (an aiortc-style
   WebRTC broadcaster/recorder) but isn't referenced anywhere by the three in-scope repos. Is it
   dead/exploratory code to ignore (this plan's assumption), or should the new avatar-publishing
   WebRTC logic actually live there instead of in `agents` (Python)? If the latter, Phase 4's
   `avatar_webrtc.py`/`avatar_webrtc_controller.py` would move to Go instead.
8. **`MicAudioStreamer` vs. extending `TurnAudioRecorder` directly** — Phase 5 leans toward
   adding a streaming-chunk event to the existing `TurnAudioRecorder` rather than opening a
   second NAudio capture device, but this is an implementation detail worth the user's sign-off
   when that phase starts.
9. **Concurrent exam session capacity on the GPU pod** — unknown today; needed to size
   `gpu_scheduler.py`'s policy/queue depth in Phase 4/6, and to decide whether the realtime
   avatar pipeline supports multiple simultaneous exam-takers per GPU or needs one-exam-per-GPU.
10b. **In-process vs. subprocess/separate-service for LivePortrait/MuseTalk in Phase 4** —
    discovered during Phase 0 (2026-06-25): LivePortrait and MuseTalk each pin exact, mutually
    incompatible dependency versions (LivePortrait hardcodes `torch==2.3.0` CPU index;
    numpy 1.26.4 vs. MuseTalk's 1.23.5 vs. the main app's 2.3.3; `opencv-python` GUI build vs.
    the main app's `opencv-python-headless`; MuseTalk also pulls in `tensorflow==2.12.0`). The
    Phase 0 PoC works around this with a separate venv (`agents/spikes/.venv-avatar`,
    documented in `spikes/README.md` section 2b), never installing either repo's deps into the
    main `agents/.venv`. For Phase 4's `avatar_renderer.py`, decide deliberately: reconcile
    dependencies in-process (likely requires patching LivePortrait/MuseTalk for numpy 2.x and
    relaxing their exact pins — real, nontrivial work, with risk of subtly breaking what they
    were tested against), or call out to a subprocess/separate service using a dedicated
    environment permanently (avoids the conflict entirely, and is a common pattern for isolating
    heavy/crash-prone ML inference from a request-handling process anyway — may be the better
    architecture regardless of the dependency conflict). Don't back into this by default;
    decide it explicitly when Phase 4 starts.
10. **Durable idempotency storage mechanism** — this plan assumes extending `archive_graph`'s
    existing Postgres-checkpointed state (e.g. a `published_turn_orders` field) is the right place
    to track "already published to Kafka," rather than introducing a separate table/store. This
    keeps everything inside the one persistence mechanism already in use and avoids any new
    infrastructure, but flagging it as a design choice rather than a forced one.

---

### Critical files for implementation
- `DesktopApp/VoxOralExam/VoxOralExam.DesktopApp/Services/TavusFullPipelineExamFlowService.cs` (being deleted — read before deleting)
- `DesktopApp/VoxOralExam/VoxOralExam.DesktopApp/Services/TurnAudioRecorder.cs`
- `DesktopApp/VoxOralExam/VoxOralExam.DesktopApp/App.xaml.cs`
- `agents/src/controller/tavus_controller.py` (being deleted — read before deleting)
- `agents/src/node/followUpDecisionGraph/graphConfig.py`
- `agents/src/controller/webrtc.py`
