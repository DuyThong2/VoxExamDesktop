using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Vpx.Net;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Infra.Clients.AIService;

/// <summary>
/// WebRTC client that sends webcam frames to the Python aiortc server for YOLO proctoring.
///
/// <para>RECONNECT. This connection used to be one-shot in the same way MonitorStreamClient was:
/// onconnectionstatechange raised an event nobody subscribed to, and ListenSseAsync swallowed every
/// failure into Debug.WriteLine -- which produces nothing whatsoever in a release build. So a blip
/// on the exam machine's network ended cheating detection for the rest of the exam with no reconnect,
/// no alert, no UI change, and no line in desktopapp.jsonl. The exam looked completely normal while
/// the detector had been offline since minute three. That silence was the worst part of it, and it
/// is why every failure path below now writes to LocalFileLogger.</para>
///
/// <para>Rebuilding is CHEAP here, unlike the live monitor stream. Python keys its proctoring
/// session on exam_attempt_id (controller/webrtc.py), not on a per-connection id, so a rebuilt
/// connection lands back on the SAME session: the SSE URL is unchanged, the event log survives, and
/// the alert policy's streak/cooldown state is preserved rather than reset. There is no equivalent
/// of the monitor stream's "a rebuild mints a new stream id" cost, so this needs no ICE-restart fast
/// path -- and could not have one anyway, since /webrtc/offer builds a brand new RTCPeerConnection
/// per offer and has no renegotiation route.</para>
///
/// <para>The server half of this is in controller/webrtc.py: a reconnect evicts the previous peer
/// before registering the new one, and the peer state handler is identity-checked so a superseded
/// peer's death cannot tear down the connection that replaced it. Without that guard the reconnect
/// here would be actively harmful -- the exam machine notices the outage first and reconnects within
/// a second or two, while aiortc only finds out when ICE consent expires, so the old peer's cleanup
/// would land squarely on the new session and the two sides would loop.</para>
/// </summary>
public class WebRtcClient : IDisposable
{
    private readonly string _pythonBaseUrl;
    private readonly string _stunUrls;
    private readonly string _turnUrl;
    private readonly string _turnUsername;
    private readonly string _turnCredential;
    private readonly HttpClient _http;

    /// <summary>
    /// Cancelled once, by DisconnectAsync/Dispose. Every session's own token links to it.
    /// </summary>
    private CancellationTokenSource _lifetimeCts = new();

    /// <summary>
    /// The transport as one replaceable unit -- see MonitorStreamClient.Session for the reasoning.
    /// PushRawFrame reads this ONCE into a local so a rebuild cannot pair a live peer connection
    /// with an encoder that is being disposed underneath it.
    /// </summary>
    private volatile Session? _session;

    private Task? _supervisorTask;
    private string _examAttemptId = "";
    private int _reconnectAttempt;
    private volatile bool _isStopping;
    private bool _isDisposed;
    /// <summary>0 until a Dispose call claims the teardown; see Dispose for why it is not _isDisposed.</summary>
    private int _disposeStarted;

    private const int RTP_CLOCK_RATE = 90000;
    private const int FPS = 15;
    private const uint DURATION_PER_FRAME = (uint)(RTP_CLOCK_RATE / FPS);

