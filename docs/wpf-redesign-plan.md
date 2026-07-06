> Saved 2026-07-06. Consolidates a multi-turn design discussion into one plan. This is the
> **WPF desktop redesign** plan: it does not replace `realtime-self-hosted-avatar-plan.md` (the AI
> examiner / avatar pipeline), it sits beside it and re-frames how the student client is structured
> around it. Where the two overlap (mic capture, per-turn archive) this doc defers to the avatar
> plan's turn contract and only changes *who owns what* and *when things happen*.

# WPF desktop redesign — server-authoritative exam flow, OTP entry, device pre-flight, lockdown

## Context

`VoxOralExam.DesktopApp` today does far more than its intended role. It was described as a thin
student client that only streams + records camera/screen, but the code actually orchestrates the
whole exam: it runs the question loop, drives VAD/turn detection, talks to the AI avatar, uploads
turn audio to S3 with **static AWS credentials baked into the client**, calls Python's
`/evaluate/turn`, and decides when the exam is finished — all on the student's machine. Exam data
and the exam list are **mock** (`MockExamDataFactory`). There is **no OTP flow**, **no screen
recording**, and **no background-app/lockdown enforcement** despite those being requirements. The
proctoring WebRTC path points at **Python**, while the purpose-built Go service
(`vox-streaming`, see `.claude/VIDEO_STREAMING.md`) is not wired in at all.

Decisions taken during the design discussion (2026-07-06):

1. **Client split (confirmed):** students stream from the **WPF app**; teachers / school admins
   monitor from a **web** client. The web monitor side is exactly what `vox-streaming` already
   targets — it needs no redesign. The WPF streaming side is the real work: `vox-streaming`'s
   `/ws/stream` contract assumes a browser, so WPF must be adapted to it.
2. **Machine types (confirmed):** exams run on **both** managed school-lab machines **and** student
   personal devices (BYOD). This is solved with a single codebase via **capability tiers +
   server-side policy**, not two apps.
3. **Entry sequencing (confirmed):** device checks (camera/screen/mic/audio) and the new
   background-app / lockdown check move **out of login** and happen **after a successful OTP**,
   right before entering the exam — the standard pre-flight pattern.

## Intended outcome

- The student client becomes **thin and server-driven**: the server owns exam-attempt identity,
  the question loop's authoritative decisions, and all secrets. The client captures media, runs
  local device/lockdown checks, streams, and renders — it never holds AWS credentials and never
  mints its own `attemptId`.
- A clear multi-stage entry flow with an explicit state machine:
  `Login → ExamList → OtpEntry → SystemCheck → DevicePreflight → InExam → Submitted`.
- Lockdown is **defense-in-depth, not a guarantee** — it deters casual cheating; the real backstop
  is server-side recording + AI proctoring. Detection is separated from enforcement; enforcement is
  tiered (Service / Helper / DetectOnly) and the active tier is reported to the server, which
  applies a per-exam minimum-tier policy.
- Streaming transport moves from WPF→Python (VP8, HTTP one-shot offer) to WPF→`vox-streaming`
  (H.264, `/ws/stream` WebSocket signaling with ICE trickle, camera + screen + audio). Python drops
  its inbound WebRTC role and becomes a pure AI consumer of Kafka `exam.frame.ready`.

## Scope

`VoxOralExam.DesktopApp` / `VoxOralExam.Core` is the primary scope. Java (`vox`), Python
(`agents`), and Go (`vox-streaming`) changes are **flagged as cross-repo dependencies**, not
implemented here — but several of them are hard prerequisites without which the WPF flow cannot run
end-to-end (called out in §F).

---

## Ground truth (verified by code reading, 2026-07-06)

- Static AWS credentials in the client: `TurnAudioUploader.CreateS3Client` builds `IAmazonS3` from
  `AppSettings.AwsAccessKeyId`/`AwsSecretAccessKey`. The app runs on the student's machine → keys
  are extractable. **Must be removed.**
- Client mints its own attempt id: `RealtimeExamFlowService.EnsureSessionInitialized` does
  `_sessionState.ExamAttemptId = Guid.NewGuid()` — not tied to any server record.
