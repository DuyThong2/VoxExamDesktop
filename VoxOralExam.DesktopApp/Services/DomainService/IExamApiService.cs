using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Dtos.Requests;

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

    /// <summary>
    /// Báo cáo chi phí AI phát sinh ngay trên máy học viên (vd TTS qua LocalAvatarSpeaker) --
    /// đường REST song song với Kafka topic ai-usage-recorded bên BE. Best-effort: caller chịu
    /// trách nhiệm nuốt lỗi, không được để việc báo cáo làm hỏng luồng thi thật.
    /// </summary>
    Task ReportAiUsageAsync(Guid sessionId, ReportAiUsageRequestDto request, CancellationToken ct = default);
}

