using VoxOralExam.DesktopApp.Dtos;
using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Services.ExamFlow.Turn;

internal sealed class TurnArchiveQueue : IDisposable
{
    private readonly object _sync = new();
    private readonly TurnAudioUploader _audioUploader;
    private readonly TurnArchiveClient _archiveClient;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _pending = [];
    private bool _disposed;

    public TurnArchiveQueue(
        TurnAudioUploader audioUploader,
        TurnArchiveClient archiveClient)
    {
        _audioUploader = audioUploader;
        _archiveClient = archiveClient;
    }

    public void Enqueue(TurnArchiveWorkItem item)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending.RemoveAll(task => task.IsCompleted);
            _pending.Add(ArchiveAsync(item, _lifetime.Token));
            LocalFileLogger.Info("exam_flow", "archive_enqueued", new
            {
                item.AnswerId,
                item.TurnOrder,
                pcmBytes = item.Pcm.Length,
                pendingCount = _pending.Count
            });
        }
    }

    public async Task DrainAsync(TimeSpan timeout)
    {
        Task[] pending;
        lock (_sync)
        {
            pending = _pending.Where(task => !task.IsCompleted).ToArray();
        }
        if (pending.Length == 0)
        {
            LocalFileLogger.Info("exam_flow", "archive_drain_noop", null);
            return;
        }

        // Timing this matters: a successful drain used to be completely silent, so there was no
        // way to tell "nothing to drain" from "drained one turn in 12s" -- and that second number
        // is exactly what PostArchiveSettleSeconds has to be tuned against.
        var startedAt = DateTime.UtcNow;
        try
        {
            await Task.WhenAll(pending).WaitAsync(timeout);
            LocalFileLogger.Info("exam_flow", "archive_drain_complete", new
            {
                drained = pending.Length,
                elapsedMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds
            });
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "pending_archives_incomplete", ex, new
            {
                pending = pending.Length,
                timeoutSeconds = timeout.TotalSeconds,
                elapsedMs = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds
            });
        }
    }

    private async Task ArchiveAsync(
        TurnArchiveWorkItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            var wav = _audioUploader.EncodeWav(item.Pcm);
            var audioUrl = await _audioUploader.UploadTurnAudioAsync(
                wav,
                item.AnswerId,
                item.TurnOrder,
                cancellationToken);
            await _archiveClient.ArchiveTurnAsync(
                new EvaluateTurnRequest
                {
                    AudioRef = audioUrl,
                    AnswerId = item.AnswerId,
                    PaperItemId = item.PaperItemId,
                    TurnOrder = item.TurnOrder,
                    PromptText = item.PromptText,
                    Language = "en",
                    DurationSeconds = item.DurationSeconds,
                    Question = item.Question
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            LocalFileLogger.Info(
                "exam_flow",
                "archive_turn_cancelled",
                new { item.AnswerId, item.TurnOrder });
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error(
                "exam_flow",
                "archive_turn_failed",
                ex,
                new { item.AnswerId, item.TurnOrder });
        }
    }

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
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
