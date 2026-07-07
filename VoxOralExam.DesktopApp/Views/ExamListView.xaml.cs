using System.Windows.Controls;

namespace VoxOralExam.DesktopApp.Views;

/// <summary>
/// Interaction logic for ExamListView.xaml. Hosted inside ShellWindow via a DataTemplate; its
/// DataContext (MainViewModel) is supplied by the navigator, so the constructor is parameterless.
/// Replaces the former standalone MainWindow.
/// </summary>
public partial class ExamListView : UserControl
{
    public ExamListView()
    {
        InitializeComponent();
    }
}