- Client owns exam-integrity decisions: `MaxTurnsPerQuestion`, timeouts, "question done", "exam
  complete" are all decided in `RealtimeExamFlowService`.
- Mock data wired as a real service: `MainViewModel`/`ExamViewModel` call `MockExamDataFactory`
  directly; no real exam-list/paper API call exists.
- No OTP anywhere (repo-wide search empty). Login is username/password → Java `/api/v1/auth/login`.
- No screen capture anywhere. "Proctoring" is camera → `WebRtcClient` (VP8, sendonly) → Python
  `/webrtc/offer` → YOLO. No audio track is sent.
- `vox-streaming` (Go) is not referenced by any in-scope client code.
- Codec mismatch: `WebRtcClient` encodes **VP8**; `vox-streaming`'s recording pipeline is built on
  **H.264** (`-c copy`, no transcode). Incompatible as-is.
- Camera start is jammed into `ExamViewModel.InitializeAsync`/`StartCameraAsync`, not a separate
  pre-flight stage.

---

## A. Navigation + entry state machine

Today navigation is scattered: `App.OnStartup` shows `LoginView`; `MainViewModel.StartExam` news up
`ExamWindow` directly; camera lives inside `ExamViewModel`. There is no single source of truth for
"which stage are we in."

**Add** an explicit stage concept + a navigator that owns transitions (with back/retry):

```csharp
// State/ExamEntryStage.cs  (new)
public enum ExamEntryStage
{
    Login,
    ExamList,
    OtpEntry,        // new
    SystemCheck,     // new — background-app + virtual-device scan
    DevicePreflight, // new — camera/screen/mic/audio test (MOVED here from login)
    InExam,
    Submitted,
    Error
}
```

**New files:**
- `ViewModels/OtpEntryViewModel.cs` + `Views/OtpEntryView.xaml`
- `ViewModels/SystemCheckViewModel.cs` + `Views/SystemCheckView.xaml`
- `ViewModels/DevicePreflightViewModel.cs` + `Views/DevicePreflightView.xaml`
- `Services/IExamEntryNavigator.cs` + implementation — holds current stage, transitions, back/retry.

**Modified files:**
- `App.xaml.cs` — register the new VM/Views; navigate via the navigator instead of hard-coded
  `Show()`.
- `ViewModels/ExamViewModel.cs` — **remove** `StartCameraAsync`/pre-flight from here; on entering
  `InExam` the camera/mic are already warm from `DevicePreflight`, so it only hands them off. Pass a
  real `CancellationToken` instead of `CancellationToken.None`.
- `ViewModels/MainViewModel.cs` — `StartExam` no longer opens `ExamWindow`; it advances the
  navigator to `OtpEntry`.

---

## B. Lockdown subsystem — detection separated from enforcement

New folder `Lockdown/`. The single most important architectural rule: **detection is identical on
every machine; only enforcement is tiered** (detecting needs far less privilege than killing).

```csharp
// Lockdown/EnforcementTier.cs
public enum EnforcementTier { Service, Helper, DetectOnly }

// Lockdown/Models.cs
public record DetectedProcess(int Pid, string Name, string? Path,
    bool IsElevated, BlockCategory Category);

public enum BlockCategory
{
    RemoteControl,   // AnyDesk, TeamViewer, RustDesk, Chrome Remote Desktop
    ScreenRecorder,  // OBS, Bandicam, ShareX
    VirtualCamera,   // OBS VirtualCam — high priority: defeats camera proctoring
    VirtualAudio,    // VB-Cable, virtual audio cables
    VirtualMachine,  // running inside a VM
    Communication,   // Discord, Zoom, Telegram
    Unknown
}

public record SystemScanResult(
    IReadOnlyList<DetectedProcess> Blocked,
    IReadOnlyList<string> VirtualCameras,
    IReadOnlyList<string> VirtualAudioDevices,
    int MonitorCount,
    bool IsInsideVm);

// Lockdown/ISystemDetector.cs — SAME on every tier (detect only, never kills)
public interface ISystemDetector
{
    Task<SystemScanResult> ScanAsync(Blocklist blocklist, CancellationToken ct);
    // enumerate Process, DirectShow/MediaFoundation devices, Screen.AllScreens,
    // WMI/registry VM markers
}

// Lockdown/ILockdownEnforcer.cs — TIERED (differs only in how it closes)
public interface ILockdownEnforcer
{
    EnforcementTier Tier { get; }
    Task<CloseResult> CloseAsync(IEnumerable<DetectedProcess> targets, CancellationToken ct);
}
// impls: ServiceEnforcer   (named-pipe IPC → pre-installed Windows Service running as SYSTEM)
//        HelperEnforcer    (on-demand elevated helper via UAC 'runas', once per session)
//        DetectOnlyEnforcer(can only kill same-user processes + report; no guarantee)

// Lockdown/IEnforcerProbe.cs — picks the tier at SystemCheck, falling back down
public interface IEnforcerProbe
{
    Task<ILockdownEnforcer> ResolveAsync(CancellationToken ct);
    // try Service (pipe reachable?) → Helper (UAC accepted?) → DetectOnly
}
```

