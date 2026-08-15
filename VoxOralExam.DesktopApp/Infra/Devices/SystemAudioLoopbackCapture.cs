using NAudio.CoreAudioApi;
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
    private WasapiRecorder? _capture;

    public event Action<byte[]>? DataAvailable;

    public WaveFormat? WaveFormat => _capture?.WaveFormat;

    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_capture is not null)
        {
            return Task.CompletedTask;
        }

        // WithLoopbackCapture with no WithDevice() means "the default render device", which is what
        // the old WasapiLoopbackCapture did. Deliberately system-wide rather than
        // WithProcessLoopback(ProcessId): capturing only this app's own audio would drop everything
        // playing elsewhere on the machine, and a student playing prepared answers out of another
        // application is exactly the thing this track exists to catch.
        var capture = new WasapiRecorderBuilder()
            .WithLoopbackCapture()
            .Build();
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

    private void HandleDataAvailable(
        ReadOnlySpan<byte> buffer,
        AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        // WASAPI documents the buffer contents as undefined when it flags a packet silent, and
        // AudioMixer sums whatever arrives straight into the exam recording -- so hand on real
        // zeroes rather than trusting the bytes behind the flag.
        if ((flags & AudioClientBufferFlags.Silent) != 0)
        {
            DataAvailable?.Invoke(new byte[buffer.Length]);
            return;
        }

        // The span points directly at the WASAPI buffer and is only valid for the duration of this
        // callback, so copying out before returning is mandatory now, not merely defensive.
        DataAvailable?.Invoke(buffer.ToArray());
    }

    private void HandleRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // The recorder stops itself if the default playback device changes/disappears
        // mid-recording. AudioMixer just keeps mixing mic-only from that point on -- this is not
        // fatal to the recording, so only log it.
        if (e.Exception is not null)
        {
            LocalFileLogger.Error("system_audio", "loopback_recording_stopped_with_error", e.Exception);
        }
    }

    public void Dispose() => _ = StopAsync();
}
