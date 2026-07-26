using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Infra.WebRtc.VideoEncoding;
using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Infra.WebRtc;

/// <summary>
/// Live WebRTC connection to vox-streaming's /ws/stream signaling endpoint for ONE recording
/// stream (camera or screen), sending H.264 video + Opus audio so a teacher watching via the
/// monitor UI sees it in real time. Entirely independent of and parallel to
/// ExamRecordingService's local-recording + segment-upload pipeline (the durable evidence path) --
/// this is a best-effort live view only; any failure here must never affect local recording.
///
/// Mirrors Infra/Clients/AIService/WebRtcClient.cs's RTCPeerConnection/encode/SendVideo pattern,
/// with 3 differences: WebSocket offer/answer/ICE signaling (matching demo/web/student.js) instead
/// of HTTP POST + SSE, H.264 (via MediaFoundationH264Encoder) instead of VP8 (server only accepts
/// H.264/Opus -- see vox-streaming/internal/transport/webrtc/api.go's registerCodecs), and an
/// added Opus audio track.
/// </summary>
public sealed class MonitorStreamClient : IAsyncDisposable
{
    private const int VideoClockRate = 90_000;

    // RFC 7587: Opus's RTP timestamp clock is always 48kHz, regardless of the codec's actual
    // internal encode sample rate (OpusAudioEncoder encodes at 16kHz to match the mic's capture
    // format -- the SDP channel count of 2 is also an RFC 7587 convention, independent of the
    // actual mono content being encoded).
    private const int OpusRtpClockRate = 48_000;
    private const uint AudioDurationPerFrame = OpusRtpClockRate * OpusAudioEncoder.FrameMilliseconds / 1000;

    private static readonly JsonSerializerOptions SignalJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _wsUrl;
    private readonly string _origin;
    private readonly int _videoFps;
    private readonly int _videoBitrate;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // Only used to seed the very first frame's RTP duration, before any real inter-frame gap is
    // known -- every subsequent frame's duration comes from PushVideoFrame's own captureTimestamp
    // instead (see VideoEncodeWorkerAsync). Its exact value only shifts the whole video timeline by
    // one nominal frame interval, which is harmless; it does not accumulate.
    private readonly uint _firstFrameVideoDuration;

    private ClientWebSocket? _ws;
    private RTCPeerConnection? _pc;
    private MediaFoundationH264Encoder? _videoEncoder;
    private OpusAudioEncoder? _audioEncoder;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoopTask;

    // Capture callbacks can arrive from more than one thread for the same stream (e.g.
    // ScreenCaptureSource's Windows Graphics Capture callback AND its separate keep-alive Timer,
    // both independently invoking FrameArrived -- see its own comments). MediaFoundationH264Encoder
    // and RTCPeerConnection.SendVideo are not safe to call concurrently, so every frame is queued here
    // and drained by exactly one worker task instead of being encoded inline on whichever thread
    // captured it. Bounded to 2 with DropOldest: if the single worker falls behind (a slow encode,
    // a GC pause), the newest frame always wins over backlog -- catching up on stale frames is
    // pointless for a live view and would only add latency.
    private Channel<VideoFrameItem>? _videoQueue;
    private Task? _videoWorkerTask;

    private volatile bool _isConnected;
    private bool _isDisposed;

    private readonly record struct VideoFrameItem(
        byte[] PixelBytes, int Width, int Height, VideoPixelFormatsEnum PixelFormat, TimeSpan CaptureTimestamp);

    public event Action<RTCPeerConnectionState>? OnConnectionStateChanged;

    public bool IsConnected => _isConnected;

