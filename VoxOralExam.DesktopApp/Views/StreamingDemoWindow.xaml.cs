using System.Windows;
using VoxOralExam.DesktopApp.ViewModels;

namespace VoxOralExam.DesktopApp.Views;

public partial class StreamingDemoWindow : Window
{
    private bool _isClosingCleanly;

    public StreamingDemoWindow(StreamingDemoViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += StreamingDemoWindow_Closing;
    }

    private async void StreamingDemoWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosingCleanly || DataContext is not StreamingDemoViewModel vm)
        {
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
            Closing -= StreamingDemoWindow_Closing;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
