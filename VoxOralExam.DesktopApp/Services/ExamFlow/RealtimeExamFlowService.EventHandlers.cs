using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Services.ExamFlow;

public partial class RealtimeExamFlowService
{
    private void HandleAvatarUtteranceComplete(int sequence, string utteranceText)
    {
        LocalFileLogger.Info("exam_flow", "avatar_utterance_complete_signal", new
        {
            sequence,
            utteranceText
        });
        _avatarUtteranceCompleteTcs?.TrySetResult(true);
    }

    private void HandleSpeakRequested(int sequence, string text, string? rate)
    {
        _ = HandleSpeakRequestedAsync(sequence, text, rate);
    }

    private async Task HandleSpeakRequestedAsync(int sequence, string text, string? rate)
    {
        var hasSpeech = !string.IsNullOrWhiteSpace(text);
        try
        {
            if (hasSpeech)
            {
                OnAvatarSpeakingChanged?.Invoke(true);
            }
            await _avatarSpeaker.SpeakAsync(text, rate, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "avatar_speak_failed", ex, new { sequence, text });
        }
        finally
        {
            if (hasSpeech)
            {
                OnAvatarSpeakingChanged?.Invoke(false);
            }
            HandleAvatarUtteranceComplete(sequence, text);
        }
    }

    private void HandleVadSpeechStart()
    {
        if (!_studentSpeechWindowOpen)
        {
            return;
        }

        if (_recorder is not null && !_recorder.IsTurnActive)
        {
            _recorder.BeginTurnCapture();
        }
        StartQuestionSpeechTimer();
        _prepInterruptTcs?.TrySetResult(true);
        _vadSpeechStartTcs?.TrySetResult(true);
        OnStudentSpeakingChanged?.Invoke(true);
    }

    private void HandleVadSpeechEnd()
    {
        if (!_studentSpeechWindowOpen)
        {
            return;
        }

        _vadSpeechEndTcs?.TrySetResult(true);
        StopQuestionSpeechTimer();
        OnStudentSpeakingChanged?.Invoke(false);
    }

    private void HandleSessionError(string message)
    {
        LocalFileLogger.Info("exam_flow", "realtime_session_error", new { message });
        OnStatusChanged?.Invoke($"Loi realtime session: {message}");
    }

    private void HandleForceEnded(string reason)
    {
        _forceEndRequested = true;
        _prepInterruptTcs?.TrySetResult(true);
        CloseStudentSpeechWindow();
        _avatarSpeaker.Stop();
        OnAvatarSpeakingChanged?.Invoke(false);
        LocalFileLogger.Info("exam_flow", "force_end_received", new { reason });
        OnStatusChanged?.Invoke("Bài thi đã tạm dừng để xem xét. Vui lòng liên hệ giám thị/nhà trường.");
        _runCts?.Cancel();
    }

    private void HandleSessionReconnecting()
    {
        // Fires once RealtimeSessionClient's fast reconnect backoff (~30s) is exhausted -- it
        // keeps retrying indefinitely in the background after this (see
        // RealtimeSessionClient.LongOutageRetryInterval), so this is "still trying", not fatal.
        LocalFileLogger.Info("exam_flow", "realtime_session_reconnecting", null);
        OnStatusChanged?.Invoke("Mat ket noi realtime session (co the do mat mang). Dang tiep tuc thu ket noi lai...");
    }

    private void HandleSessionReconnected(int lastArchivedTurnOrder)
    {
        // Best-effort realignment: logs the server's durable view so a mismatch against this
        // service's own in-flight turnOrder is at least visible. Does not (yet) skip/replay
        // turns to force agreement -- see the class doc's Phase 6 gap note.
        LocalFileLogger.Info("exam_flow", "realtime_session_reconnected", new { lastArchivedTurnOrder });
        OnStatusChanged?.Invoke("Da ket noi lai realtime session.");
    }

    private void HandleAvatarReconnecting()
    {
        // Mirrors HandleSessionReconnecting -- see AvatarWebRtcClient.OnReconnecting's doc
        // comment for why this isn't fatal either.
        LocalFileLogger.Info("exam_flow", "avatar_reconnecting", null);
        OnStatusChanged?.Invoke("Mat ket noi avatar (co the do mat mang). Dang tiep tuc thu ket noi lai...");
    }

    private void HandleAvatarReconnected()
    {
        LocalFileLogger.Info("exam_flow", "avatar_reconnected", null);
        OnStatusChanged?.Invoke("Da ket noi lai avatar.");
    }
}
