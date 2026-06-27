using VoxOralExam.Core.Dtos;

namespace VoxOralExam.Core.Interfaces;

public interface IExamFlowService
{
    event Action<ExamQuestionPrompt>? OnQuestionPresented;
    event Action<string>? OnTranscriptAppended;
    event Action<string>? OnStatusChanged;
    event Action? OnExamCompleted;
    event Action<bool>? OnStudentSpeakingChanged;

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
}
