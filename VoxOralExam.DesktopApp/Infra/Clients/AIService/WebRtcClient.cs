using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Vpx.Net;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Infra.Clients.AIService;

/// <summary>
/// WebRTC client that sends webcam frames to the Python aiortc server.
/// </summary>
public class WebRtcClient : IDisposable
{
    private readonly string _pythonBaseUrl;
    private readonly string _stunUrls;
    private readonly string _turnUrl;
    private readonly string _turnUsername;
    private readonly string _turnCredential;
    private readonly HttpClient _http;
    private RTCPeerConnection? _peerConnection;
    private VP8Codec? _vp8Encoder;
    private CancellationTokenSource? _cts;
    private string? _sessionId;
    private bool _isConnected;
    private int _videoPayloadType = 96;
    private bool _isDisposed;

    private const int RTP_CLOCK_RATE = 90000;
    private const int FPS = 15;
    private const uint DURATION_PER_FRAME = (uint)(RTP_CLOCK_RATE / FPS);

    public event Action<RTCPeerConnectionState>? OnConnectionStateChanged;
    public event Action<string>? OnProctoringEvent;
    public bool IsConnected => _isConnected;
    public string? SessionId => _sessionId;

    public WebRtcClient(IHttpClientFactory httpClientFactory, string pythonBaseUrl, AppSettings settings)
    {
        _http = httpClientFactory.CreateClient("WebRtcClient");
        _pythonBaseUrl = pythonBaseUrl.TrimEnd('/');
        _stunUrls = settings.StunUrls;
        _turnUrl = settings.TurnUrl;
        _turnUsername = settings.TurnUsername;
        _turnCredential = settings.TurnCredential;
    }

