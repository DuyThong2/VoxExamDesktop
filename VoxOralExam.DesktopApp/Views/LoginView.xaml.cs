using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VoxOralExam.DesktopApp.ViewModels;

namespace VoxOralExam.DesktopApp.Views;

public partial class LoginView : Window
{
    public LoginView(LoginViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        PasswordBox.Password = viewModel.Password;
        Closing += LoginView_Closing;
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm)
        {
            vm.Password = ((PasswordBox)sender).Password;
        }
    }

    private void Field_GotFocus(object sender, RoutedEventArgs e)
    {
        // Placeholder handled via style trigger
    }

    private void LoginView_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is LoginViewModel vm)
        {
            vm.CleanupDeviceTests();
        }
    }
}
