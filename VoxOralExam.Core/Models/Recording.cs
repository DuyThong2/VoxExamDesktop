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
    CaptureFailure,

    /// <summary>
    /// Never passed to StopAsync -- this run is not the one that did the recording.
    /// OrphanedUploadRecoveryService reports it when it finishes a stream whose original run died
    /// before reaching /complete, so the real reason was never observed. Distinguishing it from a
    /// stream that simply reported nothing is the point: it tells the server this recording was
    /// salvaged rather than completed normally, which is exactly the case where a short or gapped
    /// recording has an explanation.
    /// </summary>
    RecoveredAfterCrash
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
    string Sha256,
    long FramesWritten
);

/// <summary>
/// One segment as the client declares it to the server, uploaded or not. See
/// LocalSegmentStore.GetDeclaredSegmentsAsync for why the server needs to be told this at all.
/// </summary>
public sealed record DeclaredSegment(
    long Seq,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string Sha256,
    long SizeBytes,
    long FramesWritten
);

public sealed class RecordingManifest
{
    public Guid AttemptId { get; init; }

    public string ScheduleId { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    /// <summary>
    /// The upload credentials this attempt's streams were opened with, so a later run can finish
    /// what this one could not.
    ///
    /// Without them the manifest records which segments still need uploading but nothing that could
    /// actually upload them: the stream id and upload token used to live only in
    /// ExamRecordingService's memory and were dropped when the attempt ended, so a crash, a forced
    /// shutdown or a drain that ran out of time left the segments permanently stranded -- already
    /// half-uploaded to S3, with no way to send the rest and no way to ask for assembly.
    /// </summary>
    public List<StoredUploadSession> UploadSessions { get; init; } = [];

    public List<StoredSegment> Segments { get; init; } = [];
}

public sealed class StoredUploadSession
{
    public string StreamId { get; init; } = string.Empty;

    public string StreamType { get; init; } = string.Empty;

    public string UploadToken { get; set; } = string.Empty;

    /// <summary>
    /// When the server stops accepting this credential. vox-streaming sets it to the stream JWT's
    /// own expiry plus a fixed grace and never extends it on upload activity, so it is a hard
    /// deadline for any resumed upload, not a hint -- past it every request is answered 410 Gone.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set once /complete succeeded, so a later run knows this stream needs nothing more.</summary>
    public bool Completed { get; set; }
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

    public long SizeBytes { get; init; }

    /// <summary>
    /// Video frames in this segment. Reported to the server as part of the segment inventory: a
    /// count far below the segment's duration times its frame rate means the interval was recorded
    /// but not really captured -- a frozen capture, a covered camera, a starved encoder -- which no
    /// amount of gap analysis over sequence numbers can reveal, because the segment is present and
    /// correct as far as sequencing is concerned.
    /// </summary>
    public long FramesWritten { get; init; }

    public SegmentUploadState State { get; set; }

    public string? LastError { get; set; }
}

public enum SegmentUploadState
{
    Pending,
    Uploading,
    Acknowledged,

    /// <summary>Upload failed for a reason worth retrying. Picked up again on the next pass.</summary>
    Failed,

    /// <summary>
    /// The server already holds a DIFFERENT segment under this stream's sequence number and
    /// rejected ours with 409 (see vox-streaming's SegmentUseCase.Upload: same SHA-256 is accepted
    /// idempotently, a different one is refused rather than overwritten).
    ///
    /// Terminal, and deliberately NOT counted as outstanding. Retrying can only ever produce the
    /// same 409, and because the completion gate counts every non-Acknowledged segment, leaving
    /// these as Failed would block /complete for the whole stream forever -- the exact silent
    /// orphaning that loses an entire recording. The sequence itself is covered on the server, so
    /// for coverage purposes there is nothing missing; what needs attention is the disagreement,
    /// which is reported rather than resolved.
    /// </summary>
    Conflicted
}
