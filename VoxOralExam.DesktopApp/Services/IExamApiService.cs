using VoxOralExam.DesktopApp.Models;

namespace VoxOralExam.DesktopApp.Services;

/// <summary>
/// Source of exam-list and exam-paper data. Introduced so the UI depends on an abstraction instead
/// of MockExamDataFactory directly (see docs/wpf-redesign-plan.md §D). The concrete implementation
/// is chosen at startup by AppSettings.UseMockData: MockExamApiService for dev, ExamApiService for
/// the real Java backend.
/// </summary>
public interface IExamApiService
{
    Task<IReadOnlyList<Exam>> GetAvailableExamsAsync(CancellationToken ct = default);

    Task<ExamPaper> GetExamPaperAsync(string? examId, CancellationToken ct = default);
}
