using System.Windows;
using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Infra.Interop;

/// <summary>
/// Ghi nhận mỗi lần cửa sổ thi mất focus và kéo nó trở lại. Song sinh với
/// <see cref="WindowCloseGuard"/>: cái kia chặn ĐÓNG, cái này chặn RỜI ĐI.
///
/// Cố ý PHÁT HIỆN chứ không NGĂN CHẶN. Chặn Alt+Tab cần low-level keyboard hook, mà hook đó
/// vừa hay bị antivirus gắn cờ (app thi cài trên máy lạ, bị Defender chặn là hỏng cả buổi),
/// vừa không chặn nổi Ctrl+Alt+Del hay Win+L -- hai phím Windows bảo vệ ở tầng nhân. Quan
/// trọng hơn: người muốn gian lận có màn hình thứ hai, điện thoại, máy khác -- không ai cần
/// Alt+Tab. Nên giá trị thật nằm ở BẰNG CHỨNG có mốc thời gian, không nằm ở việc khoá cứng.
///
/// Tự lấy lại focus vẫn giữ, nhưng với mục đích khác: kéo học sinh bấm nhầm quay lại bài,
/// không phải để "nhốt" người cố tình thoát.
/// </summary>
public sealed class WindowFocusGuard : IDisposable
{
    /// <summary>
    /// Bỏ qua các lần mất focus ngắn hơn mức này kể từ lần trước. Chuyển cửa sổ có thể sinh
    /// vài cặp Deactivated/Activated liên tiếp (hộp thoại hệ thống, tooltip, chuyển màn hình),
    /// không gộp lại thì một thao tác duy nhất đẻ ra một chuỗi cảnh báo và giám thị sẽ học
    /// cách phớt lờ chúng.
    /// </summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(3);

    private readonly Window _window;
    private DateTimeOffset _lastReportedAt = DateTimeOffset.MinValue;
    private bool _isLocked = true;
    private bool _disposed;

    public WindowFocusGuard(Window window)
    {
        _window = window;
        _window.Deactivated += OnDeactivated;
    }

    /// <summary>Bám theo ExamViewModel.IsExamLocked, y như WindowCloseGuard.</summary>
    public bool IsLocked
    {
        get => _isLocked;
        set => _isLocked = value;
    }

    /// <summary>
    /// Bắn mỗi lần ghi nhận một lần rời khỏi màn hình thi, kèm thời điểm UTC. Người đăng ký lo
    /// việc gửi lên server -- lớp này cố tình không biết gì về HTTP để còn dùng lại được và để
    /// test không cần dựng mạng.
    /// </summary>
    public event EventHandler<DateTimeOffset>? FocusLost;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.Deactivated -= OnDeactivated;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_isLocked)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastReportedAt < MinimumInterval)
        {
            return;
        }

        _lastReportedAt = now;

        // Ghi log TRƯỚC khi lấy lại focus: Activate() có thể ném nếu cửa sổ đang đóng dở, và
        // bằng chứng mới là thứ không được phép mất.
        LocalFileLogger.Info("exam_flow", "focus_lost", new { at = now });

        try
        {
            FocusLost?.Invoke(this, now);
        }
        catch (Exception ex)
        {
            // Người đăng ký gửi mạng, mạng thì hỏng được. Không để việc báo cáo làm gãy bài thi.
            LocalFileLogger.Error("exam_flow", "focus_lost_report_failed", ex);
        }

        // Xếp hàng qua Dispatcher chứ không gọi thẳng: đang ở giữa quá trình Windows chuyển
        // focus, Activate() ngay lúc này thường bị hệ điều hành bỏ qua (quy tắc chống cướp
        // foreground), và nếu ăn thì lại sinh tiếp một cặp Deactivated/Activated nữa.
        _ = _window.Dispatcher.BeginInvoke(new Action(RestoreFocus));
    }

    private void RestoreFocus()
    {
        try
        {
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Maximized;
            }

            _window.Activate();
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "focus_restore_failed", ex);
        }
    }
}
