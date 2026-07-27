using System.Diagnostics;

namespace VoxOralExam.DesktopApp.Infra.Recording;

public sealed class RecordingClock
{
    private readonly Stopwatch _stopwatch = new();

    public DateTimeOffset StartedAtUtc { get; private set; }

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public bool IsRunning => _stopwatch.IsRunning;

    public void Start()
    {
        StartedAtUtc = DateTimeOffset.UtcNow;
        _stopwatch.Restart();
    }

    public void Stop() => _stopwatch.Stop();

    public DateTimeOffset ToUtc(TimeSpan timestamp) => StartedAtUtc + timestamp;
}
