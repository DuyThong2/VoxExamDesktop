namespace VoxOralExam.Core.Models;

public enum RecordingStreamType
{
    Camera,
    Screen
}

public enum RecordingStopReason
{
    Submitted,
    Expired,
    UserClosed,
    ApplicationShutdown,
    CaptureFailure
}

public sealed record RecordingSessionContext(
    Guid AttemptId,
    string ScheduleId,
    string SessionId,
    string StreamToken,
    IReadOnlyCollection<RecordingStreamType> StreamTypes
);

public sealed record RecordingStatus(
    string Code,
    string Message,
    bool IsDegraded = false
);

public sealed record CompletedSegment(
    string StreamId,
    RecordingStreamType StreamType,
    long Sequence,
    string FilePath,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long SizeBytes,
    string Sha256
);

public sealed class RecordingManifest
{
    public Guid AttemptId { get; init; }

    public string ScheduleId { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public List<StoredSegment> Segments { get; init; } = [];
}

public sealed class StoredSegment
{
    public string StreamId { get; init; } = string.Empty;

    public string StreamType { get; init; } = string.Empty;

    public long Sequence { get; init; }

    public string RelativePath { get; init; } = string.Empty;

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset EndedAt { get; init; }

    public string Sha256 { get; init; } = string.Empty;

    public SegmentUploadState State { get; set; }

    public string? LastError { get; set; }
}

public enum SegmentUploadState
{
    Pending,
    Uploading,
    Acknowledged,
    Failed
}
