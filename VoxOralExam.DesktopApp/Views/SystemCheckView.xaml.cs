using System.Windows.Controls;

namespace VoxOralExam.DesktopApp.Views;

/// <summary>
/// Interaction logic for SystemCheckView.xaml. Hosted inside ShellWindow via a DataTemplate; its
/// DataContext (SystemCheckViewModel) is supplied by the navigator, so the constructor is parameterless.
/// </summary>
public partial class SystemCheckView : UserControl
{
    public SystemCheckView()
    {
        InitializeComponent();
    }
}
