using NAudio.Wave;
using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Infra.Devices;

/// <summary>
/// Captures whatever is playing through the default output device (WASAPI loopback), so the
/// Screen recording's audio track can include system/device audio (e.g. the avatar's TTS voice)
/// alongside the mic -- see AudioMixer, which combines this with a TurnAudioRecorder instance.
/// Independent of the exam mic: opening the default playback device has nothing to do with the
/// exam's STT/VAD microphone capture in RealtimeExamFlowService.
/// </summary>
public sealed class SystemAudioLoopbackCapture : IDisposable
{
    private WasapiLoopbackCapture? _capture;

    public event Action<byte[]>? DataAvailable;

    public WaveFormat? WaveFormat => _capture?.WaveFormat;

    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_capture is not null)
        {
            return Task.CompletedTask;
        }

        var capture = new WasapiLoopbackCapture();
        capture.DataAvailable += HandleDataAvailable;
        capture.RecordingStopped += HandleRecordingStopped;
        capture.StartRecording();
        _capture = capture;
        LocalFileLogger.Info("system_audio", "loopback_started", new
        {
            sampleRate = capture.WaveFormat.SampleRate,
            channels = capture.WaveFormat.Channels,
            bitsPerSample = capture.WaveFormat.BitsPerSample
        });
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        if (_capture is null)
        {
            return Task.CompletedTask;
        }

        var capture = _capture;
        _capture = null;
        capture.DataAvailable -= HandleDataAvailable;
        capture.RecordingStopped -= HandleRecordingStopped;
        capture.StopRecording();
        capture.Dispose();
        LocalFileLogger.Info("system_audio", "loopback_stopped");
        return Task.CompletedTask;
    }

    private void HandleDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
        {
            return;
        }

        // WasapiLoopbackCapture reuses e.Buffer across callbacks, same as WaveInEvent -- copy out
        // before returning.
        DataAvailable?.Invoke(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());
    }

    private void HandleRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // WasapiLoopbackCapture stops itself if the default playback device changes/disappears
        // mid-recording. AudioMixer just keeps mixing mic-only from that point on -- this is not
        // fatal to the recording, so only log it.
        if (e.Exception is not null)
        {
            LocalFileLogger.Error("system_audio", "loopback_recording_stopped_with_error", e.Exception);
        }
    }

    public void Dispose() => _ = StopAsync();
}
