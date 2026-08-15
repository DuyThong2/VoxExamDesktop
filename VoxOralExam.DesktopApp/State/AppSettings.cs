namespace VoxOralExam.DesktopApp.State;

public class AppSettings
{
    public string JavaBaseUrl { get; set; } = "http://localhost:8081";
    public string PythonBaseUrl { get; set; } = "http://localhost:8000";
    public string StreamingBaseUrl { get; set; } = "http://localhost:8082";
    public bool EnableLocalRecording { get; set; } = true;
    public bool EnableSegmentUpload { get; set; } = true;

    // Live WebRTC view for the monitor UI (separate from local recording/segment upload above).
    // Encodes H.264 through the Media Foundation encoder that ships with Windows (see
    // MediaFoundationH264Encoder), so it needs no bundled native libraries -- only Windows N/KN
    // editions without the Media Feature Pack lack an encoder, and there the live view degrades
    // while local recording is unaffected.
    public bool EnableLiveMonitorStream { get; set; } = true;

    // Target bitrate for the live monitor video tracks. Deliberately well below the
    // Screen/CameraRecordingBitrate values above: those govern the durable evidence recording that
    // gets graded, whereas this is a best-effort real-time view over whatever network the school
    // has, encoded CBR so a busy screen cannot balloon into a stall.
    public int MonitorStreamVideoBitrate { get; set; } = 1_500_000;

    // vox-streaming's /ws/stream upgrader rejects the WebSocket handshake with 403 unless the
    // request's Origin header is in its own ALLOWED_ORIGINS (default http://localhost:5173, the
    // browser demo's nginx-proxied origin) -- .NET's ClientWebSocket sends no Origin header at all
    // by default, unlike a browser, so MonitorStreamClient sets this one explicitly. Must match
    // whatever ALLOWED_ORIGINS the target vox-streaming instance is actually configured with.
    public string MonitorStreamOrigin { get; set; } = "http://localhost:5173";
    public bool RequireRecording { get; set; } = true;
    public int RecordingSegmentSeconds { get; set; } = 10;
    public int RecordingUploadTimeoutSeconds { get; set; } = 30;
    public int RecordingFinalDrainSeconds { get; set; } = 20;
    public int RecordingAuditTimeoutSeconds { get; set; } = 3;
    public int ScreenRecordingFps { get; set; } = 30;
    public int ScreenRecordingBitrate { get; set; } = 4_000_000;
    public int CameraRecordingBitrate { get; set; } = 2_000_000;
    public int RecordingQueueCapacity { get; set; } = 4;
    public long MinimumRecordingDiskBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    // How often to re-check free disk space while recording. The start-of-attempt check alone is not
    // enough: screen and camera together write roughly 2.7 GB/hour at their configured bitrates, and
    // an attempt whose uploads are failing keeps every segment on disk, so the drive can cross the
    // threshold long after recording began. 0 disables the periodic check.
    public int RecordingDiskCheckSeconds { get; set; } = 30;

    // How long before an upload credential expires to start renewing it. Must be comfortably longer
    // than any outage the renewal itself has to survive: the renewal needs the network, so trying
    // only at the last minute means the one situation that makes renewal necessary -- being offline
    // -- is also the one that prevents it.
    public int UploadCredentialRefreshLeadMinutes { get; set; } = 10;
    public string RealtimeWebSocketPath { get; set; } = "/realtime/attempts";
    public string AvatarWebRtcOfferPath { get; set; } = "/avatar/webrtc/offer";
    public int MaxTurnsPerQuestion { get; set; } = 10;
    public int QuestionTurnTimeoutSeconds { get; set; } = 180;
    public int InitialSilenceTimeoutSeconds { get; set; } = 12;
    public int SilenceTimeoutAfterRepeatSeconds { get; set; } = 18;
    public int PostSpeechSilenceGracePeriodSeconds { get; set; } = 4;
    public int AvatarSpeechMaxWaitSeconds { get; set; } = 25;

