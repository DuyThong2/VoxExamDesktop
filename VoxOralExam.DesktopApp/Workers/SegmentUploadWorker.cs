using System.IO;
using System.Net;
using System.Net.Http;
using System.Collections.Concurrent;
using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Infra.Clients.StreamService;
using VoxOralExam.DesktopApp.Infra.Recording.Storage;
using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Workers;

public sealed class SegmentUploadWorker : IAsyncDisposable
{
    private readonly SegmentUploadClient _client;
    private readonly LocalSegmentStore _store;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();

    private Task? _workerTask;
    private readonly ConcurrentDictionary<string, string> _uploadTokens = new(StringComparer.Ordinal);
    private int _activeUploads;

    public SegmentUploadWorker(
        SegmentUploadClient client,
        LocalSegmentStore store
    )
    {
        _client = client;
        _store = store;
    }

    public void Start(IEnumerable<StreamUploadSession> sessions)
    {
        foreach (var session in sessions)
        {
            if (string.IsNullOrWhiteSpace(session.UploadToken))
            {
                throw new InvalidOperationException($"Upload credential is missing for stream {session.StreamId}.");
            }
            _uploadTokens[session.StreamId] = session.UploadToken;
        }
        // Task.Run, not a direct call: Start() is invoked from the UI thread, and calling RunAsync
        // directly here would capture the WPF SynchronizationContext for all of its continuations
        // (every await inside the loop, with no ConfigureAwait(false) anywhere). DisposeAsync()
        // later does _cts.Cancel() then blocks the UI thread synchronously on this same task via
        // GetAwaiter().GetResult() -- if RunAsync's cancellation continuation needed to resume on
        // that same, now-blocked, UI thread, it never could: the same class of deadlock already
        // fixed in ScreenSegmentRecorder/CameraSegmentRecorder.StopAsync. Task.Run gives RunAsync a
        // threadpool context with nothing captured, so it can always make progress independently.
        _workerTask ??= Task.Run(() => RunAsync(_cts.Token));
        _signal.Release();
    }

    public void NotifyPendingSegment()
    {
        _signal.Release();
    }

    /// <summary>
    /// Swaps in a renewed upload credential for a stream already being uploaded. The stream id is
    /// unchanged -- vox-streaming resumes the same stream rather than opening a new one -- so
    /// nothing already uploaded is orphaned, and segments still queued simply go out under the new
    /// token. Also wakes the worker: a refresh usually follows a spell of failures, and there is no
    /// reason to sit out the remaining backoff now that they can succeed.
    /// </summary>
    public void UpdateUploadToken(string streamId, string uploadToken)
    {
        if (string.IsNullOrWhiteSpace(uploadToken))
        {
            return;
        }

        _uploadTokens[streamId] = uploadToken;
        _signal.Release();
    }

