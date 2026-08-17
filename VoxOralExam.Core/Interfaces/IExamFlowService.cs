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
}
