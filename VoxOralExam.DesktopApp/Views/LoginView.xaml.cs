using System.Windows;
using System.Windows.Controls;
using VoxOralExam.DesktopApp.ViewModels;

namespace VoxOralExam.DesktopApp.Views;

/// <summary>
/// Interaction logic for LoginView.xaml. Hosted inside ShellWindow via a DataTemplate; its DataContext
/// (LoginViewModel) is supplied by the navigator, so the constructor is parameterless. PasswordBox
/// can't be data-bound, so its value is seeded on load and pushed back on change.
/// </summary>
public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        Loaded += LoginView_Loaded;
    }

    private void LoginView_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm)
        {
            PasswordBox.Password = vm.Password;
        }
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
}
