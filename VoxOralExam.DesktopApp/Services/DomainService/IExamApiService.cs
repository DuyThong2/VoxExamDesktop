using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Dtos;
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
    /// Phiên này có bị giám thị buộc kết thúc chưa. Trả <c>null</c> khi không hỏi được (mạng lỗi,
    /// server lỗi) -- người gọi PHẢI coi null là "không biết" và thi tiếp, tuyệt đối không được
    /// dừng bài. Dừng bài vì một cú nghẽn mạng còn tệ hơn lỗi đang vá.
    ///
    /// <para>Không dùng được mốc thời gian còn lại để suy ra: buộc kết thúc đặt phiên sang
    /// INTERRUPTED, mà trạng thái đó vẫn nằm trong RESUMABLE nên endpoint checkpoint vẫn nhận
    /// bình thường, không hề báo lỗi.</para>
    /// </summary>
    Task<ExamSessionGuard?> GetSessionGuardAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Báo cáo chi phí AI phát sinh ngay trên máy học viên (vd TTS qua LocalAvatarSpeaker) --
    /// đường REST song song với Kafka topic ai-usage-recorded bên BE. Best-effort: caller chịu
    /// trách nhiệm nuốt lỗi, không được để việc báo cáo làm hỏng luồng thi thật.
    /// </summary>
    Task ReportAiUsageAsync(Guid sessionId, ReportAiUsageRequestDto request, CancellationToken ct = default);
}

