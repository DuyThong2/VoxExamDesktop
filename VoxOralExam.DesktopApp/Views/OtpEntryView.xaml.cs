using System.Windows;
using System.Windows.Controls;
using VoxOralExam.DesktopApp.ViewModels;

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
        Unloaded += OtpEntryView_Unloaded;
    }

    private void OtpEntryView_Loaded(object sender, RoutedEventArgs e)
    {
        OtpBox.Focus();
    }

    private void OtpEntryView_Unloaded(object sender, RoutedEventArgs e)
    {
        // Leaving the screen (verified or navigated back) -- stop the rotation countdown timer.
        if (DataContext is OtpEntryViewModel vm)
        {
            vm.Cleanup();
        }
    }
}
