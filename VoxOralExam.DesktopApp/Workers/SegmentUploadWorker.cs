using System.IO;
using System.Net;
using System.Net.Http;
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
    private string _token = string.Empty;
    private int _activeUploads;

    public SegmentUploadWorker(
        SegmentUploadClient client,
        LocalSegmentStore store
    )
    {
        _client = client;
        _store = store;
    }

    public void Start(string token)
    {
        _token = token;
        _workerTask ??= RunAsync(_cts.Token);
        _signal.Release();
    }

    public void NotifyPendingSegment()
    {
        _signal.Release();
    }

    public async Task<bool> WaitUntilIdleAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (Volatile.Read(ref _activeUploads) == 0 &&
                    await _store.GetOutstandingCountAsync(ct) == 0)
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

            foreach (var segment in await _store.GetPendingSegmentsAsync(ct))
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
                    return;
                }
                catch (Exception ex)
                {
                    await _store.MarkFailedAsync(
                        segment,
                        ex.Message,
                        ct
                    );
                    break;
                }
                finally
                {
                    Interlocked.Decrement(ref _activeUploads);
                }

            }

            if (await _store.GetOutstandingCountAsync(ct) > 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    _signal.Release();
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
        Exception? lastError = null;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await _client.UploadAsync(segment, _token, ct);
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
