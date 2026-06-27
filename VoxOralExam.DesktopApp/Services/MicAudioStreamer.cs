using VoxOralExam.DesktopApp.Infrastructure;

namespace VoxOralExam.DesktopApp.Services;

/// <summary>
/// Forwards TurnAudioRecorder's continuous StreamChunkAvailable PCM frames to the realtime
/// WebSocket (Phase 5 of docs/realtime-self-hosted-avatar-plan.md). Thin by design: the
/// recorder owns the one NAudio capture device and the pre-roll/turn-buffer logic for archival
/// upload; this class only adds the continuous streaming side, exactly as Open Question 8
/// resolved.
/// </summary>
public sealed class MicAudioStreamer : IDisposable
{
    private TurnAudioRecorder? _recorder;
    private RealtimeSessionClient? _sessionClient;
    private bool _isStreaming;

    public void Start(TurnAudioRecorder recorder, RealtimeSessionClient sessionClient)
    {
        if (_isStreaming)
        {
            return;
        }

        _recorder = recorder;
        _sessionClient = sessionClient;
        _recorder.StreamChunkAvailable += HandleChunk;
        _isStreaming = true;
    }

    public void Stop()
    {
        if (!_isStreaming)
        {
            return;
        }

        if (_recorder is not null)
        {
            _recorder.StreamChunkAvailable -= HandleChunk;
        }

        _recorder = null;
        _sessionClient = null;
        _isStreaming = false;
    }

    private void HandleChunk(byte[] pcm)
    {
        var client = _sessionClient;
        if (client is null)
        {
            return;
        }

        // Fire-and-forget: NAudio's callback thread must never block on a WebSocket send.
        // SendAudioFrameAsync swallows its own failures (see RealtimeSessionClient) so a dropped
        // connection just silently stops streaming rather than throwing on a background thread.
        _ = client.SendAudioFrameAsync(pcm);
    }

    public void Dispose()
    {
        Stop();
    }
}