    public async Task<bool> WaitUntilIdleAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Volatile.Read(ref _activeUploads) == 0 &&
                    await _store.GetOutstandingCountAsync(RegisteredStreamIds(), ct) == 0)
                {
                    return true;
                }

                _signal.Release();
                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }

        return false;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            foreach (var segment in await _store.GetPendingSegmentsAsync(RegisteredStreamIds(), ct))
            {
                Interlocked.Increment(ref _activeUploads);
                try
                {
                    await _store.MarkUploadingAsync(segment, ct);
                    await UploadWithRetryAsync(segment, ct);
                    await _store.MarkAcknowledgedAsync(segment, ct);
                    try
                    {
                        File.Delete(segment.FilePath);
                    }
                    catch (Exception ex)
                    {
                        LocalFileLogger.Error(
                            "segment_upload",
                            "acknowledged_segment_delete_failed",
                            ex,
                            new { segment.StreamId, segment.Sequence });
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // MarkUploadingAsync above may already have flipped this segment to Uploading
                    // before ct fired -- leaving it there would be permanent: GetPendingSegmentsAsync
                    // only looks at Pending/Failed, so it would never be retried again, yet
                    // GetOutstandingCountAsync (!= Acknowledged) would count it forever, permanently
                    // blocking ExamRecordingService's per-stream /complete check. Reset it back to
                    // Failed with a fresh, uncancelled token so a later run (including startup
                    // recovery) picks it up again -- best-effort, since the local store itself may
                    // already be shutting down.
                    try
                    {
                        await _store.MarkFailedAsync(segment, "Upload cancelled during shutdown.", CancellationToken.None);
                    }
                    catch (Exception resetEx)
                    {
                        LocalFileLogger.Error(
                            "segment_upload",
                            "cancelled_segment_reset_failed",
                            resetEx,
                            new { segment.StreamId, segment.Sequence });
                    }

                    return;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                {
                    // The server holds different bytes under this sequence number and refuses to
                    // overwrite them (vox-streaming's SegmentUseCase.Upload). Retrying is pointless
                    // -- every attempt returns the same 409 -- and leaving it Failed would count as
                    // outstanding forever, blocking this stream's /complete permanently. Settle it
                    // terminally instead and surface the disagreement for a human.
                    //
                    // Only our own hash can be logged: the 409 body carries no server-side hash.
                    // Reporting both sides needs the client segment inventory work, which is where
                    // an anomaly like this belongs anyway.
                    await _store.MarkConflictedAsync(segment, ex.Message, CancellationToken.None);
                    LocalFileLogger.Error(
                        "segment_upload",
                        "segment_conflict_server_has_different_bytes",
                        ex,
                        new { segment.StreamId, segment.StreamType, segment.Sequence, LocalSha256 = segment.Sha256 });
                    continue;
                }
                catch (Exception ex)
                {
                    // continue, not break: GetPendingSegmentsAsync orders by (StreamId, Sequence),
                    // so breaking here means one segment stuck on a persistent (non-transient)
                    // rejection -- e.g. a 409 segment conflict, not retried by UploadWithRetryAsync
                    // at all -- permanently blocks every later segment of every stream from ever
                    // being attempted again: each pass re-fetches Pending-or-Failed segments in the
                    // same order, hits the same stuck segment first, and stops there again. That is
                    // exactly how a single bad early segment silently truncates an entire recording
                    // to just its first few seconds. Let the rest of the batch keep uploading; the
                    // one segment stays Failed and is retried on its own on the next pass.
                    await _store.MarkFailedAsync(
                        segment,
                        ex.Message,
                        ct
                    );
                    continue;
                }
                finally
                {
                    Interlocked.Decrement(ref _activeUploads);
                }

            }

            if (await _store.GetOutstandingCountAsync(RegisteredStreamIds(), ct) > 0)
            {
                try
                {
                    // Waiting ON the signal rather than sleeping beside it is what makes this
                    // backoff interruptible. It used to be Task.Delay(30s) followed by a Release,
                    // which meant NotifyPendingSegment()'s Release only incremented a semaphore
                    // nobody was awaiting -- the worker slept out the full 30 seconds regardless.
                    //
                    // That cost a whole camera recording: the final segment of a stream is always
                    // produced during StopAsync, and if the worker happened to be mid-sleep when it
                    // landed, ExamRecordingService's 20-second drain expired first, the stream was
                    // left with one outstanding segment, /complete was never called, and the 130
                    // segments already in S3 were never assembled. Waking on the signal removes that
                    // race entirely, and with it the requirement that the drain outlast this delay.
                    await _signal.WaitAsync(TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task UploadWithRetryAsync(CompletedSegment segment, CancellationToken ct)
    {
        if (!_uploadTokens.TryGetValue(segment.StreamId, out var uploadToken))
        {
            throw new InvalidOperationException($"No upload credential is registered for stream {segment.StreamId}.");
        }
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await _client.UploadAsync(segment, uploadToken, ct);
                return;
            }
            catch (HttpRequestException ex) when (IsTransient(ex.StatusCode))
            {
                lastError = ex;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex;
            }

            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));

            await Task.Delay(delay, ct);
        }
        throw new InvalidOperationException($"Upload segment {segment.Sequence} failed", lastError);
    }

    private static bool IsTransient(HttpStatusCode? statusCode)
    {
        if (!statusCode.HasValue)
        {
            return true;
        }

        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
               (int)statusCode.Value >= 500;
    }

    private IReadOnlySet<string> RegisteredStreamIds() =>
        _uploadTokens.Keys.ToHashSet(StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_workerTask is not null)
        {
            try
            {
                await _workerTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
        _signal.Dispose();
    }
}
