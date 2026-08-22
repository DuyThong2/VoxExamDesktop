using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using VoxOralExam.DesktopApp.State;
using VoxOralExam.DesktopApp.ViewModels;

using VoxOralExam.DesktopApp.Services.EntryFlow;

namespace VoxOralExam.DesktopApp.Services.EntryFlow.Impl;

/// <summary>
/// Default <see cref="IExamEntryNavigator"/>. Maps each entry stage to its view model type and
/// resolves a fresh instance from DI on every visit (so a stage like DevicePreflight re-runs its
/// checks each time it is entered). Registered as a singleton; view models depend on the navigator,
/// the navigator depends only on <see cref="IServiceProvider"/>, so there is no construction cycle.
/// </summary>
public sealed class ExamEntryNavigator : IExamEntryNavigator
{
    private readonly IServiceProvider _services;
    private readonly Stack<ExamEntryStage> _history = new();

    // InExam / Submitted / Error are intentionally absent: InExam is a hand-off to ExamWindow via
    // ExamStartRequested, not a content-swap stage yet. TODO(§A): add them here once the exam surface
    // is folded into the shell.
    private static readonly Dictionary<ExamEntryStage, Type> StageViewModels = new()
    {
        [ExamEntryStage.Login] = typeof(LoginViewModel),
        [ExamEntryStage.ExamList] = typeof(MainViewModel),
        [ExamEntryStage.OtpEntry] = typeof(OtpEntryViewModel),
        [ExamEntryStage.DevicePreflight] = typeof(DevicePreflightViewModel),
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ExamStartRequested;

    public ExamEntryNavigator(IServiceProvider services)
    {
        _services = services;
    }

    public ExamEntryStage CurrentStage { get; private set; }

    public BaseViewModel? CurrentViewModel { get; private set; }

    public bool CanGoBack => _history.Count > 0;

    public void GoTo(ExamEntryStage stage)
    {
        // TODO(§A): make transitions conditional on the entry ticket's deliveryMode -- a take-home
        // exam (no live proctor) must skip OtpEntry and the live-monitor assumptions entirely, while a
        // proctored lab exam walks the full chain. For now every exam walks Login -> ... -> InExam.
        if (CurrentViewModel is not null)
        {
            _history.Push(CurrentStage);
        }

        SetCurrent(stage);
    }

    public void Reset(ExamEntryStage stage)
    {
        _history.Clear();
        SetCurrent(stage);
    }

    public void Back()
    {
        if (!CanGoBack)
        {
            return;
        }

        SetCurrent(_history.Pop());
    }

    public void RequestStartExam()
    {
        LocalFileLogger.Info("navigator", "exam_start_requested");
        ExamStartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SetCurrent(ExamEntryStage stage)
    {
        if (!StageViewModels.TryGetValue(stage, out var viewModelType))
        {
            throw new InvalidOperationException(
                $"No view model is registered for entry stage '{stage}'. " +
                "Add it to ExamEntryNavigator.StageViewModels and register it in App.ConfigureServices.");
        }

        var viewModel = (BaseViewModel)_services.GetRequiredService(viewModelType);

        CurrentStage = stage;
        CurrentViewModel = viewModel;

        OnPropertyChanged(nameof(CurrentStage));
        OnPropertyChanged(nameof(CurrentViewModel));
        OnPropertyChanged(nameof(CanGoBack));

        LocalFileLogger.Info("navigator", "go_to", new { stage = stage.ToString() });
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

