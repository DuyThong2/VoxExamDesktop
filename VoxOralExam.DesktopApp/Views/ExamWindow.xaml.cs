using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using VoxOralExam.DesktopApp.Infra.Interop;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.ViewModels;

namespace VoxOralExam.DesktopApp.Views;

public partial class ExamWindow : Window
{
    private readonly WindowCloseGuard _closeGuard;
    private readonly WindowFocusGuard _focusGuard;
    private bool _isDragging;
    private bool _isClosingCleanly;
    private Point _dragStartPoint;

    public ExamWindow(ExamViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Constructed before SourceInitialized so the X is already greyed the first time the window
        // paints. ExamViewModel.IsExamLocked is the single source of truth, and it starts locked.
        _closeGuard = new WindowCloseGuard(this) { IsLocked = viewModel.IsExamLocked };
        // Cùng nguồn sự thật IsExamLocked với _closeGuard: hết bài thì vừa mở được nút đóng,
        // vừa thôi coi việc chuyển cửa sổ là vi phạm.
        _focusGuard = new WindowFocusGuard(this) { IsLocked = viewModel.IsExamLocked };
        _focusGuard.FocusLost += (_, capturedAt) => viewModel.ReportFocusLost(capturedAt);
        viewModel.PropertyChanged += ExamViewModel_PropertyChanged;
        viewModel.MediaStopRequested += ExamViewModel_MediaStopRequested;
        viewModel.MediaRetryRequested += ExamViewModel_MediaRetryRequested;

        Loaded += ExamWindow_Loaded;
        Closing += ExamWindow_Closing;
        Closed += ExamWindow_Closed;
    }

