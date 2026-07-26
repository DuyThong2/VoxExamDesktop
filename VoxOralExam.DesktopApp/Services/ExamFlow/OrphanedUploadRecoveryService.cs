using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Infra.Clients.StreamService;
using VoxOralExam.DesktopApp.Infra.Recording.Storage;
using VoxOralExam.DesktopApp.State;
using VoxOralExam.DesktopApp.Workers;

namespace VoxOralExam.DesktopApp.Services.ExamFlow;

/// <summary>
/// Finishes uploads that a previous run of the app never got to finish.
///
/// An attempt that ends abruptly -- a crash, a power cut, a forced shutdown, or simply a stop drain
/// that ran out of time with one straggling segment -- leaves its segments half-uploaded: some
/// already in S3, the rest on disk, and no /complete ever sent, so vox-streaming is never asked to
/// assemble any of it. Until the manifest started carrying the upload credentials
/// (RecordingManifest.UploadSessions) there was nothing a later run could have done about that;
/// this is the other half of that change.
///
/// Strictly best-effort and strictly additive: it only ever uploads segments that were already
/// captured and completes streams that were already open. It never deletes a recording, and any
/// failure leaves the attempt exactly as it found it, to be retried next launch.
/// </summary>
public sealed class OrphanedUploadRecoveryService
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SegmentUploadClient _uploadClient;
    private readonly StreamSessionClient _sessionClient;
    private readonly UploadCredentialRefresher _credentialRefresher;
    private readonly LocalSegmentStore _store;
    private readonly AppSettings _settings;

    public OrphanedUploadRecoveryService(
        SegmentUploadClient uploadClient,
        StreamSessionClient sessionClient,
        UploadCredentialRefresher credentialRefresher,
        LocalSegmentStore store,
        AppSettings settings)
    {
        _uploadClient = uploadClient;
        _sessionClient = sessionClient;
        _credentialRefresher = credentialRefresher;
        // Injected only for its BaseDirectory: the singleton is bound to whichever attempt is
        // recording now, so each orphan below gets its own store instance instead.
        _store = store;
        _settings = settings;
    }

    /// <summary>
    /// Sweeps every attempt left on disk. Safe to call at startup before any recording begins;
    /// caller is expected to run it in the background rather than block the UI on it.
    /// </summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        if (!_settings.EnableSegmentUpload)
        {
            return;
        }

        List<string> attemptDirectories;
        try
        {
            if (!Directory.Exists(_store.BaseDirectory))
            {
                return;
            }

            attemptDirectories = [.. Directory.EnumerateDirectories(_store.BaseDirectory)];
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("upload_recovery", "enumerate_attempts_failed", ex);
            return;
        }

        foreach (var attemptDirectory in attemptDirectories)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await RecoverAttemptAsync(attemptDirectory, ct);
            }
            catch (Exception ex)
            {
                // One unreadable or unusable attempt must not stop the others from being recovered.
                LocalFileLogger.Error(
                    "upload_recovery",
                    "attempt_recovery_failed",
                    ex,
                    new { attemptDirectory });
            }
        }
    }

    private async Task RecoverAttemptAsync(string attemptDirectory, CancellationToken ct)
    {
        var manifestPath = Path.Combine(attemptDirectory, "recording.json");
        if (!File.Exists(manifestPath))
        {
            return;
        }

        RecordingManifest? manifest;
        await using (var input = File.OpenRead(manifestPath))
        {
            manifest = await JsonSerializer.DeserializeAsync<RecordingManifest>(input, ManifestJsonOptions, ct);
        }

        if (manifest is null || manifest.UploadSessions.Count == 0)
        {
            // Either unreadable, or from before credentials were persisted. Nothing can be done for
            // it here; leave it on disk rather than quietly discard a recording.
            return;
        }

        var resumable = manifest.UploadSessions.Where(session => !session.Completed).ToList();
        if (resumable.Count == 0)
        {
            return;
        }

        var streamIds = resumable.Select(session => session.StreamId).ToHashSet(StringComparer.Ordinal);
        var unsettled = manifest.Segments.Count(segment =>
            streamIds.Contains(segment.StreamId) &&
            segment.State is not (SegmentUploadState.Acknowledged or SegmentUploadState.Conflicted));

        // A stream can still need /complete with nothing left to upload: that is exactly the case
        // where a drain timed out on the last segment and the segment later succeeded, or where the
        // app died between the final upload and the completion call.
        var hasCommittedSegments = manifest.Segments.Exists(segment => streamIds.Contains(segment.StreamId));
        if (!hasCommittedSegments)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var live = new List<StoredUploadSession>();

        foreach (var session in resumable)
        {
            if (session.ExpiresAt > now)
            {
                live.Add(session);
                continue;
            }

            // Expired, but not necessarily lost: if the exam is still inside its schedule window and
            // a student is signed in, the credential can be renewed for the same stream. That only
            // succeeds during an exam, which is rare at startup -- so it is attempted, not assumed.
            var renewed = await _credentialRefresher.TryRefreshAsync(
                manifest.AttemptId, session.StreamType, ct);
            if (renewed is not null &&
                string.Equals(renewed.StreamId, session.StreamId, StringComparison.Ordinal))
            {
                session.UploadToken = renewed.UploadToken;
                session.ExpiresAt = renewed.ExpiresAt;
                live.Add(session);
                continue;
            }

            // Logged loudly because it means real evidence is stranded: vox-streaming answers
            // 410 Gone past ExpiresAt, and past the exam window no token can be issued. The
            // server-side assembly watchdog still builds a recording from whatever did arrive, but
            // this run cannot add to it.
            LocalFileLogger.Error(
                "upload_recovery",
                "upload_credential_expired_segments_stranded",
                new InvalidOperationException(
                    $"Upload credential for stream {session.StreamId} expired at {session.ExpiresAt:O} " +
                    "and could not be renewed."),
                new { attemptDirectory, session.StreamId, session.StreamType, session.ExpiresAt });
        }

        if (live.Count == 0)
        {
            return;
        }

        LocalFileLogger.Info(
            "upload_recovery",
            "resuming_orphaned_attempt",
            new
            {
                attemptDirectory,
                manifest.AttemptId,
                streams = live.Count,
                unsettledSegments = unsettled
            });

        await ResumeAsync(manifest, live, ct);
    }

    private async Task ResumeAsync(
        RecordingManifest manifest,
        List<StoredUploadSession> sessions,
        CancellationToken ct)
    {
        // A dedicated store bound to this old attempt. InitializeAsync also resets any segment left
        // mid-flight (Uploading) back to Pending, which is precisely the state a killed process
        // leaves behind.
        var store = new LocalSegmentStore();
        var context = new RecordingSessionContext(
            manifest.AttemptId,
            manifest.ScheduleId,
            manifest.SessionId,
            // Only CreateAsync needs a stream token, and recovery never opens a new stream: it
            // reuses the credentials the original run already obtained.
            string.Empty,
            [.. sessions.Select(session => FromWireValue(session.StreamType)).Distinct()]);
        await store.InitializeAsync(context, ct);
        // Writes back any credential renewed above, so a run that gets interrupted again does not
        // have to renew from scratch -- and so the manifest never disagrees with what is in use.
        await store.SaveUploadSessionsAsync(sessions, ct);

        await using var worker = new SegmentUploadWorker(_uploadClient, store);
        worker.Start([.. sessions.Select(session => new StreamUploadSession(
            session.StreamId, session.StreamType, session.ExpiresAt, session.UploadToken))]);

        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drainCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings.RecordingUploadTimeoutSeconds)));
        await worker.WaitUntilIdleAsync(drainCts.Token);

        foreach (var session in sessions)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var outstanding = await store.GetOutstandingCountAsync(
                new HashSet<string> { session.StreamId }, ct);
            if (outstanding > 0)
            {
                LocalFileLogger.Error(
                    "upload_recovery",
                    "resume_incomplete_segments_still_outstanding",
                    new InvalidOperationException(
                        $"{outstanding} segment(s) still pending for stream {session.StreamId}."),
                    new { session.StreamId, session.StreamType, outstanding });
                continue;
            }

            try
            {
                // Declared before completing, so the server knows the expected set when it
                // assembles -- the original run may have died before ever sending one.
                var declared = await store.GetDeclaredSegmentsAsync(session.StreamId, ct);
                if (declared.Count > 0)
                {
                    await _sessionClient.DeclareInventoryAsync(
                        session.StreamId, session.UploadToken, complete: true, declared, ct);
                }

                await _sessionClient.CompleteAsync(
                    session.StreamId,
                    session.UploadToken,
                    RecordingStopReason.RecoveredAfterCrash,
                    ct);
                await store.MarkUploadSessionCompletedAsync(session.StreamId, ct);
                LocalFileLogger.Info(
                    "upload_recovery",
                    "orphaned_stream_completed",
                    new { session.StreamId, session.StreamType });
            }
            catch (Exception ex)
            {
                // Left uncompleted on purpose: the next launch retries it, and past ExpiresAt the
                // server-side assembly watchdog assembles whatever did arrive regardless.
                LocalFileLogger.Error(
                    "upload_recovery",
                    "orphaned_stream_complete_failed",
                    ex,
                    new { session.StreamId, session.StreamType });
            }
        }
    }

    private static RecordingStreamType FromWireValue(string streamType) =>
        string.Equals(streamType, "camera", StringComparison.OrdinalIgnoreCase)
            ? RecordingStreamType.Camera
            : RecordingStreamType.Screen;
}
