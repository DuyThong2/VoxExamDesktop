using System.Windows;
using System.Windows.Controls;
using VoxOralExam.DesktopApp.ViewModels;

namespace VoxOralExam.DesktopApp.Views;

/// <summary>
/// Interaction logic for DevicePreflightView.xaml. Hosted inside ShellWindow via a DataTemplate; its
/// DataContext (DevicePreflightViewModel) is supplied by the navigator, so the constructor is parameterless.
/// </summary>
public partial class DevicePreflightView : UserControl
{
    public DevicePreflightView()
    {
        InitializeComponent();
        Unloaded += DevicePreflightView_Unloaded;
    }

    private void DevicePreflightView_Unloaded(object sender, RoutedEventArgs e)
    {
        // Leaving the screen (navigated back, or entering the exam) -- release camera/mic test devices.
        if (DataContext is DevicePreflightViewModel vm)
        {
            vm.CleanupDeviceTests();
        }
    }
}
