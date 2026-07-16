using VoxOralExam.Core.Models;

namespace VoxOralExam.DesktopApp.Services.ExamFlow;

public sealed class QuestionAssetPresentationCoordinator
{
    private const int DefaultMediaTimeoutSeconds = 30;

    private TaskCompletionSource<bool>? _pendingMediaCompletionTcs;

    public event Action<QuestionAsset?>? OnAssetDisplayRequested;

    public async Task PresentAsync(QuestionAsset asset, int preparationTimeSeconds, CancellationToken ct)
    {
        OnAssetDisplayRequested?.Invoke(asset);

        if (asset.Type == QuestionAssetType.Image || asset.Type == QuestionAssetType.TextPassage)
        {
            var waitSeconds = Math.Max(0, preparationTimeSeconds);
            if (waitSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
            }

            return;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingMediaCompletionTcs = tcs;

        try
        {
            var timeoutSeconds = asset.DurationSeconds ?? preparationTimeSeconds;
            if (timeoutSeconds <= 0)
            {
                timeoutSeconds = DefaultMediaTimeoutSeconds;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var registration = timeoutCts.Token.Register(() => tcs.TrySetResult(false));
            await tcs.Task;
        }
        finally
        {
            if (ReferenceEquals(_pendingMediaCompletionTcs, tcs))
            {
                _pendingMediaCompletionTcs = null;
            }
        }
    }

    public void CompleteMediaPlayback()
    {
        _pendingMediaCompletionTcs?.TrySetResult(true);
    }

    public void Clear()
    {
        _pendingMediaCompletionTcs?.TrySetResult(false);
        _pendingMediaCompletionTcs = null;
        OnAssetDisplayRequested?.Invoke(null);
    }
}

