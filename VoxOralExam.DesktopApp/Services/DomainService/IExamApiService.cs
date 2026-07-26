using VoxOralExam.Core.Models;

namespace VoxOralExam.DesktopApp.Services.DomainService;

/// <summary>
/// Source of exam-list and exam-paper data. Introduced so the UI depends on an abstraction instead
/// of MockExamDataFactory directly (see docs/wpf-redesign-plan.md Â§D). The concrete implementation
/// is chosen at startup by AppSettings.UseMockData: MockExamApiService for dev, ExamApiService for
/// the real Java backend.
/// </summary>
public interface IExamApiService
{
    Task<IReadOnlyList<Exam>> GetAvailableExamsAsync(CancellationToken ct = default);

    Task<ExamPaper> GetExamPaperAsync(string? examId, CancellationToken ct = default);

    Task UpdateSessionStatusAsync(Guid sessionId, string status, CancellationToken ct = default);

    Task UpdateRemainingTimeAsync(Guid sessionId, int remainingSeconds, CancellationToken ct = default);
}

