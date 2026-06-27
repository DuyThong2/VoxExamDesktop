using System.Windows;

using VoxOralExam.DesktopApp.ViewModels;

namespace VoxOralExam.DesktopApp;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
