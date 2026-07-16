using System.ComponentModel;
using VoxOralExam.DesktopApp.State;
using VoxOralExam.DesktopApp.ViewModels;

namespace VoxOralExam.DesktopApp.Services.EntryFlow;

/// <summary>
/// Single source of truth for "which entry stage are we in" and the owner of every transition
/// (see docs/wpf-redesign-plan.md Â§A). Replaces the old scattered navigation where each screen was
/// its own Window and moved with Show()/Close(). The shell (ShellWindow) binds a ContentControl to
/// <see cref="CurrentViewModel"/>; app-level DataTemplates map each stage view model to its View, so
/// changing stage swaps content inside one window rather than opening a new one.
///
/// The entry stages (Login -> ExamList -> OtpEntry -> SystemCheck -> DevicePreflight) live inside the
/// shell. Reaching the actual exam is a hand-off: the last stage calls <see cref="RequestStartExam"/>
/// and App opens the exam surface.
/// TODO(Â§A): eventually fold InExam + Submitted into the shell too, so the whole exam runs in one
/// lockdown-controlled window instead of a separate ExamWindow.
/// </summary>
public interface IExamEntryNavigator : INotifyPropertyChanged
{
    ExamEntryStage CurrentStage { get; }

    /// <summary>The view model for <see cref="CurrentStage"/>, bound by the shell's ContentControl.</summary>
    BaseViewModel? CurrentViewModel { get; }

    bool CanGoBack { get; }

    /// <summary>
    /// Raised when the entry flow is complete (device pre-flight passed) and the exam surface should
    /// launch. App subscribes and opens the exam window.
    /// </summary>
    event EventHandler? ExamStartRequested;

    /// <summary>Push the current stage onto the back-stack and navigate to <paramref name="stage"/>.</summary>
    void GoTo(ExamEntryStage stage);

    /// <summary>Navigate to <paramref name="stage"/> and clear the back-stack (e.g. entering Login).</summary>
    void Reset(ExamEntryStage stage);

    /// <summary>Return to the previous stage if there is one.</summary>
    void Back();

    /// <summary>Signal that the entry flow is done and the exam should start.</summary>
    void RequestStartExam();
}

