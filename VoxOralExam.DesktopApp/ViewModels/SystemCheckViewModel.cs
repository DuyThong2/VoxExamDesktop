using System.Windows.Input;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.ViewModels;

/// <summary>
/// Stage: SystemCheck (background-app / virtual-device / VM scan). Placeholder for slice 1-2.
/// </summary>
public class SystemCheckViewModel : BaseViewModel
{
    private readonly IExamEntryNavigator _navigator;

    public SystemCheckViewModel(IExamEntryNavigator navigator)
    {
        _navigator = navigator;
        ContinueCommand = new RelayCommand(Continue);
        BackCommand = new RelayCommand(() => _navigator.Back());
    }

    public ICommand ContinueCommand { get; }
    public ICommand BackCommand { get; }

    private void Continue()
    {
        // TODO(§B - lockdown, detection half first): run ISystemDetector.ScanAsync(blocklist) and show
        //   the result here:
        //   - detect remote-control (AnyDesk/TeamViewer), screen recorders (OBS), VIRTUAL CAMERA and
        //     virtual audio (highest priority -- they defeat camera proctoring), VM markers, monitor count.
        //   - blocklist comes FROM THE SERVER (entry ticket), never hard-coded.
        //   - resolve the EnforcementTier via IEnforcerProbe (Service -> Helper -> DetectOnly), report
        //     tier + scan to the server, and block continuing if below this exam's minEnforcementTier.
        //   Detection is identical on every machine; only enforcement is tiered.
        //   Multi-monitor policy also lives here (typically require exactly one screen).
        _navigator.GoTo(ExamEntryStage.DevicePreflight);
    }
}
