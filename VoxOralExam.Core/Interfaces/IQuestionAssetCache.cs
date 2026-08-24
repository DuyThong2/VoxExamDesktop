using VoxOralExam.Core.Models;

namespace VoxOralExam.Core.Interfaces;

/// <summary>
/// Tải sẵn tài nguyên câu hỏi xuống đĩa TRƯỚC khi vào thi, rồi cho phát từ tệp cục bộ.
///
/// <para>Trước đây ảnh/audio/video được tải đúng lúc tới câu hỏi, ngay giữa bài. Hỏng mạng lúc đó
/// là hỏng âm thầm: học sinh nhận một câu hỏi về bức ảnh không hiện ra, và đồng hồ thi vẫn trừ
/// trong lúc chờ tải. Kéo toàn bộ việc tải lên trước, vào đúng lúc học sinh đang kiểm tra
/// camera/mic, thì sự cố lộ ra khi còn ở phòng chờ -- lúc còn gọi được giám thị.</para>
///
/// <para>Đệm ra ĐĨA chứ không phải RAM: audio/video không nạp vừa bộ nhớ như ảnh. Khoá theo hash
/// của URL nên sống qua cả lần khởi động lại ứng dụng, và lần vào lại sau khi bị ngắt sẽ trúng
/// đệm thay vì tải lại từ đầu -- đúng vào lúc mạng đang tệ nhất.</para>
/// </summary>
public interface IQuestionAssetCache
{
    /// <summary>
    /// Tải mọi tài nguyên có URL về đĩa. Gọi lại với cùng bộ tài nguyên là gần như tức thì vì
    /// tệp đã nằm sẵn trong đệm.
    /// </summary>
    /// <param name="onProgress">
    /// Báo tiến độ (đã xong / tổng) để màn kiểm tra thiết bị hiển thị. Gọi trên thread nền.
    /// </param>
    /// <returns>Danh sách URL tải KHÔNG thành công, rỗng nghĩa là đủ cả.</returns>
    Task<IReadOnlyList<string>> PrefetchAsync(
        IEnumerable<QuestionAsset> assets,
        Action<int, int>? onProgress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Đường dẫn tệp cục bộ của một URL, hoặc <c>null</c> nếu chưa có trong đệm.
    /// </summary>
    string? TryGetLocalPath(string url);

    /// <summary>
    /// Xoá toàn bộ tệp đã đệm.
    ///
    /// <para>CHỈ gọi khi bài thi thật sự kết thúc (đã nộp), TUYỆT ĐỐI không gọi lúc cửa sổ thi
    /// đóng: cửa sổ đóng bất thường chính là tình huống bị ngắt giữa chừng, mà xoá đệm ở đó thì
    /// lần vào lại phải tải lại từ đầu -- mất tác dụng đúng lúc cần nhất.</para>
    /// </summary>
    void Clear();
}
