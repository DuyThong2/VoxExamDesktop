namespace VoxOralExam.DesktopApp.State;

public class AppSettings
{
    public string JavaBaseUrl { get; set; } = "http://localhost:8080";
    public string PythonBaseUrl { get; set; } = "http://localhost:8000";
    public string WebRtcSignalingUrl { get; set; } = "ws://localhost:8081/signaling";
    public string RealtimeWebSocketPath { get; set; } = "/realtime/attempts";
    public string AvatarWebRtcOfferPath { get; set; } = "/avatar/webrtc/offer";
    public int MaxTurnsPerQuestion { get; set; } = 5;
    public int QuestionTurnTimeoutSeconds { get; set; } = 180;
    public int InitialSilenceTimeoutSeconds { get; set; } = 12;
    public int SilenceTimeoutAfterRepeatSeconds { get; set; } = 18;
    public int PostSpeechSilenceGracePeriodSeconds { get; set; } = 4;
    public int AvatarSpeechMaxWaitSeconds { get; set; } = 25;
    public int TurnAudioPreRollMilliseconds { get; set; } = 400;
    public int TurnAudioTailMilliseconds { get; set; } = 500;
    public int CameraDeviceIndex { get; set; } = 0;
    public int CameraWidth { get; set; } = 640;
    public int CameraHeight { get; set; } = 480;
    public int CameraFps { get; set; } = 30;
    public string LoginPlatform { get; set; } = "DESKTOP";

    // Length of the OTP the proctor's screen shows and the student types.
    // Matches Java's GetExamScheduleOtpUseCase.OTP_LENGTH.
    public int OtpLength { get; set; } = 8;

    // How often the proctor's OTP rotates, in seconds -- drives the countdown on the OTP screen.
    public int OtpRefreshSeconds { get; set; } = 60;

    // Dev-only: true serves exam data from MockExamDataFactory; false uses ExamApiService (real
    // Java backend). Defaults true so the app runs before Java's exam endpoints exist.
    public bool UseMockData { get; set; } = true;

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