**Key implementation notes:**
- The admin requirement is **not absolute**: killing **same-user** processes (AnyDesk/OBS running as
  the student) usually works **without** elevation. Admin/service is only needed for elevated
  processes, launch-blocking, reliable enumeration, and strong lab lockdown.
- **Blocklist is fetched from the server** (carried in the entry ticket or a dedicated endpoint),
  never hard-coded — so it can be updated without redeploying the app.
- **Virtual camera / virtual audio detection is higher priority than killing chat apps** — a virtual
  cam feeds pre-recorded video and defeats the entire camera-proctoring premise.
- **Continuous monitoring during the exam:** `Services/LockdownMonitorService.cs` (new) scans every
  1–2s for the whole exam and pushes findings to the server as proctoring events (same alert channel
  the AI uses → teacher monitor). A one-time gate is not enough.
- **No kernel driver / WDAC** — a poll-and-kill loop is enough; don't over-invest in an arms race.
  Anti-tamper = sign the binaries + have the service verify the caller's signature.
- The elevated **helper** and the **Windows Service** are separate projects
  (`VoxOralExam.LockdownHelper`, `VoxOralExam.LockdownService`) with their own manifests; the main
  app stays non-elevated.

**Tier + policy:** the client reports the active tier + scan result to the server; the **server**
decides whether that tier may proceed for this exam (e.g. high-stakes graded exam → require
`Service` tier → lab machines only; practice exam → allow `DetectOnly`). The client never decides
pass/fail itself. This is what lets one codebase serve both lab and BYOD cleanly.

**Unified UX** (same screen every tier, only the button outcome differs):
```
These applications must be closed to continue:
  • AnyDesk        • OBS Studio (virtual camera)
[ Close them for me ]   [ I'll close them → Re-scan ]
```
Service/Helper: "Close for me" executes immediately. DetectOnly: it kills what it can (same-user),
then falls back to manual-close + re-scan for the rest.

---

## C. OTP + entry ticket