    private void ExamViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null && e.PropertyName != nameof(ExamViewModel.IsExamLocked))
        {
            return;
        }

        // The system menu belongs to the thread that owns the HWND, and an unlock can be raised from
        // a background continuation, so marshal before touching user32.
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ApplyCloseGuardState));
            return;
        }

        ApplyCloseGuardState();
    }

    private void ApplyCloseGuardState()
    {
        if (DataContext is ExamViewModel vm)
        {
            _closeGuard.IsLocked = vm.IsExamLocked;
            _focusGuard.IsLocked = vm.IsExamLocked;
        }
    }

    private async void ExamWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ExamViewModel vm)
        {
            return;
        }

        try
        {
            await vm.InitializeAsync();
        }
        catch (Exception ex)
        {
            // The exam never started, so nothing will ever raise OnExamEnded -- without unlocking
            // here the student is left in a window whose close button does nothing at all.
            LocalFileLogger.Error("exam_window", "exam_initialize_failed", ex);
            vm.UnlockWindowForFailure($"Không thể bắt đầu bài thi: {ex.Message}");
            MessageBox.Show(
                this,
                "Không thể bắt đầu bài thi. Vui lòng đóng cửa sổ và liên hệ giám thị.",
                "Lỗi khởi động bài thi",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ExamWindow_Closed(object? sender, EventArgs e)
    {
        Closed -= ExamWindow_Closed;
        if (DataContext is ExamViewModel vm)
        {
            vm.PropertyChanged -= ExamViewModel_PropertyChanged;
            vm.MediaStopRequested -= ExamViewModel_MediaStopRequested;
            vm.MediaRetryRequested -= ExamViewModel_MediaRetryRequested;
        }

        _closeGuard.Dispose();
        _focusGuard.Dispose();
    }

    private async void ExamWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosingCleanly)
        {
            return;
        }

        if (DataContext is ExamViewModel vm)
        {
            // Every close path except the X/Alt+F4/system-menu one lands here: WindowCloseGuard
            // filters WM_SYSCOMMAND only, so a taskbar "Close window", an Alt+Tab preview close and
            // the app's own Window.Close() all arrive as Closing and are refused here instead. That
            // split is deliberate -- see WindowCloseGuard's summary; filtering WM_CLOSE natively
            // would also wedge Application.Shutdown() and block a Windows logoff.
            //
            // This returns before _isClosingCleanly is set on purpose: a blocked close is not a
            // close in progress, and latching that flag here would let the very next Close() --
            // including a second attempt -- straight through.
            if (vm.IsExamLocked)
            {
                e.Cancel = true;
                LocalFileLogger.Info("exam_window", "close_blocked_exam_in_progress");
                return;
            }

            e.Cancel = true;
            _isClosingCleanly = true;

            try
            {
                await vm.CleanupAsync();
            }
            finally
            {
                Closing -= ExamWindow_Closing;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
    }

    private void CameraPreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var border = (Border)sender;
        _isDragging = true;
        _dragStartPoint = e.GetPosition(RootGrid);
        border.CaptureMouse();
    }

    private void CameraPreview_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var border = (Border)sender;
        var currentPos = e.GetPosition(RootGrid);

        var deltaX = currentPos.X - _dragStartPoint.X;
        var deltaY = currentPos.Y - _dragStartPoint.Y;

        var margin = border.Margin;
        var newMargin = new Thickness(
            margin.Left + deltaX,
            margin.Top + deltaY,
            margin.Right - deltaX,
            margin.Bottom - deltaY);

        var maxX = RootGrid.ActualWidth - border.ActualWidth - 10;
        var maxY = RootGrid.ActualHeight - border.ActualHeight - 10;

        if (newMargin.Left >= -10 && newMargin.Left <= maxX)
        {
            border.Margin = new Thickness(
                Math.Max(-10, Math.Min(maxX, newMargin.Left)),
                Math.Max(-10, Math.Min(maxY, newMargin.Top)),
                0,
                0);
        }

        _dragStartPoint = currentPos;
    }

    private void CameraPreview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        ((Border)sender).ReleaseMouseCapture();
    }

    private void QuestionAssetMedia_TargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (sender is not MediaElement mediaElement)
        {
            return;
        }

        if (mediaElement.Source is null)
        {
            mediaElement.Stop();
            return;
        }

        mediaElement.Stop();
        mediaElement.Position = TimeSpan.Zero;

        // Vào lại giữa câu sau khi ĐÃ trả lời ít nhất một lượt thì chỉ hiện lại khung, KHÔNG phát.
        //
        // Nhánh đó (QuestionPresentationService.PresentResumeAsync) chỉ chạy khi câu hỏi đã có lượt
        // hoàn thành, mà lượt chỉ mở được sau khi media chạy hết -- tức thí sinh CHẮC CHẮN đã nghe.
        // Phát lại ở đây là cho nghe lần hai, phá luật "audio/video đúng một lần" và khai thác được
        // bằng cách cố tình để bị cấm. Nặng hơn: đường đó không bắn MediaPlaybackStateChanged nên
        // mic KHÔNG bị tắt trong lúc phát, mà mic không khử vọng -- tiếng loa đi thẳng vào transcript.
        //
        // Ngắt đúng lúc media đang phát dở thì KHÔNG rơi vào đây: chưa lượt nào xong nên luồng đi
        // nhánh PresentInitialAsync, phát lại từ đầu và chờ hết như bình thường.
        if (DataContext is ExamViewModel { AutoPlayAssetMedia: false })
        {
            return;
        }

        // AUDIO do QuestionAssetAudioPlayer phát, để ra đúng thiết bị học sinh đã chọn ở màn kiểm
        // tra thiết bị -- MediaElement không chọn được thiết bị. Gọi Play() ở đây nữa là nghe chồng
        // hai lần, mà bản của MediaElement lại đi ra thiết bị mặc định của Windows.
        if (DataContext is ExamViewModel { PlaysAssetAudioInternally: true })
        {
            return;
        }

        mediaElement.Play();
    }

    /// <summary>
    /// Lượt phát chạm trần an toàn mà media vẫn chạy. Không dừng ở đây thì nó kêu chồng lên tiếng
    /// AI đọc đề bài, đúng lúc mic sắp mở -- và mic thì không có khử vọng.
    /// </summary>
    private void ExamViewModel_MediaStopRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ExamViewModel_MediaStopRequested));
            return;
        }

        QuestionAssetMedia.Stop();
    }

    /// <summary>
    /// Thử phát lại sau một lần <c>MediaFailed</c>. Gọi <c>Close()</c> trước <c>Play()</c> để
    /// MediaElement thả bộ giải mã đang hỏng và mở lại nguồn từ đầu -- gọi thẳng <c>Play()</c> trên
    /// một element vừa lỗi thường lỗi lại ngay mà không hề chạm tới mạng.
    /// </summary>
    private void ExamViewModel_MediaRetryRequested()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ExamViewModel_MediaRetryRequested));
            return;
        }

        QuestionAssetMedia.Close();
        QuestionAssetMedia.Position = TimeSpan.Zero;
        QuestionAssetMedia.Play();
    }

    private void QuestionAssetMedia_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ExamViewModel vm)
        {
            vm.NotifyQuestionAssetMediaEnded();
        }
    }

    private void QuestionAssetMedia_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (DataContext is ExamViewModel vm)
        {
            vm.NotifyQuestionAssetMediaFailed(e.ErrorException?.Message);
        }
    }
}
