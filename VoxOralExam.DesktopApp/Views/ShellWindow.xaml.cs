using System.Windows;
using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Views;

/// <summary>
/// The single host window for the entry flow. Its DataContext is the navigator, so its ContentControl
/// tracks <see cref="IExamEntryNavigator.CurrentViewModel"/> and swaps stage views in place.
/// </summary>
public partial class ShellWindow : Window
{
    public ShellWindow(IExamEntryNavigator navigator)
    {
        InitializeComponent();
        DataContext = navigator;
    }
}
