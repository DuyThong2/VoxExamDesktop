using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using NAudio.Codecs;
using NAudio.Wave;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;
using Vpx.Net;

namespace VoxOralExam.DesktopApp.Infra.Clients.AIService;

/// <summary>
/// Recvonly WebRTC client for the avatar's rendered video+audio (Phase 5 of
/// docs/realtime-self-hosted-avatar-plan.md). A SEPARATE RTCPeerConnection from
/// Infra/Clients/AIService/WebRtcClient.cs's proctoring connection (which sends the webcam, not
/// receives) -- opened once at exam start and held open for the whole attempt, never
/// renegotiated between questions, mirroring Python's realtime/avatar_webrtc.py.
///
/// Codecs are restricted to VP8 (video) and PCMU/G.711 (audio) in the SDP offer, the same way
/// WebRtcClient.cs restricts its own offer to VP8 -- both decode with packages already
/// referenced by this project (Vpx.Net for VP8, NAudio's built-in MuLawDecoder for PCMU), so no
/// new native dependency (e.g. FFmpeg) is needed just for the avatar.
/// </summary>
public sealed class AvatarWebRtcClient : IDisposable
{
    private const int RTP_AUDIO_CLOCK_RATE = 8000;

    // The Python side sends true-zero PCM for idle silence and only ever sends real (non-zero)
    // amplitude while a TTS utterance is actually playing (see realtime/avatar_webrtc.py's
    // AvatarAudioTrack) -- so a simple amplitude threshold on the decoded PCM doubles as a
    // "is the avatar currently speaking" signal, with no extra backend protocol needed. The
    // frame/streak counts give a little hysteresis (avoid flicker between words, avoid an
    // instant flip back to idle on a short natural pause).
    private const short SpeakingAmplitudeThreshold = 400;
    private const int SpeakingOnStreak = 2;
    // A natural TTS pause between an instruction sentence and the actual question can easily be
    // longer than 300ms; if we flip to "not speaking" too quickly, RealtimeExamFlowService opens
    // the student's answer window while the avatar is only pausing mid-utterance. Hold silence
    // for about one second before declaring the avatar finished speaking.
    private const int SpeakingOffStreak = 50;

