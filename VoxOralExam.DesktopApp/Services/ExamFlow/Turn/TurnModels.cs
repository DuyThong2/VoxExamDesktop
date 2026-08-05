using VoxOralExam.DesktopApp.Dtos;

namespace VoxOralExam.DesktopApp.Services.ExamFlow.Turn;

internal enum SpeechCaptureEndReason
{
    Completed,
    InitialSilenceTimeout,
    OverallTimeout,
    SpeechBudgetExceeded,
    SpeechWindowClosed,

    // Capture was aborted mid-answer by the run token (countdown hit zero, the student pressed
    // "Nop bai", or proctoring force-ended the attempt) and the recorder buffer was rescued
    // after the fact by SpeechTurnCoordinator.TrySalvageInFlightCapture. CaptureAsync unwinds
    // before CompleteCapture in that case, so without the rescue the audio is simply lost.
    Salvaged
}

internal sealed record CapturedTurn(
    int TurnOrder,
    byte[] Pcm,
    double DurationSeconds,
    SpeechCaptureEndReason EndReason)
{
    public bool SpeechBudgetExceeded => EndReason == SpeechCaptureEndReason.SpeechBudgetExceeded;
}

internal sealed record TurnArchiveWorkItem(
    Guid AnswerId,
    Guid PaperItemId,
    int TurnOrder,
    string PromptText,
    double DurationSeconds,
    byte[] Pcm,
    QuestionContextDto Question);

internal interface ISpeechBudget
{
    Task ExceededTask { get; }
    double ElapsedSeconds { get; }
    bool IsExceeded { get; }

    void StartSpeaking();
    void StopSpeaking();
}
