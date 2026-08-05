using VoxOralExam.Core.Models.Dtos;

namespace VoxOralExam.Core.Interfaces;

public interface IExamFlowService
{
    event Action<ExamQuestionPrompt>? OnQuestionPresented;
    event Action<string>? OnTranscriptAppended;
    event Action<string>? OnStatusChanged;
    event Action? OnSessionReady;
    event Action<bool>? OnExamEnded;
    event Action<bool>? OnStudentSpeakingChanged;
    event Action<bool>? OnAvatarSpeakingChanged;
    event Action<TimeSpan, TimeSpan>? OnQuestionSpeakingTimeChanged;

    /// <summary>
    /// True while the attempt is saving the student's final answer and closing the session out,
    /// false once that has finished. Drives the "saving, do not shut down" overlay.
    /// </summary>
    event Action<bool>? OnFinalSaveStateChanged;

    bool IsMicMuted { get; }

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    Task SubmitNowAsync();
    void SetMicMuted(bool muted);
}
