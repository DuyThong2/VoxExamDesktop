using VoxOralExam.Core.Models;

namespace VoxOralExam.Core.Interfaces;

public interface IExamRecordingService
{
    event Action<RecordingStatus>? StatusChanged;

    bool IsRecording { get; }

    Task StartAsync(RecordingSessionContext context, CancellationToken cancellationToken);

    Task StopAsync(RecordingStopReason reason, CancellationToken cancellationToken);

    /// <summary>
    /// Tears down resources shared across the whole app lifetime (currently: the segment upload
    /// worker), not just this recording attempt. Call once, after StopAsync, from whichever window
    /// is closing for good -- both ExamWindow and StreamingDemoWindow are always the last window in
    /// their respective flows. Safe to call more than once.
    /// </summary>
    Task ShutdownAsync();
}
