using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using VoxOralExam.DesktopApp.ViewModels;

namespace VoxOralExam.DesktopApp.Views;

public partial class ExamWindow : Window
{
    private bool _isDragging;
    private bool _isClosingCleanly;
    private Point _dragStartPoint;

    public ExamWindow(ExamViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += ExamWindow_Loaded;
        Closing += ExamWindow_Closing;
    }

    private async void ExamWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ExamViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }

    private async void ExamWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosingCleanly)
        {
            return;
        }

        if (DataContext is ExamViewModel vm)
        {
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
