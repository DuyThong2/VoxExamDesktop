namespace VoxOralExam.DesktopApp.State;

public class AppSettings
{
    public string JavaBaseUrl { get; set; } = "http://localhost:8080";
    public string PythonBaseUrl { get; set; } = "http://localhost:8000";
    public string WebRtcSignalingUrl { get; set; } = "ws://localhost:8081/signaling";
    public string RealtimeWebSocketPath { get; set; } = "/realtime/attempts";
    public string AvatarWebRtcOfferPath { get; set; } = "/avatar/webrtc/offer";
    public int MaxTurnsPerQuestion { get; set; } = 3;
    public int QuestionTurnTimeoutSeconds { get; set; } = 180;
    public int InitialSilenceTimeoutSeconds { get; set; } = 8;
    public int SilenceTimeoutAfterRepeatSeconds { get; set; } = 12;
    public int PostSpeechSilenceGracePeriodSeconds { get; set; } = 2;
    public int AvatarSpeechMaxWaitSeconds { get; set; } = 25;
    public int TurnAudioPreRollMilliseconds { get; set; } = 400;
    public int TurnAudioTailMilliseconds { get; set; } = 500;
    public int CameraDeviceIndex { get; set; } = 0;
    public int CameraWidth { get; set; } = 640;
    public int CameraHeight { get; set; } = 480;
    public int CameraFps { get; set; } = 30;
    public string LoginPlatform { get; set; } = "DESKTOP";

    // Dev-only: true serves exam data from MockExamDataFactory; false uses ExamApiService (real
    // Java backend). Defaults true so the app runs before Java's exam endpoints exist.
    public bool UseMockData { get; set; } = true;
}
