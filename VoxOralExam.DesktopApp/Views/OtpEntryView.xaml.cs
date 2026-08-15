using System.Windows;
using System.Windows.Controls;

namespace VoxOralExam.DesktopApp.Views;

/// <summary>
/// Interaction logic for OtpEntryView.xaml. Hosted inside ShellWindow via a DataTemplate; its
/// DataContext (OtpEntryViewModel) is supplied by the navigator, so the constructor is parameterless.
/// </summary>
public partial class OtpEntryView : UserControl
{
    public OtpEntryView()
    {
        InitializeComponent();
        Loaded += OtpEntryView_Loaded;
    }

    private void OtpEntryView_Loaded(object sender, RoutedEventArgs e)
    {
        OtpBox.Focus();
    }
}