    public MonitorStreamClient(
        string streamingBaseUrl, string scheduleId, RecordingStreamType streamType, string token, string origin,
        int videoFps, int videoBitrate)
    {
        var wireStreamType = streamType == RecordingStreamType.Camera ? "camera" : "screen";
        var wsBase = ToWebSocketBase(streamingBaseUrl);
        _wsUrl = $"{wsBase}/ws/stream?scheduleId={Uri.EscapeDataString(scheduleId)}" +
                 $"&streamType={Uri.EscapeDataString(wireStreamType)}&token={Uri.EscapeDataString(token)}";
        _videoFps = Math.Clamp(videoFps, 1, 60);
        _videoBitrate = videoBitrate;
        // Seeds only the first frame's RTP duration (see VideoEncodeWorkerAsync); real frames
        // compute theirs from actual captured timestamps instead.
        _firstFrameVideoDuration = (uint)(VideoClockRate / _videoFps);
        _origin = origin;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _videoEncoder = new MediaFoundationH264Encoder(_videoFps, _videoBitrate);
        _audioEncoder = new OpusAudioEncoder();
        _pc = new RTCPeerConnection(null);

        _videoQueue = Channel.CreateBounded<VideoFrameItem>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _videoWorkerTask = Task.Run(() => VideoEncodeWorkerAsync(_cts.Token));

        _pc.onconnectionstatechange += state =>
        {
            _isConnected = state == RTCPeerConnectionState.connected;
            OnConnectionStateChanged?.Invoke(state);
        };

        var videoFormat = new SDPAudioVideoMediaFormat(
            SDPMediaTypesEnum.video,
            102,
            "H264/90000",
            "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f");
        var videoTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.video, false, [videoFormat], MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(videoTrack);

        var audioFormat = new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 111, "opus/48000/2");
        var audioTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.audio, false, [audioFormat], MediaStreamStatusEnum.SendOnly);
        _pc.addTrack(audioTrack);

        _pc.onicecandidate += candidate =>
        {
            if (candidate is null)
            {
                return;
            }

            _ = SendSignalAsync(new SignalMessage
            {
                Type = "ice-candidate",
                Candidate = new IceCandidatePayload
                {
                    Candidate = candidate.candidate,
                    SdpMid = candidate.sdpMid,
                    SdpMLineIndex = candidate.sdpMLineIndex,
                    UsernameFragment = candidate.usernameFragment
                }
            });
        };

        _ws = new ClientWebSocket();
        // vox-streaming's /ws/stream upgrader 403s the handshake unless Origin matches its own
        // ALLOWED_ORIGINS -- ClientWebSocket sends no Origin header on its own, unlike a browser.
        _ws.Options.SetRequestHeader("Origin", _origin);
        await _ws.ConnectAsync(new Uri(_wsUrl), _cts.Token);

        _receiveLoopTask = ReceiveLoopAsync(_cts.Token);

        var offer = _pc.createOffer();
        await _pc.setLocalDescription(offer);
        await SendSignalAsync(new SignalMessage { Type = "offer", Sdp = offer.sdp });
    }

    /// <summary>
    /// Queues one raw video frame for encode + RTP send on the single dedicated video worker.
    /// Silently dropped until connected.
    ///
    /// captureTimestamp must be the real time the frame was captured, on a clock that is monotonic
    /// and shared across this stream's frames (e.g. RecordingClock.Elapsed at capture):
    /// VideoEncodeWorkerAsync maps it directly onto the outgoing RTP clock, so it -- not the
    /// nominal capture rate, and not when the frame happens to reach the encoder -- is what decides
    /// the video's playback timing and its sync against the audio track.
    /// </summary>
    public void PushVideoFrame(
        byte[] pixelBytes, int width, int height, VideoPixelFormatsEnum pixelFormat, TimeSpan captureTimestamp)
    {
        if (_videoQueue is null || !_isConnected || _isDisposed)
        {
            return;
        }

        // TryWrite never blocks/fails here: the channel is bounded with DropOldest, so a full
        // queue just silently evicts the stale frame in favor of this newer one.
        _videoQueue.Writer.TryWrite(new VideoFrameItem(pixelBytes, width, height, pixelFormat, captureTimestamp));
    }

    // The single consumer of _videoQueue -- see the field's own comment for why encode/send must
    // never run concurrently from more than one caller.
    private async Task VideoEncodeWorkerAsync(CancellationToken ct)
    {
        // Absolute position on the 90kHz RTP clock assigned to the previous frame. Each frame's
        // duration is the DIFFERENCE between two absolute positions, never an independently
        // estimated per-frame interval, and this is what keeps the video timeline honest:
        //
        //  * Successive differences telescope, so total RTP advance is always exactly
        //    position(latest) - position(first). No matter how many frames the bounded queue
        //    dropped or how long an encode stalled, the RTP clock lands where the capture clock
        //    says it should -- errors cannot accumulate the way summing per-frame estimates lets
        //    them. The previous version capped each frame's contribution at 1 second, so every
        //    stall longer than that permanently deleted the excess from the video timeline while
        //    the audio timeline (paced by real captured PCM, RFC 7587 48kHz) kept perfect time --
        //    measured as video running ~1.5-2% short, i.e. minutes of A/V desync over an exam,
        //    plus a recording whose playback duration was shorter than the exam itself.
        //  * Rounding once per absolute position rather than per interval means the rounding error
        //    cancels between frames instead of being shed downward on every single one (truncating
        //    a ~33ms interval loses up to a tick each time: ~2s over 216k frames).
        //
        // A genuinely long gap now produces a genuinely long RTP jump, which is the truth: that
        // time really did pass with no new picture, and a player simply holds the last frame.
        long? prevRtpPosition = null;
        try
        {
            await foreach (var item in _videoQueue!.Reader.ReadAllAsync(ct))
            {
                if (_videoEncoder is null || _pc is null)
                {
                    continue;
                }

                var rtpPosition = (long)Math.Round(item.CaptureTimestamp.TotalSeconds * VideoClockRate);
                uint durationRtpUnits;
                if (prevRtpPosition is { } prev)
                {
                    // Math.Max keeps the clock strictly monotonic if two frames ever share a
                    // capture timestamp or arrive out of order (ScreenCaptureSource's keep-alive
                    // timer and its real capture callback can both fire -- see its own comments).
                    // Advancing a single tick, and carrying that forward as the new position, keeps
                    // the mapping exact rather than inventing an interval that didn't happen.
                    var position = Math.Max(rtpPosition, prev + 1);
                    durationRtpUnits = (uint)(position - prev);
                    prevRtpPosition = position;
                }
                else
                {
                    durationRtpUnits = _firstFrameVideoDuration;
                    prevRtpPosition = rtpPosition;
                }

                try
                {
                    var encoded = _videoEncoder.Encode(
                        item.PixelBytes, item.Width, item.Height, item.PixelFormat, item.CaptureTimestamp);
                    if (encoded is { Length: > 0 })
                    {
                        _pc.SendVideo(durationRtpUnits, encoded);
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LocalFileLogger.Error("monitor_stream", "video_encode_failed", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Feeds raw PCM16 mono audio for encode + RTP send. Silently dropped until connected.</summary>
    public void PushAudioPcm(byte[] pcm16Mono)
    {
        if (_pc is null || _audioEncoder is null || !_isConnected || _isDisposed)
        {
            return;
        }

        try
        {
            foreach (var frame in _audioEncoder.Encode(pcm16Mono))
            {
                _pc.SendAudio(AudioDurationPerFrame, frame);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("monitor_stream", "audio_encode_failed", ex);
        }
    }

    private async Task SendSignalAsync(SignalMessage message)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, SignalJsonOptions));

        await _sendGate.WaitAsync();
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("monitor_stream", "signal_send_failed", ex);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var messageStream = new MemoryStream();

        try
        {
            while (_ws is { State: WebSocketState.Open } ws && !ct.IsCancellationRequested)
            {
                messageStream.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    messageStream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                SignalMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<SignalMessage>(messageStream.ToArray(), SignalJsonOptions);
                }
                catch (JsonException ex)
                {
                    LocalFileLogger.Error("monitor_stream", "signal_parse_failed", ex);
                    continue;
                }

                if (message is not null)
                {
                    HandleSignalMessage(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("monitor_stream", "signal_receive_failed", ex);
        }
    }

    private void HandleSignalMessage(SignalMessage message)
    {
        switch (message.Type)
        {
            case "answer":
                if (message.Sdp is not null && _pc is not null)
                {
                    _pc.setRemoteDescription(new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.answer,
                        sdp = message.Sdp
                    });
                }

                break;

            case "ice-candidate":
                if (message.Candidate is not null && _pc is not null)
                {
                    _pc.addIceCandidate(new RTCIceCandidateInit
                    {
                        candidate = message.Candidate.Candidate,
                        sdpMid = message.Candidate.SdpMid,
                        sdpMLineIndex = message.Candidate.SdpMLineIndex ?? 0,
                        usernameFragment = message.Candidate.UsernameFragment
                    });
                }

                break;

            case "error":
                LocalFileLogger.Error(
                    "monitor_stream",
                    "server_error",
                    new InvalidOperationException(message.Message ?? "unknown server error"));
                break;
        }
    }

    private static string ToWebSocketBase(string httpBaseUrl)
    {
        if (httpBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "wss://" + httpBaseUrl["https://".Length..].TrimEnd('/');
        }

        if (httpBaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return "ws://" + httpBaseUrl["http://".Length..].TrimEnd('/');
        }

        return httpBaseUrl.TrimEnd('/');
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _isConnected = false;

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_ws is { State: WebSocketState.Open })
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client stop", CancellationToken.None);
            }
            catch
            {
            }
        }

        if (_receiveLoopTask is not null)
        {
            try
            {
                await _receiveLoopTask;
            }
            catch
            {
            }
        }

        // Must finish (it exits on its own once _cts.Cancel() above cancels its ReadAllAsync)
        // before _pc/_videoEncoder are torn down below -- it's the only thing that touches them.
        if (_videoWorkerTask is not null)
        {
            try
            {
                await _videoWorkerTask;
            }
            catch
            {
            }
        }

        _pc?.close();
        _pc = null;
        _videoEncoder?.Dispose();
        _videoEncoder = null;
        _audioEncoder?.Dispose();
        _audioEncoder = null;
        _ws?.Dispose();
        _ws = null;
        _cts?.Dispose();
        _cts = null;
        _sendGate.Dispose();
    }

    private sealed class SignalMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("sdp")]
        public string? Sdp { get; set; }

        [JsonPropertyName("candidate")]
        public IceCandidatePayload? Candidate { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private sealed class IceCandidatePayload
    {
        [JsonPropertyName("candidate")]
        public string? Candidate { get; set; }

        [JsonPropertyName("sdpMid")]
        public string? SdpMid { get; set; }

        [JsonPropertyName("sdpMLineIndex")]
        public ushort? SdpMLineIndex { get; set; }

        [JsonPropertyName("usernameFragment")]
        public string? UsernameFragment { get; set; }
    }
}
