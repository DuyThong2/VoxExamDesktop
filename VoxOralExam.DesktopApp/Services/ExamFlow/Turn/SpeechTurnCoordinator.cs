using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Infra.Devices;

namespace VoxOralExam.DesktopApp.Services.ExamFlow.Turn;

internal sealed class SpeechTurnCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly RealtimeSessionClient _sessionClient;
    private readonly TurnAudioRecorder _recorder;
    private TaskCompletionSource<bool> _speechStarted = NewSignal();
    private TaskCompletionSource<bool> _speechEnded = NewSignal();
    private ISpeechBudget? _budget;
    private bool _speechWindowOpen;
    private bool _isSpeaking;
    private bool _disposed;

    public SpeechTurnCoordinator(
        RealtimeSessionClient sessionClient,
        TurnAudioRecorder recorder)
    {
        _sessionClient = sessionClient;
        _recorder = recorder;
        _sessionClient.OnVadSpeechStart += HandleSpeechStart;
        _sessionClient.OnVadSpeechEnd += HandleSpeechEnd;
    }

    public event Action<bool>? StudentSpeakingChanged;

    public bool IsSpeechWindowOpen
    {
        get
        {
            lock (_sync)
            {
                return _speechWindowOpen;
            }
        }
    }

    public Task SpeechStartedTask
    {
        get
        {
            lock (_sync)
            {
                return _speechStarted.Task;
            }
        }
    }

    public void OpenSpeechWindow(ISpeechBudget budget)
    {
        lock (_sync)
        {
            _budget = budget;
            _speechWindowOpen = true;
            _isSpeaking = false;
            _speechStarted = NewSignal();
            _speechEnded = NewSignal();
        }
    }

    public void CloseSpeechWindow()
    {
        ISpeechBudget? budget;
        lock (_sync)
        {
            _speechWindowOpen = false;
            _isSpeaking = false;
            budget = _budget;
            _budget = null;
            _speechStarted.TrySetResult(false);
            _speechEnded.TrySetResult(false);
        }
        budget?.StopSpeaking();
        StudentSpeakingChanged?.Invoke(false);
    }

    public async Task<CapturedTurn> CaptureAsync(
        int turnOrder,
        TimeSpan initialTimeout,
        TimeSpan overallTimeout,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
    {
        ISpeechBudget? budget;
        Task speechStartedTask;
        lock (_sync)
        {
            budget = _budget;
            speechStartedTask = _speechStarted.Task;
        }

        if (!IsSpeechWindowOpen || budget is null)
        {
            return CompleteCapture(
                turnOrder,
                0,
                SpeechCaptureEndReason.SpeechWindowClosed);
        }

        if (budget.IsExceeded)
        {
            CloseSpeechWindow();
            return CompleteCapture(
                turnOrder,
                budget.ElapsedSeconds,
                SpeechCaptureEndReason.SpeechBudgetExceeded);
        }

        var elapsedAtStart = budget.ElapsedSeconds;
        var spoke = _recorder.IsTurnActive
            || await WaitForSignalAsync(speechStartedTask, initialTimeout, cancellationToken);
        if (!spoke)
        {
            CloseSpeechWindow();
            return CompleteCapture(
                turnOrder,
                budget.ElapsedSeconds - elapsedAtStart,
                SpeechCaptureEndReason.InitialSilenceTimeout);
        }

        var endReason = await WaitForSpeechEndWithGraceAsync(
            budget,
            overallTimeout,
            gracePeriod,
            cancellationToken);
        CloseSpeechWindow();
        return CompleteCapture(
            turnOrder,
            budget.ElapsedSeconds - elapsedAtStart,
            endReason);
    }

    private CapturedTurn CompleteCapture(
        int turnOrder,
        double durationSeconds,
        SpeechCaptureEndReason reason)
    {
        var pcm = _recorder.GetTurnBufferAndReset();
        return new CapturedTurn(
            turnOrder,
            pcm,
            Math.Round(Math.Max(0, durationSeconds), 2),
            reason);
    }

    private async Task<SpeechCaptureEndReason> WaitForSpeechEndWithGraceAsync(
        ISpeechBudget budget,
        TimeSpan overallTimeout,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + overallTimeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return SpeechCaptureEndReason.OverallTimeout;
            }

            Task speechEndedTask;
            lock (_sync)
            {
                speechEndedTask = _speechEnded.Task;
            }

            var completed = await Task.WhenAny(
                speechEndedTask,
                budget.ExceededTask,
                Task.Delay(remaining, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();

            if (completed == budget.ExceededTask || budget.IsExceeded)
            {
                return SpeechCaptureEndReason.SpeechBudgetExceeded;
            }
            if (completed != speechEndedTask)
            {
                return SpeechCaptureEndReason.OverallTimeout;
            }

            remaining = deadline - DateTime.UtcNow;
            var grace = remaining < gracePeriod ? remaining : gracePeriod;
            if (grace <= TimeSpan.Zero)
            {
                return SpeechCaptureEndReason.Completed;
            }

            Task resumedTask;
            lock (_sync)
            {
                resumedTask = _isSpeaking
                    ? Task.CompletedTask
                    : _speechStarted.Task;
            }
            var resumed = await WaitForSignalAsync(
                resumedTask,
                grace,
                cancellationToken);
            if (!resumed)
            {
                return SpeechCaptureEndReason.Completed;
            }
        }
    }

    private static async Task<bool> WaitForSignalAsync(
        Task signal,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var delay = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(signal, delay);
        cancellationToken.ThrowIfCancellationRequested();
        return completed == signal;
    }

    private void HandleSpeechStart()
    {
        ISpeechBudget? budget;
        lock (_sync)
        {
            if (!_speechWindowOpen)
            {
                return;
            }

            _isSpeaking = true;
            _speechEnded = NewSignal();
            budget = _budget;
            _speechStarted.TrySetResult(true);
        }

        if (!_recorder.IsTurnActive)
        {
            _recorder.BeginTurnCapture();
        }
        budget?.StartSpeaking();
        StudentSpeakingChanged?.Invoke(true);
    }

    private void HandleSpeechEnd()
    {
        ISpeechBudget? budget;
        lock (_sync)
        {
            if (!_speechWindowOpen)
            {
                return;
            }

            _isSpeaking = false;
            budget = _budget;
            _speechEnded.TrySetResult(true);
            _speechStarted = NewSignal();
        }

        budget?.StopSpeaking();
        StudentSpeakingChanged?.Invoke(false);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CloseSpeechWindow();
        _sessionClient.OnVadSpeechStart -= HandleSpeechStart;
        _sessionClient.OnVadSpeechEnd -= HandleSpeechEnd;
    }
}