    // --- Shutdown budget -------------------------------------------------------------------
    // Every step of "the exam is ending" needs its own deadline, because the normal ceilings are
    // sized for reconnect recovery (QuestionTurnTimeoutSeconds is 180s) and a student staring at
    // a "dang luu" overlay cannot be made to wait that out. Total worst case is ~41s.
    //
    // These matter because a turn only reaches Java by a long chain: PCM -> S3 -> Python's
    // POST /turns/archive -> the websocket turn_end handshake -> Python publishes
    // AnswerTurnsRecorded -> Kafka -> Java writes the turn row. Java grades synchronously inside
    // the PATCH that marks the session SUBMITTED, so anything still in flight when that PATCH
    // lands exists in the database but is never graded.

    // Grace given to the turn_end handshake after the run token is cancelled on submit. Server
    // work is one transcript flush (3s hard cap) plus one LLM decision, typically 2-8s.
    public int SubmitTurnEndGraceSeconds { get; set; } = 10;

    // Same, for the stop / proctoring force-end paths. Shorter on purpose: nothing is being
    // graded, and after a force_end Python has already closed the socket so the send fails fast.
    public int InterruptTurnEndGraceSeconds { get; set; } = 5;

    // Ceiling for exam_end + its farewell utterance. Previously unbounded (CancellationToken.None
    // over a 180s ack ceiling plus a 60s avatar wait = 240s of a student watching nothing).
    public int SubmitFarewellTimeoutSeconds { get; set; } = 20;

    // Ceiling for draining pending turn uploads before the session is marked SUBMITTED.
    // NOTE: Python's archival runs Azure STT continuous recognition, which scales with audio
    // length -- a 60-90s answer can take tens of seconds to land. Raise this (env
    // FINAL_ARCHIVE_DRAIN_TIMEOUT_SECONDS) if 'pending_archives_incomplete' shows up in the logs.
    public int FinalArchiveDrainTimeoutSeconds { get; set; } = 20;

    // Trần cho MỘT lời gọi POST /turns/archive.
    //
    // Trước đây không ai đặt, nên nó ăn mặc định 100 giây của HttpClient -- lớn hơn cả cửa dọn
    // 20 giây ngay trên. Hệ quả đo được 2026-08-13: kết nối bị cắt giữa chừng lúc 10:25:35
    // (SocketException 995) nhưng mãi 10:27:15 client mới biết, tức 100 giây sau, và lúc đó bài
    // đã nộp xong từ lâu. Một cửa dọn 20 giây không bao giờ chờ nổi request được phép chạy 100.
    //
    // 25 giây đủ rộng cho Azure STT trên câu trả lời dài, mà vẫn phát hiện kết nối chết kịp để
    // lượt sau còn xoay xở -- archive chạy ngay sau mỗi lượt nên có vài PHÚT đường băng, không
    // phải chỉ 20 giây cuối.
    public int ArchiveRequestTimeoutSeconds { get; set; } = 25;

    // Cầu nối giữa "Python đã publish lượt" và "Java đã ghi xong dòng turn": message còn phải
    // qua Kafka và consumer bên Java mới commit. Thiếu nó thì PATCH thắng cuộc đua và lượt
    // không được chấm.
    //
    // CẬP NHẬT 2026-08-13: mốc bắt đầu của cuộc đua này đã dịch sớm hẳn lên. Trước đây phải chờ
    // POST /turns/archive về (có khi ~90 giây) rồi mới publish; giờ turn_publisher publish pha
    // sơ bộ ngay khi quyết định follow-up xong, tức vài giây sau khi thí sinh dứt lời. 3 giây ở
    // đây giờ chỉ còn phải phủ chặng Kafka -> consumer, rộng rãi hơn nhiều so với lúc đặt ra.
    public int PostArchiveSettleSeconds { get; set; } = 3;

    // Floor for salvaging an aborted capture. TurnAudioRecorder always seeds a turn with
    // TurnAudioPreRollMilliseconds of pre-roll, so anything near that floor holds no speech --
    // uploading it would create a junk turn row and cost Python a doomed 92s archive poll.
    public int MinimumSalvageAudioMilliseconds { get; set; } = 800;
    public int TurnAudioPreRollMilliseconds { get; set; } = 400;
    public int TurnAudioTailMilliseconds { get; set; } = 500;
    public int CameraDeviceIndex { get; set; } = 0;
    public int CameraWidth { get; set; } = 640;
    public int CameraHeight { get; set; } = 480;
    public int CameraFps { get; set; } = 30;
    public string StunUrls { get; set; } = "stun:stun.l.google.com:19302";
    public string TurnUrl { get; set; } = "";
    public string TurnUsername { get; set; } = "";
    public string TurnCredential { get; set; } = "";
    public string LoginPlatform { get; set; } = "DESKTOP";

