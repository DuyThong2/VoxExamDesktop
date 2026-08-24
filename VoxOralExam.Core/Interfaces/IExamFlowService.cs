using VoxOralExam.Core.Models.Dtos;

namespace VoxOralExam.Core.Interfaces;

public interface IExamFlowService
{
    event Action<ExamQuestionPrompt>? OnQuestionPresented;
    event Action<string>? OnTranscriptAppended;
    event Action<string>? OnStatusChanged;
    event Action? OnSessionReady;
    event Action<bool>? OnExamEnded;
    event Action<bool>? OnStudentSpeakingChanged;
    event Action<bool>? OnAvatarSpeakingChanged;

    /// <summary>
    /// Lời avatar vừa bắt đầu đọc, để màn thi hiện nguyên văn câu AI đang nói bên cạnh đề bài gốc.
    ///
    /// <para>Khác <see cref="OnTranscriptAppended"/>: cái đó chỉ có follow-up và chỉ để ghi log.
    /// Sự kiện này bám frame <c>speak</c> nên bắt được MỌI lời avatar nói, kể cả lời dẫn section
    /// và thông báo chuẩn bị.</para>
    /// </summary>
    event Action<string>? OnAvatarUtteranceChanged;

    event Action<TimeSpan, TimeSpan>? OnQuestionSpeakingTimeChanged;

    /// <summary>
    /// True while the attempt is saving the student's final answer and closing the session out,
    /// false once that has finished. Drives the "saving, do not shut down" overlay.
    /// </summary>
    event Action<bool>? OnFinalSaveStateChanged;

    bool IsMicMuted { get; }

    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    Task SubmitNowAsync();
    void SetMicMuted(bool muted);

    /// <summary>
    /// Dừng bài vì giám thị đã buộc kết thúc, khi biết được qua đường HỎI SERVER chứ không phải
    /// qua tin <c>force_end</c> của WebSocket.
    ///
    /// <para>Cần có vì đường WebSocket không đáng tin: lệnh cấm đi Kafka rồi mới tới pod Python
    /// giữ kết nối, mà consumer group giao partition cho MỘT pod trong khi WebSocket của thí sinh
    /// nằm ở pod nào thì không biết trước. Đo được 2026-08-17: hệ có 2 pod, lệnh cấm rơi vào pod
    /// không giữ kết nối, bị ghi log "no local realtime connection" rồi bỏ qua — bài thi chạy
    /// tiếp như không có gì.</para>
    ///
    /// <para>Chạy đúng nhánh mà tin <c>force_end</c> vẫn chạy, nên không có đường xử lý thứ hai
    /// để mà lệch nhau.</para>
    /// </summary>
    void ForceEndFromServer(string reason);

    /// <summary>
    /// Thí sinh rời khỏi cửa sổ thi. Best-effort: nuốt lỗi, không được làm gián đoạn bài thi.
    /// </summary>
    Task ReportFocusLostAsync(DateTimeOffset capturedAt);

    /// <summary>
    /// Camera ngừng gửi khung hình quá ngưỡng cảnh báo. <paramref name="capturedAt"/> là mốc khung
    /// hình cuối cùng, không phải lúc phát hiện. Best-effort như trên.
    /// </summary>
    Task ReportCameraSignalLostAsync(DateTimeOffset capturedAt, bool neverDelivered);

    /// <summary>
    /// Khung hình trở lại sau một lần mất ĐÃ được cảnh báo, đóng lại khoảng trống trong sổ bằng
    /// chứng. Không gọi cho những lần gián đoạn ngắn chưa từng sinh cảnh báo.
    /// </summary>
    Task ReportCameraSignalRestoredAsync(DateTimeOffset capturedAt, TimeSpan outage);

    /// <summary>
    /// Tài nguyên audio/video của câu hỏi không phát được, kể cả sau một lần thử lại. Thí sinh sẽ
    /// bị hỏi về đoạn ghi âm họ chưa từng nghe, nên phải để lại dấu vết ở phía server cho người
    /// chấm truy được -- log máy trạm thì không ai đọc.
    ///
    /// <para>Cố ý KHÔNG đi vào kênh cảnh báo giám thị: kênh đó dành cho hành vi của thí sinh, và
    /// trộn lỗi kỹ thuật vào là cách nhanh nhất để người ta ngừng tin những cảnh báo thật.</para>
    ///
    /// <para>Best-effort như các Report* khác: nuốt lỗi, không được làm gián đoạn bài thi.</para>
    /// </summary>
    Task ReportAssetPlaybackFailedAsync(string reason, int questionNumber);
}
