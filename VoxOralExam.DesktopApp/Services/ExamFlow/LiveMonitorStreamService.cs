using SIPSorceryMedia.Abstractions;
using Vortice.Direct3D11;
using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.Infra.Recording;
using VoxOralExam.DesktopApp.Infra.Recording.Audio;
using VoxOralExam.DesktopApp.Infra.Recording.Capture;
using VoxOralExam.DesktopApp.Infra.Recording.Interop;
using VoxOralExam.DesktopApp.Infra.WebRtc;
using VoxOralExam.DesktopApp.State;
using Windows.Graphics.DirectX.Direct3D11;

namespace VoxOralExam.DesktopApp.Services.ExamFlow;

/// <summary>
/// Streams camera + screen live to vox-streaming over WebRTC (MonitorStreamClient, one connection
/// per stream type, matching demo/web/student.js's per-streamType session model) so a teacher
/// watching the monitor UI sees the exam in real time -- entirely separate from
/// ExamRecordingService's local-recording + segment-upload pipeline, which remains the durable
/// evidence path. This service is best-effort only: any failure here (capture, encode, signaling)
/// is logged and degrades to that one stream not being visible live, and must never affect local
/// recording or the exam flow.
///
/// Audio matches the same design as local recording: Screen carries mixed mic + system/device audio
/// (via its own AudioMixer + SystemAudioLoopbackCapture), Camera carries mic-only. Capture is NOT
/// shared with ExamRecordingService's own capture/writer pipeline -- camera reuses the shared,
/// multi-consumer CameraService singleton (by design, see its own doc comment), but screen and mic
/// each get their own dedicated, independent capture instances here, mirroring the established
/// precedent of not threading new consumers into the already-sensitive local-recording state
/// machine (ScreenCaptureSource/WASAPI both support more than one independent open).
/// </summary>
public sealed class LiveMonitorStreamService : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly CameraService _camera;
    private readonly RecordingClock _clock;
    private readonly object _screenContextLock = new();

    private MonitorStreamClient? _cameraClient;
    private MonitorStreamClient? _screenClient;

    private TurnAudioRecorder? _cameraMic;
    private TurnAudioRecorder? _screenMic;
    private SystemAudioLoopbackCapture? _screenLoopback;
    private AudioMixer? _cameraAudioMixer;
    private AudioMixer? _screenAudioMixer;

    private ID3D11Device? _screenDevice;
    private IDirect3DDevice? _screenWinRtDevice;
    private ScreenCaptureSource? _screenCapture;

    private bool _cameraSubscribed;
    private bool _started;
    private bool _disposed;

    public LiveMonitorStreamService(AppSettings settings, CameraService camera, RecordingClock clock)
    {
        _settings = settings;
        _camera = camera;
        _clock = clock;
    }

    public async Task StartAsync(RecordingSessionContext context, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started || !_settings.EnableLiveMonitorStream)
        {
            return;
        }

        // Each stream type starts (and fails) on its own. A shared catch used to route ANY failure
        // through StopCoreAsync, which disposed both clients -- so a screen-side problem also
        // dropped a perfectly healthy camera stream, and the server saw both peers close moments
        // after connecting.
        if (context.StreamTypes.Contains(RecordingStreamType.Camera))
        {
            try
            {
                await StartCameraAsync(context, ct);
                _started = true;
            }
            catch (Exception ex)
            {
                LocalFileLogger.Error("live_monitor_stream", "camera_start_failed", ex);
                await StopCameraAsync();
            }
        }

        if (context.StreamTypes.Contains(RecordingStreamType.Screen))
        {
            try
            {
                await StartScreenAsync(context, ct);
                _started = true;
            }
            catch (Exception ex)
            {
                LocalFileLogger.Error("live_monitor_stream", "screen_start_failed", ex);
                await StopScreenAsync();
            }
        }
    }

    private async Task StartCameraAsync(RecordingSessionContext context, CancellationToken ct)
    {
        var client = new MonitorStreamClient(
            _settings.StreamingBaseUrl, context.ScheduleId, RecordingStreamType.Camera, context.StreamToken,
            _settings.MonitorStreamOrigin, _settings.CameraFps, _settings.MonitorStreamVideoBitrate,
            _settings.StunUrls, _settings.TurnUrl, _settings.TurnUsername, _settings.TurnCredential);
        WireTransportLogging(client, RecordingStreamType.Camera);
        await client.ConnectAsync(ct);
        _cameraClient = client;

        // Camera audio is mic-only, but it still goes through an AudioMixer instead of being pushed
        // straight from the recorder. The mixer ticks on its own 20ms timer and its mic buffer is
        // ReadFully, so the audio track keeps emitting frames -- silence -- even when no mic could
        // be opened at all. That matters well beyond the audio itself: vox-streaming only starts
        // its ffmpeg ingest once BOTH a video and an audio track have arrived (see
        // noteFFmpegIngestTrack), so a silent-but-flowing track is what keeps recording and live
        // rewind working for this stream on a machine with no usable capture device.
        var mixer = new AudioMixer(_clock, AudioMixerLatencyPolicy.LiveMonitor);
        mixer.MixedAudioAvailable += OnCameraMixedAudio;
        // Field takes ownership before anything below can throw, so StopCameraAsync tears the mixer
        // down on the failure path too.
        _cameraAudioMixer = mixer;
        _cameraMic = await TryStartMicAsync(mixer, ct);
        mixer.Start();

        _camera.OnCapturedFrame += OnCameraFrame;
        _cameraSubscribed = true;
    }

    /// <summary>
    /// Opens a mic capture feeding <paramref name="mixer"/>, or returns null when the device can't
    /// be opened.
    ///
    /// Non-fatal by design, matching ExamRecordingService.TryStartMicAsync. This used to throw
    /// straight out of StartCameraAsync/StartScreenAsync -- and since it runs AFTER
    /// MonitorStreamClient.ConnectAsync has already completed the WebRTC handshake, an unavailable
    /// mic (the machine's only capture device unplugged: waveInOpen -> MMSYSERR_BADDEVICEID) tore
    /// down an established peer before a single frame of perfectly good video was sent. The server
    /// side of that looks like a peer closing the instant it reaches "connecting", with zero
    /// segments and no explanation.
    /// </summary>
    private static async Task<TurnAudioRecorder?> TryStartMicAsync(AudioMixer mixer, CancellationToken ct)
    {
        var mic = new TurnAudioRecorder();
        mic.StreamChunkAvailable += mixer.AddMicSamples;
        try
        {
            await mic.StartAsync(ct);
            return mic;
        }
        catch (OperationCanceledException)
        {
            // The whole start is being abandoned, not a device problem -- let it propagate.
            mic.StreamChunkAvailable -= mixer.AddMicSamples;
            mic.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            mic.StreamChunkAvailable -= mixer.AddMicSamples;
            mic.Dispose();
            LocalFileLogger.Error("live_monitor_stream", "mic_start_failed", ex);
            return null;
        }
    }

    private async Task StartScreenAsync(RecordingSessionContext context, CancellationToken ct)
    {
        var client = new MonitorStreamClient(
            _settings.StreamingBaseUrl, context.ScheduleId, RecordingStreamType.Screen, context.StreamToken,
            _settings.MonitorStreamOrigin, _settings.ScreenRecordingFps, _settings.MonitorStreamVideoBitrate,
            _settings.StunUrls, _settings.TurnUrl, _settings.TurnUsername, _settings.TurnCredential);
        WireTransportLogging(client, RecordingStreamType.Screen);
        await client.ConnectAsync(ct);
        _screenClient = client;

        // Live policy: a teacher watching in real time is better served by skipping stale audio
        // than by hearing it late, the opposite of ExamRecordingService's choice.
        var mixer = new AudioMixer(_clock, AudioMixerLatencyPolicy.LiveMonitor);
        mixer.MixedAudioAvailable += OnScreenMixedAudio;
        // Owned by the field before anything below can throw -- see StartCameraAsync.
        _screenAudioMixer = mixer;
        // Degrades to system-audio-only (or, if the loopback below also fails, to silence) rather
        // than taking the stream down -- same policy the loopback has always had.
        _screenMic = await TryStartMicAsync(mixer, ct);

        var loopback = new SystemAudioLoopbackCapture();
        try
        {
            await loopback.StartAsync(ct);
            loopback.DataAvailable += mixer.AddLoopbackSamples;
            mixer.EnableLoopback(loopback.WaveFormat!);
            _screenLoopback = loopback;
        }
        catch (Exception ex)
        {
            // Degrade to mic-only live audio, same policy as ExamRecordingService's local
            // recording: a missing/unavailable playback device must not block the live view.
            loopback.Dispose();
            LocalFileLogger.Error("live_monitor_stream", "loopback_start_failed", ex);
        }

        mixer.Start();

        (_screenDevice, _screenWinRtDevice) = Direct3D11Interop.CreateSharedDevice();
        var capture = new ScreenCaptureSource(
            _screenDevice,
            _screenWinRtDevice,
            _clock,
            _screenContextLock,
            Math.Clamp(_settings.ScreenRecordingFps, 1, 60));
        capture.FrameArrived += OnScreenFrame;
        capture.CaptureFailed += OnScreenCaptureFailed;
        capture.Initialize();
        capture.Start();
        _screenCapture = capture;
    }

    /// <summary>
    /// Puts this stream's transport lifecycle into the client log.
    ///
    /// <para>Nothing used to subscribe to OnConnectionStateChanged -- on any of the three WebRTC
    /// clients -- so a live view that died mid-exam left no trace anywhere: the recording carried
    /// on, the exam carried on, and the only evidence was a tile going grey on a screen nobody was
    /// necessarily watching. These lines are what make "the proctor says they lost the picture at
    /// 10:32" a checkable claim afterwards.</para>
    /// </summary>
    private static void WireTransportLogging(MonitorStreamClient client, RecordingStreamType streamType)
    {
        client.OnConnectionStateChanged += state => LocalFileLogger.Info(
            "live_monitor_stream",
            "peer_state_changed",
            new { streamType = streamType.ToString(), state = state.ToString() });

        client.OnReconnecting += () => LocalFileLogger.Error(
            "live_monitor_stream",
            "live_view_lost_rebuilding",
            new InvalidOperationException($"Live monitor stream for {streamType} dropped; rebuilding."),
            new { streamType = streamType.ToString() });

        client.OnReconnected += attempts => LocalFileLogger.Info(
            "live_monitor_stream",
            "live_view_restored",
            new { streamType = streamType.ToString(), attempts });
    }

    private void OnCameraFrame(CameraFrame frame) =>
        _cameraClient?.PushVideoFrame(frame.Data, frame.Width, frame.Height, VideoPixelFormatsEnum.Bgr, frame.Timestamp);

    private void OnCameraMixedAudio(byte[] pcm, TimeSpan timestamp) => _cameraClient?.PushAudioPcm(pcm);

    private void OnScreenMixedAudio(byte[] pcm, TimeSpan timestamp) => _screenClient?.PushAudioPcm(pcm);

    private void OnScreenFrame(ID3D11Texture2D texture, TimeSpan timestamp)
    {
        using (texture)
        {
            try
            {
                var description = texture.Description;
                var width = (int)description.Width;
                var height = (int)description.Height;
                var bgra = Direct3D11Interop.ReadBackBgra(_screenDevice!, _screenContextLock, texture, width, height);
                _screenClient?.PushVideoFrame(bgra, width, height, VideoPixelFormatsEnum.Bgra, timestamp);
            }
            catch (Exception ex)
            {
                LocalFileLogger.Error("live_monitor_stream", "screen_frame_failed", ex);
            }
        }
    }

    private void OnScreenCaptureFailed(Exception exception) =>
        LocalFileLogger.Error("live_monitor_stream", "screen_capture_failed", exception);

    public async Task StopAsync()
    {
        if (!_started)
        {
            return;
        }

        await StopCoreAsync();
        _started = false;
    }

    private async Task StopCoreAsync()
    {
        await StopCameraAsync();
        await StopScreenAsync();
    }

    // Both of these tear down only what is actually set, so they are safe to call on a stream that
    // failed halfway through starting (which is exactly what StartAsync does) as well as on a fully
    // running one.
    private async Task StopCameraAsync()
    {
        if (_cameraSubscribed)
        {
            _camera.OnCapturedFrame -= OnCameraFrame;
            _cameraSubscribed = false;
        }

        // Mic before mixer: the recorder feeds the mixer's buffer, so stopping it first means no
        // AddMicSamples can land on a disposed provider.
        if (_cameraMic is not null)
        {
            await _cameraMic.StopAsync();
            _cameraMic.Dispose();
            _cameraMic = null;
        }

        if (_cameraAudioMixer is not null)
        {
            _cameraAudioMixer.MixedAudioAvailable -= OnCameraMixedAudio;
            _cameraAudioMixer.Dispose();
            _cameraAudioMixer = null;
        }

        if (_cameraClient is not null)
        {
            await _cameraClient.DisposeAsync();
            _cameraClient = null;
        }
    }

    private async Task StopScreenAsync()
    {
        if (_screenCapture is not null)
        {
            _screenCapture.FrameArrived -= OnScreenFrame;
            _screenCapture.CaptureFailed -= OnScreenCaptureFailed;
            _screenCapture.Stop();
            _screenCapture.Dispose();
            _screenCapture = null;
        }

        // After the capture that draws on it has stopped.
        (_screenWinRtDevice as IDisposable)?.Dispose();
        _screenWinRtDevice = null;
        _screenDevice?.Dispose();
        _screenDevice = null;

        if (_screenMic is not null)
        {
            await _screenMic.StopAsync();
            _screenMic.Dispose();
            _screenMic = null;
        }

        if (_screenLoopback is not null)
        {
            await _screenLoopback.StopAsync();
            _screenLoopback.Dispose();
            _screenLoopback = null;
        }

        if (_screenAudioMixer is not null)
        {
            _screenAudioMixer.MixedAudioAvailable -= OnScreenMixedAudio;
            _screenAudioMixer.Dispose();
            _screenAudioMixer = null;
        }

        if (_screenClient is not null)
        {
            await _screenClient.DisposeAsync();
            _screenClient = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopCoreAsync();
    }
}