**New files:**
- `Services/IExamEntryApiService.cs` + impl — calls Java: `VerifyOtpAsync(examId, otp)` returns an
  **entry ticket** containing: server-generated `attemptId` (replacing the client's `Guid.NewGuid()`),
  the **stream JWT** (for `vox-streaming`), the lockdown blocklist, `minEnforcementTier`, and the
  presigned-upload endpoint/URL source.
- `State/ExamEntryTicket.cs` (new).

**Modified files:**
- `State/ExamSessionState.cs` — hold the ticket; remove the self-minted `attemptId`.

**Sequencing rule:** OTP rotates every 60s, but device/system checks take minutes. So **OTP is
verified once → it yields the ticket**; all subsequent checks run while holding the ticket; entry
uses the ticket, **never re-validating the (now-expired) OTP**. The ticket has its own longer
validity.

---

## D. Security + de-mock (small, do early)

**Remove AWS credentials from the client:**
- `State/AppSettings.cs` — delete `AwsAccessKeyId` / `AwsSecretAccessKey` / `AwsSessionToken`.
- `Services/TurnAudioUploader.cs` — **rewrite**: drop `IAmazonS3`/`CreateS3Client`; instead request
  a **presigned PUT URL** from the server keyed by `(attemptId, turnOrder)` and `HttpClient.PutAsync`
  to it. Remove `AWSSDK.S3` from `VoxOralExam.DesktopApp.csproj`.

**De-mock:**
- `Services/MockExamDataFactory.cs` — gate behind a dev-only config flag.
- `MainViewModel`/`ExamViewModel` — call a real `IExamApiService` (Java) behind an interface for the
  exam list and exam paper.

---

## E. Streaming transport (WPF → vox-streaming Go)

**Rewrite** `Infrastructure/WebRtcClient.cs` into:
- `Infrastructure/StreamingSignalingClient.cs` — WebSocket `/ws/stream`, `offer/answer/ice-candidate`
  flow with **ICE trickle** (today it POSTs a single offer, no trickle), stream JWT in the query.
- Switch codec **VP8 → H.264** (drop `VP8Codec`), using `SIPSorceryMedia.FFmpeg`.
- Add an **Opus audio track** and a **second peer for screen** (`streamType=screen`), which requires
  new desktop capture (Windows Graphics Capture / DXGI).
- Replace `Debug.WriteLine` with `LocalFileLogger` for consistency.

**New file:** `Services/MediaCaptureHub.cs` — a mic/camera hub that opens each device **once** and
fans out to: (a) Opus → Go recording, (b) PCM → avatar Voice Live, (c) WAV turn buffer → archive.
Today `TurnAudioRecorder`, `MicAudioStreamer`, and `CameraService` each open devices independently —
this hub prevents contention (ties into `DevicePreflight` handing warm devices to `InExam`).

**Retire the Python proctoring path:** `Services/ScreenProctoringService.cs` no longer pushes frames
to Python; proctoring alerts arrive via the Go monitor pipeline instead.

---

## F. Cross-repo dependencies (outside the WPF repo, but hard prerequisites)

- **Java (`vox`):** issue the **stream JWT** (verify OTP → mint ticket); implement gRPC `ExamService`
  (`ValidateAccess` / `UpdateRecording`); add a **presigned PUT** endpoint for turn audio; provide a
  real exam-list / exam-paper API; add the **Kafka consumer for `answer-turns-recorded`** (confirmed
  missing today → scores never persist without it).
- **Python (`agents`):** drop inbound `/webrtc/offer`; become a **consumer of `exam.frame.ready`**
  reading the `.264` frames, pushing alerts back via gRPC `PushAlert` to Go.
- **Go (`vox-streaming`):** mostly as-is; patch the items already noted in the doc review — recording
  primary vs. WebM-fallback precedence (don't `-c copy`-concat WebM + fMP4 together), `frameUrl` as
  reconstructable key vs. presigned, emit-or-remove `exam.room.closed`, unify time formats
  (`capturedAtMs` vs RFC3339), Opus-in-MP4 playback verification for teacher review.

---

## Suggested implementation order

1. **D** — remove AWS creds + stand up the entry-ticket skeleton (closes the worst hole, lays the
   server-authoritative foundation).
2. **C + A** — OTP → entry-ticket → state machine → move device checks after OTP.
3. **B** — lockdown: Detection first (usable on every tier), then the Service/Helper enforcers.
4. **E + F** — switch transport to Go, shrink Python's role, add the Java prerequisites. Do this
   after Java is ready, since it's the gating dependency.

---

## Open questions (the user's call)

1. **Avatar coexistence:** the recording/proctoring media path (WPF → Go) and the AI-examiner avatar
   path (WPF ↔ Python realtime) stay two independent transports sharing only capture sources. Confirm
   they should remain fully separate (this plan assumes yes).
2. **BYOD minimum tier:** is a graded exam ever allowed on `DetectOnly`, or must graded exams require
   `Service` (lab only)? Drives the server policy and how strict BYOD is.
3. **Screen-recording retention for minors:** camera + screen of high-school students stored for up
   to 365 days needs a consent / retention / access-control policy (Nghị định 13/2023). Must be
   defined before this ships, not after.
4. **Data-retention owner:** who purges expired recordings/frames and enforces access to them —
   Java, Go lifecycle rules, or storage TTL alone?