    public async Task ConnectAsync(string examAttemptId)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(WebRtcClient));
        }

        _cts = new CancellationTokenSource();
        _vp8Encoder = new VP8Codec();
        _peerConnection = new RTCPeerConnection(BuildRtcConfiguration());

        _peerConnection.onconnectionstatechange += state =>
        {
            _isConnected = state == RTCPeerConnectionState.connected;
            System.Diagnostics.Debug.WriteLine($"[WebRTC] Connection state: {state}");
            OnConnectionStateChanged?.Invoke(state);
        };

        var videoCapabilities = new List<SDPAudioVideoMediaFormat>
        {
            new(SDPMediaTypesEnum.video, 96, "VP8/90000")
        };
        var videoTrack = new MediaStreamTrack(
            SDPMediaTypesEnum.video,
            false,
            videoCapabilities,
            MediaStreamStatusEnum.SendOnly);
        _peerConnection.addTrack(videoTrack);

        _peerConnection.OnVideoFormatsNegotiated += formats =>
        {
            var fmt = formats.First();
            _videoPayloadType = fmt.FormatID;
            System.Diagnostics.Debug.WriteLine($"[WebRTC] Video format negotiated: {fmt.FormatName} PT={fmt.FormatID}");
        };

        var offer = _peerConnection.createOffer();
        await _peerConnection.setLocalDescription(offer);

        // createOffer()'s returned offer.sdp is a SNAPSHOT taken before ICE gathering runs --
        // it only ever contains the host candidate. STUN/TURN candidates resolve asynchronously
        // AFTER setLocalDescription (that's what triggers gathering), so sending offer.sdp here
        // (as the old code did) meant the remote peer only ever learned about our raw LAN IP,
        // never a reachable STUN/TURN candidate -- confirmed for real: agents-side logs always
        // showed the remote candidate as a private 192.168.x.x address, even once agents' own
        // TURN allocation was working correctly. Must wait for iceGatheringState == complete,
        // then read the up-to-date SDP off localDescription (not the stale offer.sdp).
        await WaitForIceGatheringCompleteAsync();
        var fullOfferSdp = _peerConnection.localDescription.sdp.ToString();
        System.Diagnostics.Debug.WriteLine($"[WebRTC] SDP Offer (post-gathering):\n{fullOfferSdp}");

        var (sessionId, answerSdp) = await PostOfferAsync(fullOfferSdp, offer.type.ToString(), examAttemptId);
        _sessionId = sessionId;

        var answerInit = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = answerSdp
        };
        _peerConnection.setRemoteDescription(answerInit);

        _ = ListenSseAsync(_cts.Token);
    }

    // Timeout is a safety net, not the expected path -- normal gathering (STUN/TURN both
    // reachable) completes in well under 1s. If it doesn't complete, proceed with whatever
    // candidates gathered so far rather than hanging the exam flow forever.
    private static readonly TimeSpan IceGatheringTimeout = TimeSpan.FromSeconds(5);

    private async Task WaitForIceGatheringCompleteAsync()
    {
        if (_peerConnection == null || _peerConnection.iceGatheringState == RTCIceGatheringState.complete)
        {
            return;
        }

        var tcs = new TaskCompletionSource();
        void OnGatheringStateChange(RTCIceGatheringState state)
        {
            if (state == RTCIceGatheringState.complete)
            {
                tcs.TrySetResult();
            }
        }

        _peerConnection.onicegatheringstatechange += OnGatheringStateChange;
        try
        {
            // Only unsubscribe once the wait is actually over (either gathering genuinely
            // completed, or the timeout fired) -- unsubscribing any earlier would remove the
            // handler before it ever gets a chance to fire.
            await Task.WhenAny(tcs.Task, Task.Delay(IceGatheringTimeout));
        }
        finally
        {
            _peerConnection.onicegatheringstatechange -= OnGatheringStateChange;
        }
    }

    private RTCConfiguration BuildRtcConfiguration()
    {
        var iceServers = new List<RTCIceServer>();
        foreach (var url in _stunUrls.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmedUrl = url.Trim();
            if (trimmedUrl.Length > 0)
            {
                iceServers.Add(new RTCIceServer { urls = trimmedUrl });
            }
        }

        if (iceServers.Count == 0)
        {
            iceServers.Add(new RTCIceServer { urls = "stun:stun.l.google.com:19302" });
        }

        var turnUrl = _turnUrl.Trim();
        if (!string.IsNullOrEmpty(turnUrl))
        {
            iceServers.Add(new RTCIceServer
            {
                urls = turnUrl,
                username = _turnUsername,
                credential = _turnCredential
            });
        }

        return new RTCConfiguration { iceServers = iceServers };
    }

    private int _frameCount;

    public void PushRawFrame(byte[] bgrBytes, int width, int height)
    {
        if (_peerConnection == null || _vp8Encoder == null || !_isConnected || _isDisposed)
        {
            return;
        }

        try
        {
            if (_frameCount == 0)
            {
                var allZero = bgrBytes.All(b => b == 0);
                System.Diagnostics.Debug.WriteLine(
                    $"[WebRTC] First raw frame: {width}x{height}, sample: {bgrBytes[0]},{bgrBytes[1]},{bgrBytes[2]}, allZero={allZero}, len={bgrBytes.Length}");
                _vp8Encoder.ForceKeyFrame();
            }

            using var bgrMat = new Mat(height, width, MatType.CV_8UC3);
            System.Runtime.InteropServices.Marshal.Copy(bgrBytes, 0, bgrMat.Data, bgrBytes.Length);
            using var i420Mat = new Mat();
            Cv2.CvtColor(bgrMat, i420Mat, ColorConversionCodes.BGR2YUV_I420);
            var i420Bytes = new byte[i420Mat.Rows * i420Mat.Cols];
            System.Runtime.InteropServices.Marshal.Copy(i420Mat.Data, i420Bytes, 0, i420Bytes.Length);

            var encodedSample = _vp8Encoder.EncodeVideo(
                width,
                height,
                i420Bytes,
                VideoPixelFormatsEnum.I420,
                VideoCodecsEnum.VP8);

            if (encodedSample == null || encodedSample.Length == 0)
            {
                return;
            }

            if (_frameCount == 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[WebRTC] First encoded: {encodedSample.Length} bytes, first4: {encodedSample[0]},{encodedSample[1]},{encodedSample[2]},{encodedSample[3]}");
            }

            _peerConnection.SendVideo(DURATION_PER_FRAME, encodedSample);
            _frameCount++;
            if (_frameCount % 15 == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[WebRTC] Sent {_frameCount} frames, last encoded: {encodedSample.Length} bytes");
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebRTC] Encode error: {ex.Message}");
        }
    }

    private async Task<(string sessionId, string sdp)> PostOfferAsync(string sdp, string type, string examAttemptId)
    {
        var body = JsonSerializer.Serialize(new { sdp, type, exam_attempt_id = examAttemptId });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        System.Diagnostics.Debug.WriteLine($"[WebRTC] POST {_pythonBaseUrl}/webrtc/offer ...");
        using var response = await _http.PostAsync($"{_pythonBaseUrl}/webrtc/offer", content);
        System.Diagnostics.Debug.WriteLine($"[WebRTC] Response: {response.StatusCode}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var sessionId = root.GetProperty("session_id").GetString()
            ?? throw new Exception("Python did not return session_id.");
        var answerSdp = root.GetProperty("sdp").GetString()
            ?? throw new Exception("Python did not return sdp.");

        return (sessionId, answerSdp);
    }

    private async Task ListenSseAsync(CancellationToken ct)
    {
        if (_sessionId == null)
        {
            return;
        }

        try
        {
            var url = $"{_pythonBaseUrl}/webrtc/connections/{_sessionId}/events/stream";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "text/event-stream");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null)
                {
                    break;
                }

                if (line.StartsWith("data: "))
                {
                    var data = line[6..];
                    if (!string.IsNullOrWhiteSpace(data))
                    {
                        OnProctoringEvent?.Invoke(data);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SSE Error] {ex.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isConnected = false;
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _peerConnection?.close();
        _peerConnection = null;
        _vp8Encoder?.Dispose();
        _vp8Encoder = null;
        _sessionId = null;
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _ = DisconnectAsync();
        _cts?.Dispose();
        _cts = null;
        _isDisposed = true;
    }
}

