using VoxOralExam.Core.Context;

namespace VoxOralExam.Core.Interfaces;

public interface IExamSessionBootstrapService
{
    /// <summary>
    /// Nhận vé vào thi và nạp đề. KHÔNG xin stream token -- xem
    /// <see cref="IssueStreamAccessAsync"/>.
    /// </summary>
    Task EnterWithTicketAsync(ExamEntryTicket ticket, CancellationToken ct = default);

    /// <summary>
    /// Xin stream token cho phiên thi, chốt loại stream nếu kỳ thi cho học viên tự chọn.
    ///
    /// <para>Tách khỏi <see cref="EnterWithTicketAsync"/> vì server CHỐT loại stream ngay ở lần
    /// phát token đầu tiên và từ chối mọi loại khác sau đó. Gọi nó lúc nhận vé -- tức trước khi
    /// màn kiểm tra thiết bị kịp hiện ra -- đồng nghĩa lựa chọn đã bị quyết hộ trước khi học viên
    /// được hỏi, nên kỳ thi cấu hình "cho học viên tự chọn" chạy y hệt "bắt buộc cả hai".</para>
    ///
    /// <param name="preferredStreamType">
    /// CAMERA / SCREEN / CAMERA_AND_SCREEN, hoặc null để theo đúng cấu hình kỳ thi.
    /// </param>
    /// </summary>
    Task IssueStreamAccessAsync(string? preferredStreamType, CancellationToken ct = default);
}



