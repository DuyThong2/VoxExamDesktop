using VoxOralExam.Core.Models;

using VoxOralExam.DesktopApp.Services.DomainService;

namespace VoxOralExam.DesktopApp.Mocks;

/// <summary>
/// Dev-only <see cref="IExamApiService"/> backed by the in-memory <see cref="MockExamDataFactory"/>.
/// Selected when AppSettings.UseMockData is true so the app runs end-to-end before the real Java
/// exam endpoints exist.
/// </summary>
public class MockExamApiService : IExamApiService
{
    private readonly MockExamDataFactory _factory;

    public MockExamApiService(MockExamDataFactory factory)
    {
        _factory = factory;
    }

    public Task<IReadOnlyList<Exam>> GetAvailableExamsAsync(CancellationToken ct = default)
        => Task.FromResult(_factory.GetAvailableExams());

    public Task<ExamPaper> GetExamPaperAsync(string? examId, CancellationToken ct = default)
        => Task.FromResult(_factory.CreateMockPaperForExam(examId));

    public Task UpdateSessionStatusAsync(Guid sessionId, string status, CancellationToken ct = default)
        => Task.CompletedTask;
}

