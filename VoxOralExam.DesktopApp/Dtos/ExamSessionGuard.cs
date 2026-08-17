namespace VoxOralExam.DesktopApp.Dtos;

/// <summary>
/// Ảnh chụp trạng thái kiểm soát của một phiên thi, dùng cho vòng hỏi định kỳ của
/// <c>ExamViewModel</c> để biết giám thị đã buộc kết thúc hay chưa.
/// </summary>
/// <param name="Status">Trạng thái phiên (IN_PROGRESS, INTERRUPTED, SUBMITTED...).</param>
/// <param name="Flagged">Phiên có bị đánh dấu nghi vấn không.</param>
/// <param name="CandidateBlocked">
/// Thí sinh đã bị chặn chưa. <b>Đây là trường duy nhất được phép dùng để dừng bài.</b>
///
/// <para>Đừng dùng <paramref name="Status"/>: mất mạng giữa chừng cũng cho ra INTERRUPTED y hệt
/// lúc bị buộc kết thúc, nên bắt theo trạng thái là dừng bài oan mỗi lần rớt mạng. Còn
/// <c>blockedAt</c> chỉ do đúng hành vi cấm đặt, và gỡ cấm thì xoá đi.</para>
///
/// <para>Cũng đừng dùng <paramref name="Flagged"/>: đánh dấu nghi vấn là việc ghi chú để xem lại
/// sau, không phải lệnh dừng — giám thị vẫn gắn cờ cho bài đang thi bình thường.</para>
/// </param>
public sealed record ExamSessionGuard(
    string? Status,
    bool Flagged,
    bool CandidateBlocked);