    // Mirrors RealtimeSessionClient's reconnect backoff -- this connection has no durable
    // server-side state to resume (avatar media is recvonly and stateless from WPF's point of
    // view), so a plain re-offer/re-answer handshake is all reconnect needs.
    private static readonly TimeSpan[] ReconnectBackoff =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)
    ];

    // PCMU frames arrive roughly every 20ms under normal conditions; a gap several times that
    // signals a real stall worth reacting to (see HandleAudioFrameReceived) rather than ordinary
    // jitter.
    private static readonly TimeSpan AudioGapThreshold = TimeSpan.FromMilliseconds(400);

    private readonly AppSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ExamSessionState _sessionState;

    private RTCPeerConnection? _peerConnection;
    private VP8Codec? _vp8Decoder;
    private BufferedWaveProvider? _waveProvider;
    private WaveOutEvent? _waveOut;
    private bool _isDisposed;
    private bool _isSpeaking;
    private int _loudStreak;
    private int _quietStreak;
    private Guid _examAttemptId;
    private bool _intentionalClose;
    private bool _isReconnecting;
    private DateTime? _lastAudioFrameReceivedAt;

    public event Action<BitmapImage>? OnVideoFrame;
    public event Action<RTCPeerConnectionState>? OnConnectionStateChanged;
    public event Action<bool>? OnSpeakingChanged;
    /// <summary>Fired after a dropped connection (state=failed) is successfully re-established.</summary>
    public event Action? OnReconnected;
    /// <summary>Fired once, the first time ReconnectBackoff's fast attempts (~30s total) are
    /// exhausted without success -- signals a likely real outage rather than a brief blip. Does
    /// NOT mean reconnect gave up: an indefinite slower retry loop (LongOutageRetryInterval)
    /// keeps running afterward and still fires OnReconnected if/when it eventually succeeds. The
    /// caller should surface this as "still trying to reconnect", not as a fatal error.</summary>
    public event Action? OnReconnecting;

    public AvatarWebRtcClient(AppSettings settings, IHttpClientFactory httpClientFactory, ExamSessionState sessionState)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _sessionState = sessionState;
    }

    /// <summary>WinMM output device indices/names, numbered the same way as WaveOutEvent.DeviceNumber
    /// (both go through NAudio's WaveOut* WinMM binding), so a device picked here is safe to hand
    /// straight to a WaveOutEvent's DeviceNumber -- mirrors TurnAudioRecorder.ListInputDevices().</summary>
    public static IReadOnlyList<(int DeviceIndex, string ProductName)> ListOutputDevices()
    {
        var devices = new List<(int DeviceIndex, string ProductName)>();
        for (var index = 0; index < WaveOut.DeviceCount; index++)
        {
            var caps = WaveOut.GetCapabilities(index);
            devices.Add((index, caps.ProductName));
        }

        return devices;
    }

    public async Task ConnectAsync(Guid examAttemptId, CancellationToken ct)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(AvatarWebRtcClient));
        }

        _examAttemptId = examAttemptId;
        _intentionalClose = false;

        _waveProvider = new BufferedWaveProvider(new WaveFormat(RTP_AUDIO_CLOCK_RATE, 16, 1))
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(5)
        };
        _waveOut = new WaveOutEvent { DeviceNumber = _sessionState.SelectedAudioOutputDeviceIndex };
        _waveOut.Init(_waveProvider);
        _waveOut.Play();

        await ConnectCoreAsync(examAttemptId, ct);
    }

    /// <summary>
    /// Builds a fresh RTCPeerConnection and does the offer/answer handshake. Split out from
    /// ConnectAsync so AttemptReconnectAsync can re-run just this part after a dropped
    /// connection without tearing down and re-initializing the audio output device each retry.
    /// </summary>
    private async Task ConnectCoreAsync(Guid examAttemptId, CancellationToken ct)
    {
        _vp8Decoder = new VP8Codec();

        _peerConnection = new RTCPeerConnection(null);
        _peerConnection.onconnectionstatechange += state =>
        {
            LocalFileLogger.Info("avatar_webrtc", "connection_state_changed", new { state = state.ToString() });
            OnConnectionStateChanged?.Invoke(state);

            if (state == RTCPeerConnectionState.failed && !_intentionalClose)
            {
                LocalFileLogger.Info("avatar_webrtc", "unexpected_disconnect", new { _examAttemptId });
                _ = AttemptReconnectAsync();
            }
        };

        var videoCapabilities = new List<SDPAudioVideoMediaFormat> { new(SDPMediaTypesEnum.video, 96, "VP8/90000") };
        var videoTrack = new MediaStreamTrack(SDPMediaTypesEnum.video, false, videoCapabilities, MediaStreamStatusEnum.RecvOnly);
        _peerConnection.addTrack(videoTrack);

        var audioCapabilities = new List<SDPAudioVideoMediaFormat> { new(SDPMediaTypesEnum.audio, 0, "PCMU/8000") };
        var audioTrack = new MediaStreamTrack(SDPMediaTypesEnum.audio, false, audioCapabilities, MediaStreamStatusEnum.RecvOnly);
        _peerConnection.addTrack(audioTrack);

        _peerConnection.OnVideoFrameReceived += HandleVideoFrameReceived;
        _peerConnection.OnAudioFrameReceived += HandleAudioFrameReceived;

        var offer = _peerConnection.createOffer();
        await _peerConnection.setLocalDescription(offer);

        var answerSdp = await PostOfferAsync(examAttemptId, offer.sdp, offer.type.ToString(), ct);
        _peerConnection.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = answerSdp
        });

        LocalFileLogger.Info("avatar_webrtc", "connected", new { examAttemptId });
    }

    // Mirrors RealtimeSessionClient's LongOutageRetryInterval -- once the fast backoff is
    // exhausted this is likely a real outage (internet down at the exam site), not a blip, so
    // keep retrying indefinitely at a fixed interval instead of giving up permanently.
    private static readonly TimeSpan LongOutageRetryInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Re-establishes the avatar peer connection after it was declared "failed" -- e.g. because
    /// Python's event loop was blocked long enough (a slow follow-up-decision LLM call under a
    /// degraded network) that no RTP/ICE keepalive could be sent, and consent checks on this side
    /// timed out. Without this, the avatar goes permanently silent for the rest of the exam even
    /// though Python keeps synthesizing and "completing" utterances into a dead connection.
    /// </summary>
    private async Task AttemptReconnectAsync()
    {
        if (_isReconnecting || _isDisposed || _intentionalClose)
        {
            return;
        }

        _isReconnecting = true;
        try
        {
            TeardownPeerConnection();
            _waveProvider?.ClearBuffer();
            ResetSpeakingState();

            foreach (var delay in ReconnectBackoff)
            {
                if (_isDisposed || _intentionalClose)
                {
                    return;
                }

                await Task.Delay(delay);
                if (await TryReconnectOnceAsync(delay))
                {
                    return;
                }
            }

            // Signals "this looks like a real outage, still retrying in the background at a
            // slower pace" -- not "gave up" (see the doc comment on OnReconnecting).
            LocalFileLogger.Error(
                "avatar_webrtc", "reconnect_short_backoff_exhausted",
                new InvalidOperationException("Short avatar reconnect backoff exhausted; entering long-retry mode."));
            OnReconnecting?.Invoke();

            while (!_isDisposed && !_intentionalClose)
            {
                await Task.Delay(LongOutageRetryInterval);
                if (await TryReconnectOnceAsync(LongOutageRetryInterval, longRetry: true))
                {
                    return;
                }
            }
        }
        finally
        {
            _isReconnecting = false;
        }
    }

    private async Task<bool> TryReconnectOnceAsync(TimeSpan delay, bool longRetry = false)
    {
        if (_isDisposed || _intentionalClose)
        {
            return false;
        }

        try
        {
            LocalFileLogger.Info(
                "avatar_webrtc", "reconnect_attempt",
                new { _examAttemptId, delaySeconds = delay.TotalSeconds, longRetry });
            await ConnectCoreAsync(_examAttemptId, CancellationToken.None);
            LocalFileLogger.Info("avatar_webrtc", "reconnected", new { _examAttemptId });
            OnReconnected?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("avatar_webrtc", "reconnect_attempt_failed", ex);
            TeardownPeerConnection();
            return false;
        }
    }

    /// <summary>Closes just the peer connection/decoder -- not the audio output device, which
    /// stays alive across reconnects so WaveOutEvent doesn't need to be re-initialized on every
    /// retry.</summary>
    private void TeardownPeerConnection()
    {
        _peerConnection?.close();
        _peerConnection = null;
        _vp8Decoder?.Dispose();
        _vp8Decoder = null;
        _lastAudioFrameReceivedAt = null;
    }

    private void HandleVideoFrameReceived(System.Net.IPEndPoint remote, uint timestamp, byte[] frame, VideoFormat format)
    {
        if (_vp8Decoder is null)
        {
            return;
        }

        try
        {
            foreach (var sample in _vp8Decoder.DecodeVideo(frame, VideoPixelFormatsEnum.I420, VideoCodecsEnum.VP8))
            {
                var bitmap = I420ToBitmapImage(sample.Sample, (int)sample.Width, (int)sample.Height);
                if (bitmap is not null)
                {
                    OnVideoFrame?.Invoke(bitmap);
                }
            }
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("avatar_webrtc", "video_decode_failed", ex);
        }
    }

    private static BitmapImage? I420ToBitmapImage(byte[] i420Bytes, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        using var i420Mat = new Mat(height * 3 / 2, width, MatType.CV_8UC1);
        System.Runtime.InteropServices.Marshal.Copy(i420Bytes, 0, i420Mat.Data, Math.Min(i420Bytes.Length, (int)(i420Mat.Total() * i420Mat.ElemSize())));

        using var bgrMat = new Mat();
        Cv2.CvtColor(i420Mat, bgrMat, ColorConversionCodes.YUV2BGR_I420);

        using var bitmap = BitmapConverter.ToBitmap(bgrMat);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Bmp);
        stream.Seek(0, SeekOrigin.Begin);

        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.StreamSource = stream;
        bitmapImage.EndInit();
        bitmapImage.Freeze();
        return bitmapImage;
    }

    private void HandleAudioFrameReceived(EncodedAudioFrame encodedFrame)
    {
        var encoded = encodedFrame.EncodedAudio;
        if (_waveProvider is null || encoded is null || encoded.Length == 0)
        {
            return;
        }

        try
        {
            // SIPSorceryMedia.Abstractions.EncodedAudioFrame carries no RTP sequence number or
            // timestamp -- only DurationMilliSeconds -- so real per-packet loss detection isn't
            // possible at this layer. What IS observable is the wall-clock gap between
            // successive frames: PCMU frames normally arrive back-to-back every ~20ms, so a much
            // bigger gap means something upstream stalled (network jitter, or the Python-side
            // event-loop freeze this was written to guard against -- see AttemptReconnectAsync).
            // If that backlog is just appended once the stall clears, BufferedWaveProvider plays
            // it back-to-back far faster than real time -- audibly a burst/garble ("rÃ¨") rather
            // than a clean gap. Dropping the stale backlog and resuming live from the freshest
            // frame trades a brief silence for avoiding that garble.
            var now = DateTime.UtcNow;
            if (_lastAudioFrameReceivedAt is { } lastReceivedAt && now - lastReceivedAt > AudioGapThreshold)
            {
                LocalFileLogger.Info("avatar_webrtc", "audio_gap_detected", new
                {
                    gapMilliseconds = (now - lastReceivedAt).TotalMilliseconds,
                });
                _waveProvider.ClearBuffer();
            }
            _lastAudioFrameReceivedAt = now;

            var pcm = new byte[encoded.Length * 2];
            long sumAbsAmplitude = 0;
            for (var i = 0; i < encoded.Length; i++)
            {
                var sample = MuLawDecoder.MuLawToLinearSample(encoded[i]);
                pcm[i * 2] = (byte)(sample & 0xFF);
                pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
                sumAbsAmplitude += Math.Abs(sample);
            }

            _waveProvider.AddSamples(pcm, 0, pcm.Length);
            UpdateSpeakingState(encoded.Length > 0 ? sumAbsAmplitude / encoded.Length : 0);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("avatar_webrtc", "audio_decode_failed", ex);
        }
    }

    private void UpdateSpeakingState(long averageAmplitude)
    {
        if (averageAmplitude >= SpeakingAmplitudeThreshold)
        {
            _quietStreak = 0;
            if (!_isSpeaking && ++_loudStreak >= SpeakingOnStreak)
            {
                _isSpeaking = true;
                OnSpeakingChanged?.Invoke(true);
            }
        }
        else
        {
            _loudStreak = 0;
            if (_isSpeaking && ++_quietStreak >= SpeakingOffStreak)
            {
                _isSpeaking = false;
                OnSpeakingChanged?.Invoke(false);
            }
        }
    }

    private void ResetSpeakingState()
    {
        _loudStreak = 0;
        _quietStreak = 0;
        if (_isSpeaking)
        {
            _isSpeaking = false;
            OnSpeakingChanged?.Invoke(false);
        }
    }

    private async Task<string> PostOfferAsync(Guid examAttemptId, string sdp, string type, CancellationToken ct)
    {
        var url = $"{_settings.PythonBaseUrl.TrimEnd('/')}{_settings.AvatarWebRtcOfferPath}/{examAttemptId:D}";
        var body = JsonSerializer.Serialize(new { sdp, type });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var client = _httpClientFactory.CreateClient();
        using var response = await client.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("sdp").GetString()
            ?? throw new InvalidOperationException("Python did not return an SDP answer for the avatar WebRTC offer.");
    }

    public Task DisconnectAsync()
    {
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        // Marks this an intentional close first so a state change fired by the close() call
        // below doesn't race AttemptReconnectAsync into starting.
        _intentionalClose = true;

        // Just like WebRtcClient.cs's DisconnectAsync, closing the local peer connection is
        // enough -- Python's own onconnectionstatechange handler (realtime/avatar_webrtc.py)
        // detects the resulting ICE/DTLS disconnect and cleans up server-side; no separate REST
        // call needed.
        TeardownPeerConnection();
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _waveProvider = null;
        ResetSpeakingState();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _intentionalClose = true;
        _isDisposed = true;
        TeardownPeerConnection();
        _waveOut?.Stop();
        _waveOut?.Dispose();
        ResetSpeakingState();
    }
}

