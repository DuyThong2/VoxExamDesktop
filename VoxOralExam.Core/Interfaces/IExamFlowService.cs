using VoxOralExam.Core.Models.Dtos;

namespace VoxOralExam.Core.Interfaces;

public interface IExamFlowService
{
    event Action<ExamQuestionPrompt>? OnQuestionPresented;
    event Action<string>? OnTranscriptAppended;
    event Action<string>? OnStatusChanged;
    event Action? OnExamCompleted;
    event Action<bool>? OnStudentSpeakingChanged;
    event Action<bool>? OnAvatarSpeakingChanged;

    bool IsMicMuted { get; }

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    void SetMicMuted(bool muted);
}

