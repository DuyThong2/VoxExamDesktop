using VoxOralExam.DesktopApp.Dtos;

namespace VoxOralExam.DesktopApp.Services.ExamFlow.Turn;

internal enum SpeechCaptureEndReason
{
    Completed,
    InitialSilenceTimeout,
    OverallTimeout,
    SpeechBudgetExceeded,
    SpeechWindowClosed
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