    // Length of the OTP the proctor's screen shows and the student types.
    // Matches Java's GetExamScheduleOtpUseCase.OTP_LENGTH.
    public int OtpLength { get; set; } = 8;

    // Dev-only: true serves exam data from MockExamDataFactory; false uses ExamApiService (real
    // Java backend). Defaults true so the app runs before Java's exam endpoints exist.
    public bool UseMockData { get; set; } = true;

    // Dev-only, only consulted while UseMockData is true: mint a real, signed vox-streaming JWT
    // from vox-streaming/demo/devserver (see that folder's README) instead of the hardcoded
    // "dev-stub-stream-jwt" in MockExamEntryApiService. Lets the client-side recording + segment
    // upload pipeline (Workers/SegmentUploadWorker.cs, Infra/Recording) run against a real,
    // locally-running vox-streaming instance while the rest of the exam content stays mocked --
    // mirrors how vox-streaming's demo/web Student page gets its token. Defaults false so plain
    // mock runs stay fully offline (no dependency on vox-streaming/Redis/Kafka/MinIO being up).
    public bool UseDevStreamToken { get; set; } = false;

    // Base URL of vox-streaming/demo/devserver's HTTP token endpoint (its own default is :8090).
    // Only read when UseDevStreamToken is true.
    public string DevStreamTokenUrl { get; set; } = "http://localhost:8090";

    // Dev-only: true shows Views/StreamingDemoWindow at startup instead of the normal
    // login/OTP/exam-paper flow (ShellWindow) -- the WPF analogue of vox-streaming's
    // demo/web/student.html. Lets you exercise camera/screen capture + the client-side
    // recording/segment-upload pipeline against a real vox-streaming instance without needing
    // exam content from the Java backend. Always mints its token via DevStreamTokenClient,
    // independent of UseMockData/UseDevStreamToken above.
    public bool LaunchStreamingDemo { get; set; } = false;

    // Prototype (see task/performance.txt): Azure TTS synthesized directly on WPF via
    // Services/LocalAvatarSpeaker.cs, instead of Python synthesizing and streaming it back over
    // the avatar WebRTC audio track. Same env var names as agents/.env's AZURE_SPEECH_KEY /
    // AZURE_SPEECH_REGION / AZURE_TTS_VOICE so the same dev Azure Speech resource can be reused
    // by copying those two values into this project's own .env -- see DotEnvLoader.ApplyOverrides.
    // A shipped client would need a short-lived token per attempt instead of this static key.
    public string AzureSpeechKey { get; set; } = "";
    public string AzureSpeechRegion { get; set; } = "";
    public string AzureTtsVoice { get; set; } = "en-US-JennyNeural";

    // Defaults false now that TTS moved to LocalAvatarSpeaker (see AzureSpeechKey above): the
    // avatar WebRTC connection (AvatarWebRtcClient, Python's controller/avatar_webrtc_controller.py
    // + realtime/avatar_webrtc.py) no longer carries anything meaningful -- its video track is
    // permanently hidden behind AvatarVideoHost.xaml's placeholder, and its audio track only ever
    // plays idle silence since Python stopped calling avatar_speech.speak(). Kept as a toggle
    // rather than deleting the connection/decode code outright: if a real hosted avatar (e.g.
    // Azure's realtime avatar synthesis, which is itself WebRTC-based) gets wired in later, this
    // SDP-offer/VP8-decode/PCMU-decode plumbing is the closest starting point, even though the
    // exact signaling would likely need to target that service directly rather than this
    // self-hosted Python aiortc server. Flip to true only for debugging/reviving this path.
    public bool EnableAvatarWebRtc { get; set; } = false;
}