    /// <summary>Same schedule as the realtime socket and the monitor stream, for the same reason:
    /// one outage should read as one event across all three logs.</summary>
    private static readonly TimeSpan[] ReconnectBackoff =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)
    ];

    private static readonly TimeSpan LongOutageRetryInterval = TimeSpan.FromSeconds(20);

    /// <summary>A session that lasted this long counts as a discrete outage rather than a flap, and
    /// lets the next one start from the fast attempts again.</summary>
    private static readonly TimeSpan StableConnectionThreshold = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Grace before a <c>disconnected</c> peer is rebuilt.
    ///
    /// <para>Deliberately short, and it does NOT wait to see whether ICE heals: Python tears its
    /// session down the moment aiortc reports disconnected (there is no grace window on that side),
    /// so a client-side peer that quietly recovers is very likely talking to a session that no
    /// longer exists -- frames encoded, sent, and detected by nobody. That is the original silent
    /// failure wearing a different hat. Rebuilding an unnecessary session costs one HTTP round trip;
    /// trusting a half-healed one costs the rest of the exam's proctoring.</para>
    /// </summary>
    private static readonly TimeSpan DisconnectGrace = TimeSpan.FromSeconds(3);

    public event Action<RTCPeerConnectionState>? OnConnectionStateChanged;
    public event Action<string>? OnProctoringEvent;

    /// <summary>Fired when an established session died and the rebuild loop started.</summary>
    public event Action? OnReconnecting;

    /// <summary>Fired when a rebuild succeeded, carrying the attempt count for the log.</summary>
    public event Action<int>? OnReconnected;

    public bool IsConnected => _session?.Connected == true;
    public string? SessionId => _session?.SessionId;

    public WebRtcClient(IHttpClientFactory httpClientFactory, string pythonBaseUrl, AppSettings settings)
    {
        _http = httpClientFactory.CreateClient("WebRtcClient");
        _pythonBaseUrl = pythonBaseUrl.TrimEnd('/');
        _stunUrls = settings.StunUrls;
        _turnUrl = settings.TurnUrl;
        _turnUsername = settings.TurnUsername;
        _turnCredential = settings.TurnCredential;
    }

    /// <summary>
    /// Starts the proctoring transport and arms the supervisor.
    ///
    /// <para>A failed first attempt is no longer terminal. It used to throw, and
    /// ExamAttemptRunner.StartProctoringAsync would log proctoring_start_failed and then run the
    /// ENTIRE exam with no AI detection at all -- while RebuildAsync, twenty lines below, was
    /// already willing to retry a dropped feed for as long as the exam lasted. The student ended up
    /// unobserved for the whole session because of one badly-timed first packet.</para>
    ///
    /// <para>The consequence is worse here than for the live monitor stream: a missing camera feed
    /// is visible to a proctor watching the grid, whereas a detector that never started looks
    /// exactly like a detector that found nothing.</para>
    /// </summary>
    public async Task ConnectAsync(string examAttemptId)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(WebRtcClient));
        }

        _examAttemptId = examAttemptId;
        _isStopping = false;
        _reconnectAttempt = 0;
        if (_lifetimeCts.IsCancellationRequested)
        {
            // Reused after a DisconnectAsync (the proctoring service can be stopped and started
            // again within one app run); a cancelled token source would make every session below
            // abort the instant it was created.
            _lifetimeCts.Dispose();
            _lifetimeCts = new CancellationTokenSource();
        }

        try
        {
            _session = await OpenSessionAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Error level, matching proctoring_feed_lost_rebuilding: for as long as this takes the
            // student is unobserved, and that has to leave a trace someone reviewing the exam can
            // find. Previously this path logged from the caller and then went quiet forever.
            LocalFileLogger.Error("proctoring_webrtc", "first_connect_failed_retrying", ex, new
            {
                examAttemptId
            });
            OnReconnecting?.Invoke();
        }

        _supervisorTask = Task.Run(RunSupervisorAsync);
    }

    /// <summary>
    /// Builds one complete session -- peer connection, encoder, SDP exchange, SSE listener -- or
    /// throws having disposed whatever it managed to create.
    /// </summary>
    private async Task<Session> OpenSessionAsync(CancellationToken ct)
    {
        var session = new Session(CancellationTokenSource.CreateLinkedTokenSource(ct));
        try
        {
            session.Vp8Encoder = new VP8Codec();
            session.PeerConnection = new RTCPeerConnection(BuildRtcConfiguration());

            session.PeerConnection.onconnectionstatechange +=
                state => HandleConnectionStateChanged(session, state);

            var videoCapabilities = new List<SDPAudioVideoMediaFormat>
            {
                new(SDPMediaTypesEnum.video, 96, "VP8/90000")
            };
            var videoTrack = new MediaStreamTrack(
                SDPMediaTypesEnum.video,
                false,
                videoCapabilities,
                MediaStreamStatusEnum.SendOnly);
            session.PeerConnection.addTrack(videoTrack);

            session.PeerConnection.OnVideoFormatsNegotiated += formats =>
            {
                var format = formats.First();
                LocalFileLogger.Info("proctoring_webrtc", "video_format_negotiated", new
                {
                    format = format.FormatName,
                    payloadType = format.FormatID
                });
            };

            var offer = session.PeerConnection.createOffer();
            await session.PeerConnection.setLocalDescription(offer);

            // createOffer()'s returned offer.sdp is a SNAPSHOT taken before ICE gathering runs --
            // it only ever contains the host candidate. STUN/TURN candidates resolve asynchronously
            // AFTER setLocalDescription (that's what triggers gathering), so sending offer.sdp here
            // (as the old code did) meant the remote peer only ever learned about our raw LAN IP,
            // never a reachable STUN/TURN candidate -- confirmed for real: agents-side logs always
            // showed the remote candidate as a private 192.168.x.x address, even once agents' own
            // TURN allocation was working correctly. Must wait for iceGatheringState == complete,
            // then read the up-to-date SDP off localDescription (not the stale offer.sdp).
            await WaitForIceGatheringCompleteAsync(session.PeerConnection);
            var fullOfferSdp = session.PeerConnection.localDescription.sdp.ToString();

            var (sessionId, answerSdp) = await PostOfferAsync(
                fullOfferSdp, offer.type.ToString(), _examAttemptId, ct);
            session.SessionId = sessionId;

            session.PeerConnection.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = answerSdp
            });

            session.SseTask = ListenSseAsync(session, session.Cts.Token);

            LocalFileLogger.Info("proctoring_webrtc", "session_opened", new { sessionId });
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Rebuilds the proctoring connection whenever a working one stops working. Idle for the whole
    /// exam in the normal case -- it only ever reacts to a session ending.
    /// </summary>
    /// <summary>
    /// Wrapper whose only job is to make sure this loop can never die in silence.
    ///
    /// <para>It runs as a bare Task.Run with nothing awaiting it, so before this an exception
    /// escaping the loop faulted the task and was observed by nobody -- AI proctoring simply stopped
    /// forever, with no log line to say why. Seen live on 2026-09-02: the feed failed 16 seconds
    /// into a 4-minute exam and the log contains neither proctoring_feed_restored nor
    /// proctoring_rebuild_failed after it, just silence.</para>
    /// </summary>
    private async Task RunSupervisorAsync()
    {
        try
        {
            await SuperviseAsync();
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("proctoring_webrtc", "supervisor_faulted", ex, new
            {
                _examAttemptId
            });
        }
    }

    private async Task SuperviseAsync()
    {
        while (!_isStopping && !_lifetimeCts.IsCancellationRequested)
        {
            var current = _session;
            if (current is null)
            {
                // Nothing to watch yet: either the first connect failed (see ConnectAsync) or a
                // rebuild has just cleared the field. Both want the same thing, and RebuildAsync
                // already retries for as long as the exam lasts.
                if (!await RebuildAsync())
                {
                    return;
                }
                continue;
            }

            try
            {
                await current.Ended.Task.WaitAsync(_lifetimeCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_isStopping || _lifetimeCts.IsCancellationRequested)
            {
                return;
            }

            if (current.WasStable(StableConnectionThreshold))
            {
                _reconnectAttempt = 0;
            }

            // Loud, and at Error level despite being recoverable: for however long this takes, the
            // student is unobserved by the detector. That is a fact someone reviewing the exam
            // afterwards has to be able to find, and until now it left no trace at all.
            LocalFileLogger.Error(
                "proctoring_webrtc",
                "proctoring_feed_lost_rebuilding",
                new InvalidOperationException("AI proctoring feed dropped; rebuilding."),
                new
                {
                    sessionId = current.SessionId,
                    reason = current.EndReason,
                    connectedSeconds = current.ConnectedSeconds()
                });
            OnReconnecting?.Invoke();

            // Guarded, because a throw here used to end AI proctoring for the rest of the exam. This
            // await is the last thing between a failed feed and the rebuild that would restore it,
            // and Session.DisposeAsync reaches into SIPSorcery (PeerConnection.close) and an encoder
            // on a peer that has just failed -- exactly where an exception is plausible. Tearing the
            // old session down is bookkeeping; the rebuild is the point, so nothing here is allowed
            // to stop us reaching it.
            try
            {
                await current.DisposeAsync();
            }
            catch (Exception ex)
            {
                LocalFileLogger.Error("proctoring_webrtc", "session_dispose_failed", ex, new
                {
                    sessionId = current.SessionId
                });
            }

            _session = null;

            if (!await RebuildAsync())
            {
                return;
            }
        }
    }

    private async Task<bool> RebuildAsync()
    {
        while (!_isStopping && !_lifetimeCts.IsCancellationRequested)
        {
            var delay = _reconnectAttempt < ReconnectBackoff.Length
                ? ReconnectBackoff[_reconnectAttempt]
                : LongOutageRetryInterval;
            _reconnectAttempt++;

            try
            {
                await Task.Delay(delay, _lifetimeCts.Token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (_isStopping || _lifetimeCts.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                var session = await OpenSessionAsync(_lifetimeCts.Token);
                _session = session;
                LocalFileLogger.Info("proctoring_webrtc", "proctoring_feed_restored", new
                {
                    sessionId = session.SessionId,
                    attempt = _reconnectAttempt
                });
                OnReconnected?.Invoke(_reconnectAttempt);
                return true;
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                LocalFileLogger.Error("proctoring_webrtc", "proctoring_rebuild_failed", ex, new
                {
                    attempt = _reconnectAttempt,
                    delaySeconds = delay.TotalSeconds
                });
            }
        }

        return false;
    }

    private void HandleConnectionStateChanged(Session session, RTCPeerConnectionState state)
    {
        session.Connected = state == RTCPeerConnectionState.connected;
        LocalFileLogger.Info("proctoring_webrtc", "peer_state_changed", new
        {
            sessionId = session.SessionId,
            state = state.ToString()
        });
        OnConnectionStateChanged?.Invoke(state);

        switch (state)
        {
            case RTCPeerConnectionState.connected:
                session.MarkConnected();
                break;

            case RTCPeerConnectionState.disconnected:
                session.ArmDisconnectTimer(DisconnectGrace);
                break;

            case RTCPeerConnectionState.failed:
            case RTCPeerConnectionState.closed:
                session.End(state.ToString());
                break;
        }
    }

    // Timeout is a safety net, not the expected path -- normal gathering (STUN/TURN both
    // reachable) completes in well under 1s. If it doesn't complete, proceed with whatever
    // candidates gathered so far rather than hanging the exam flow forever.
    private static readonly TimeSpan IceGatheringTimeout = TimeSpan.FromSeconds(5);

    private static async Task WaitForIceGatheringCompleteAsync(RTCPeerConnection peerConnection)
    {
        if (peerConnection.iceGatheringState == RTCIceGatheringState.complete)
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

        peerConnection.onicegatheringstatechange += OnGatheringStateChange;
        try
        {
            // Only unsubscribe once the wait is actually over (either gathering genuinely
            // completed, or the timeout fired) -- unsubscribing any earlier would remove the
            // handler before it ever gets a chance to fire.
            await Task.WhenAny(tcs.Task, Task.Delay(IceGatheringTimeout));
        }
        finally
        {
            peerConnection.onicegatheringstatechange -= OnGatheringStateChange;
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

    public void PushRawFrame(byte[] bgrBytes, int width, int height)
    {
        // One read of the session, then nothing else touched -- see the field's comment.
        var session = _session;
        if (session?.PeerConnection is null || session.Vp8Encoder is null
            || !session.Connected || _isDisposed)
        {
            return;
        }

        try
        {
            if (session.FrameCount == 0)
            {
                // Forced on the first frame of EVERY session, rebuilds included: a fresh peer has
                // no reference frame, so without this the server decodes nothing until VP8 happens
                // to emit a keyframe on its own -- a reconnect that looks connected and detects
                // nothing.
                session.Vp8Encoder.ForceKeyFrame();
            }

            using var bgrMat = new Mat(height, width, MatType.CV_8UC3);
            System.Runtime.InteropServices.Marshal.Copy(bgrBytes, 0, bgrMat.Data, bgrBytes.Length);
            using var i420Mat = new Mat();
            Cv2.CvtColor(bgrMat, i420Mat, ColorConversionCodes.BGR2YUV_I420);
            var i420Bytes = new byte[i420Mat.Rows * i420Mat.Cols];
            System.Runtime.InteropServices.Marshal.Copy(i420Mat.Data, i420Bytes, 0, i420Bytes.Length);

            var encodedSample = session.Vp8Encoder.EncodeVideo(
                width,
                height,
                i420Bytes,
                VideoPixelFormatsEnum.I420,
                VideoCodecsEnum.VP8);

            if (encodedSample == null || encodedSample.Length == 0)
            {
                return;
            }

            session.PeerConnection.SendVideo(DURATION_PER_FRAME, encodedSample);
            session.FrameCount++;
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            // Was Debug.WriteLine, i.e. nothing at all in a release build. A camera that encodes
            // badly for an entire exam is the kind of thing that has to be visible afterwards, so
            // it is logged -- but only once per session, since this runs at the camera's frame rate
            // and an unconditional write here would bury the log it is meant to help.
            if (!session.EncodeErrorLogged)
            {
                session.EncodeErrorLogged = true;
                LocalFileLogger.Error("proctoring_webrtc", "frame_encode_failed", ex, new
                {
                    sessionId = session.SessionId,
                    width,
                    height
                });
            }
        }
    }

    private async Task<(string sessionId, string sdp)> PostOfferAsync(
        string sdp, string type, string examAttemptId, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { sdp, type, exam_attempt_id = examAttemptId });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await _http.PostAsync($"{_pythonBaseUrl}/webrtc/offer", content, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var sessionId = root.GetProperty("session_id").GetString()
            ?? throw new InvalidOperationException("Python did not return session_id.");
        var answerSdp = root.GetProperty("sdp").GetString()
            ?? throw new InvalidOperationException("Python did not return sdp.");

        return (sessionId, answerSdp);
    }

    /// <summary>
    /// Streams proctoring events back from Python, retrying for as long as this session lives.
    ///
    /// <para>The retry is not cosmetic. This stream is also the only signal the client gets that
    /// PYTHON has torn the session down: cleanup_session pushes SESSION_ENDED and ends the
    /// generator. Since aiortc destroys its session the instant it sees `disconnected`, while a
    /// SIPSorcery peer on this side can quietly recover from the same blip, there is a real state
    /// in which this client believes it is connected and is encoding frames into a session that no
    /// longer exists. SESSION_ENDED is what catches that, which is why it ends the session here
    /// rather than merely being logged.</para>
    ///
    /// <para>A 404 is expected rather than exceptional: after Python cleans up, /events/stream
    /// rejects the id until a new offer re-registers it, so an SSE attempt can legitimately land in
    /// the gap. It is retried like any other failure.</para>
    /// </summary>
    private async Task ListenSseAsync(Session session, CancellationToken ct)
    {
        var url = $"{_pythonBaseUrl}/webrtc/connections/{session.SessionId}/events/stream";
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("Accept", "text/event-stream");

                using var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new HttpRequestException(
                        $"Proctoring session {session.SessionId} is not registered yet.");
                }

                response.EnsureSuccessStatusCode();
                attempt = 0;

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null)
                    {
                        break;
                    }

                    if (!line.StartsWith("data: "))
                    {
                        continue;
                    }

                    var data = line[6..];
                    if (string.IsNullOrWhiteSpace(data))
                    {
                        continue;
                    }

                    if (data.Contains("SESSION_ENDED", StringComparison.Ordinal))
                    {
                        LocalFileLogger.Info("proctoring_webrtc", "server_ended_session", new
                        {
                            sessionId = session.SessionId
                        });
                        session.End("server_session_ended");
                        return;
                    }

                    OnProctoringEvent?.Invoke(data);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Was swallowed into Debug.WriteLine and the loop simply returned, so a dropped
                // event stream was invisible AND permanent.
                LocalFileLogger.Error("proctoring_webrtc", "event_stream_failed", ex, new
                {
                    sessionId = session.SessionId,
                    attempt
                });
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            var delay = attempt < ReconnectBackoff.Length
                ? ReconnectBackoff[attempt]
                : LongOutageRetryInterval;
            attempt++;

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task DisconnectAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isStopping = true;

        try
        {
            _lifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_supervisorTask is not null)
        {
            try
            {
                await _supervisorTask;
            }
            catch
            {
            }

            _supervisorTask = null;
        }

        var session = _session;
        _session = null;
        if (session is not null)
        {
            await session.DisposeAsync();
        }
    }

    /// <summary>
    /// How long Dispose waits for the transport to actually come down.
    ///
    /// <para>DisconnectAsync awaits the supervisor, which may be mid-attempt inside OpenSessionAsync
    /// -- so this is sized to let an in-flight attempt finish rather than to be instant. If it is
    /// not enough, disposal gives up and says so instead of blocking shutdown.</para>
    /// </summary>
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a torn-down session waits for its SSE reader to notice. Short on purpose: this sits
    /// directly between a failed proctoring feed and the rebuild that restores it.
    /// </summary>
    private static readonly TimeSpan SseDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Tears the transport down and releases the client.
    ///
    /// <para>The ORDER here is the whole fix. This used to set <c>_isDisposed = true</c> and then
    /// call DisconnectAsync -- whose first statement returns early on exactly that flag. So disposal
    /// disconnected nothing: the supervisor loop kept running, the RTCPeerConnection and its camera
    /// feed were never closed, and the session stayed open for the rest of the process. It then
    /// disposed _lifetimeCts out from under that still-running supervisor, whose next
    /// <c>Task.Delay(..., _lifetimeCts.Token)</c> or <c>WaitAsync(_lifetimeCts.Token)</c> throws
    /// ObjectDisposedException. The normal path hid all of it, because ScreenProctoringService
    /// .StopAsync calls DisconnectAsync directly while the flag is still false; only
    /// ScreenProctoringService.Dispose reaches this.</para>
    ///
    /// <para>Task.Run + Wait for the reason documented in App.EnsureExamFlowStopped: Dispose is
    /// synchronous and may be called on the UI thread, so awaiting inline would capture the WPF
    /// SynchronizationContext and then block the thread its continuations need.</para>
    /// </summary>
    public void Dispose()
    {
        // Claimed atomically, on a flag of its own.
        //
        // _isDisposed cannot do this job: DisconnectAsync bails on it, so setting it up front is the
        // original bug this method exists to fix. But leaving the entry guarded by a plain read left
        // a window where two concurrent Dispose calls both fall through and both run DisconnectAsync
        // -- which then race over _supervisorTask and the session. One claimant, decided once.
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0)
        {
            return;
        }

        var disconnected = false;
        try
        {
            disconnected = Task.Run(DisconnectAsync).Wait(DisposeTimeout);
            if (!disconnected)
            {
                LocalFileLogger.Error(
                    "proctoring_webrtc",
                    "dispose_disconnect_timed_out",
                    new TimeoutException($"Proctoring transport did not stop within {DisposeTimeout}."));
            }
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("proctoring_webrtc", "dispose_disconnect_failed", ex);
        }

        _isDisposed = true;

        // Only once the supervisor is provably finished. Disposing the source while it is still
        // looping is what turned the old ordering from "did not disconnect" into "did not disconnect
        // AND threw" -- and a leaked CancellationTokenSource on a process that is shutting down is
        // the cheaper of the two outcomes by a wide margin.
        if (disconnected)
        {
            _lifetimeCts.Dispose();
        }
    }

    /// <summary>
    /// One generation of the proctoring transport. Same shape as MonitorStreamClient.Session and
    /// for the same reason: a rebuild is one field swap rather than several that a camera callback
    /// can catch half-applied.
    /// </summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly object _endLock = new();
        private Timer? _disconnectTimer;
        private DateTime? _connectedAtUtc;
        private bool _ended;
        private bool _disposed;

        public Session(CancellationTokenSource cts)
        {
            Cts = cts;
        }

        public CancellationTokenSource Cts { get; }

        /// <summary>Completes exactly once, when this generation is finished for any reason.</summary>
        public TaskCompletionSource Ended { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string EndReason { get; private set; } = "";

        public volatile bool Connected;

        public string? SessionId { get; set; }
        public RTCPeerConnection? PeerConnection { get; set; }
        public VP8Codec? Vp8Encoder { get; set; }
        public Task? SseTask { get; set; }
        public int FrameCount { get; set; }
        public bool EncodeErrorLogged { get; set; }

        public void MarkConnected()
        {
            lock (_endLock)
            {
                _connectedAtUtc ??= DateTime.UtcNow;
                _disconnectTimer?.Dispose();
                _disconnectTimer = null;
            }
        }

        public void ArmDisconnectTimer(TimeSpan grace)
        {
            lock (_endLock)
            {
                if (_disposed || _ended || _disconnectTimer is not null)
                {
                    return;
                }

                _disconnectTimer = new Timer(
                    _ => End("disconnected_grace_expired"),
                    null,
                    grace,
                    Timeout.InfiniteTimeSpan);
            }
        }

        public void End(string reason)
        {
            lock (_endLock)
            {
                if (_ended)
                {
                    return;
                }

                _ended = true;
                EndReason = reason;
            }

            Ended.TrySetResult();
        }

        public bool WasStable(TimeSpan threshold) =>
            _connectedAtUtc is { } connectedAt && DateTime.UtcNow - connectedAt >= threshold;

        public double ConnectedSeconds() =>
            _connectedAtUtc is { } connectedAt
                ? Math.Round((DateTime.UtcNow - connectedAt).TotalSeconds, 1)
                : 0;

        public async ValueTask DisposeAsync()
        {
            lock (_endLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _disconnectTimer?.Dispose();
                _disconnectTimer = null;
            }

            // Releases a supervisor waiting on a session torn down from elsewhere.
            End("disposed");

            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (SseTask is not null)
            {
                try
                {
                    // Bounded, because Cts.Cancel() above does not reliably unblock it. The reader
                    // parks in StreamReader.ReadLineAsync over an HTTP response stream, and on
                    // 2026-09-02 it went on processing lines for 48 SECONDS after this cancel --
                    // long enough to log server_ended_session for a session disposed 48s earlier.
                    // Every one of those seconds was time the supervisor spent unable to rebuild.
                    //
                    // Abandoning the task is safe: it holds only its own response stream, observes
                    // the same cancelled token, and ends on its own once the server closes the
                    // stream. Waiting for it is a courtesy, not a requirement.
                    await SseTask.WaitAsync(SseDrainTimeout);
                }
                catch (TimeoutException)
                {
                    LocalFileLogger.Error(
                        "proctoring_webrtc",
                        "sse_drain_timed_out",
                        new TimeoutException($"Event stream did not stop within {SseDrainTimeout}."),
                        new { SessionId });
                }
                catch
                {
                }
            }

            PeerConnection?.close();
            PeerConnection = null;
            Vp8Encoder?.Dispose();
            Vp8Encoder = null;
            Cts.Dispose();
        }
    }
}
