using VoxOralExam.Core.Models.Dtos;

namespace VoxOralExam.Core.Interfaces;

public interface IExamFlowService
{
    event Action<ExamQuestionPrompt>? OnQuestionPresented;
    event Action<string>? OnTranscriptAppended;
    event Action<string>? OnStatusChanged;
    event Action<bool>? OnExamEnded;
    event Action<bool>? OnStudentSpeakingChanged;
    event Action<bool>? OnAvatarSpeakingChanged;
    event Action<TimeSpan, TimeSpan>? OnQuestionSpeakingTimeChanged;

    bool IsMicMuted { get; }

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    Task SubmitNowAsync();
    void SetMicMuted(bool muted);
}
