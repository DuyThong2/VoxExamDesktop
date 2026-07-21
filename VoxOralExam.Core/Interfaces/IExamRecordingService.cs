using VoxOralExam.Core.Models;

namespace VoxOralExam.Core.Interfaces;

public interface IExamRecordingService
{
    event Action<RecordingStatus>? StatusChanged;

    bool IsRecording { get; }

    Task StartAsync(RecordingSessionContext context, CancellationToken cancellationToken);

    Task StopAsync(RecordingStopReason reason, CancellationToken cancellationToken);
}
