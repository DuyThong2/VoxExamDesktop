using VoxOralExam.DesktopApp.Services.ExamFlow.Turn;

namespace VoxOralExam.DesktopApp.Services.ExamFlow.Question;

internal sealed class QuestionSpeechBudget : ISpeechBudget, IDisposable
{
    private readonly object _sync = new();
    private readonly TimeSpan _limit;
    private readonly TaskCompletionSource _exceeded =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Timer _progressTimer;
    private TimeSpan _elapsed = TimeSpan.Zero;
    private DateTime? _speakingStartedAtUtc;
    private bool _disposed;

    public QuestionSpeechBudget(
        int maxResponseSeconds,
        double initialElapsedSeconds = 0)
    {
        _limit = TimeSpan.FromSeconds(Math.Max(0, maxResponseSeconds));
        _elapsed = TimeSpan.FromSeconds(Math.Max(0, initialElapsedSeconds));
        _progressTimer = new Timer(
            _ => PublishProgress(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(250));
    }

    public event Action<TimeSpan, TimeSpan>? ProgressChanged;

    public Task ExceededTask => _exceeded.Task;

    public double ElapsedSeconds
    {
        get
        {
            lock (_sync)
            {
                return GetElapsedLocked(DateTime.UtcNow).TotalSeconds;
            }
        }
    }

    public bool IsExceeded
    {
        get
        {
            lock (_sync)
            {
                return IsExceededLocked(DateTime.UtcNow);
            }
        }
    }

    public void StartSpeaking()
    {
        lock (_sync)
        {
            if (!_disposed && _speakingStartedAtUtc is null)
            {
                _speakingStartedAtUtc = DateTime.UtcNow;
            }
        }
        PublishProgress();
    }

    public void StopSpeaking()
    {
        lock (_sync)
        {
            if (_speakingStartedAtUtc is DateTime startedAt)
            {
                _elapsed += DateTime.UtcNow - startedAt;
                _speakingStartedAtUtc = null;
            }
        }
        PublishProgress();
    }

    private void PublishProgress()
    {
        TimeSpan elapsed;
        bool exceeded;
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            elapsed = GetElapsedLocked(now);
            exceeded = IsExceededLocked(now);
        }

        if (exceeded)
        {
            _exceeded.TrySetResult();
        }
        ProgressChanged?.Invoke(elapsed, _limit);
    }

    private TimeSpan GetElapsedLocked(DateTime now)
    {
        var elapsed = _elapsed;
        if (_speakingStartedAtUtc is DateTime startedAt)
        {
            elapsed += now - startedAt;
        }
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private bool IsExceededLocked(DateTime now) =>
        _limit > TimeSpan.Zero && GetElapsedLocked(now) >= _limit;

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        StopSpeaking();
        _progressTimer.Dispose();
        PublishProgress();
    }
}
