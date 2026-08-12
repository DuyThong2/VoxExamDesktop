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
        viewModel.PropertyChanged += ExamViewModel_PropertyChanged;

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
        }

        _closeGuard.Dispose();
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
        mediaElement.Play();
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
