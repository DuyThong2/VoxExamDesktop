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
///
/// <para>RECONNECT. The connection used to be one-shot: onconnectionstatechange raised an event
/// nobody subscribed to, and ReceiveLoopAsync returned for good on the first error. A single blip
/// on the exam machine's network therefore ended the proctor's live view of that student for the
/// rest of the exam -- silently, while local recording carried on perfectly. This class now
/// supervises its own transport and rebuilds it (see RunSupervisorAsync).</para>
///
/// <para>Recovery is TWO-TIER, and the order matters for what the proctor is told, not just for
/// how fast the picture comes back.</para>
///
/// <para>First tier -- ICE restart on the existing peer, over the existing signaling socket (see
/// TryIceRestartAsync). vox-streaming needs no change to accept it: runSignaling loops on every
/// "offer" it receives and HandleOffer is a plain SetRemoteDescription/CreateAnswer, which pion
/// treats as an ICE restart once the credentials differ. The peer survives, so the stream id, the
/// recording and the HLS playlist all survive with it -- and, critically, peer.go's
/// PeerConnectionStateConnected branch cancels its disconnect timer and publishes
/// ParticipantReconnected. That is the signal the monitor UI already understands: it clears the
/// StreamView.disconnectedAt that ParticipantDisconnected set moments earlier, and the tile goes
/// back to live. This is the path a brief blip should take, and it costs the proctor one short
/// "mất kết nối" and nothing else.</para>
///
/// <para>Second tier -- a full rebuild, used only when the signaling socket itself is gone, which
/// is what a real outage does. ServeStream calls NewPeer on every WebSocket upgrade and NewPeer
/// mints a fresh uuid, so this necessarily produces a NEW stream id; sessions.Replace then closes
/// the peer the old connection owned, so there is never double ingest. The monitor UI copes --
/// useMonitoringBoard's pickCurrentStreams keeps one live stream per type per candidate and lets
/// it outrank the ended one, so the proctor keeps a single tile, while allStreams retains the old
/// id so an alert raised before the drop still resolves to its own recording.</para>
///
/// <para>But note what the second tier COSTS in meaning: on the wire it is
/// disconnected -> left -> joined(new id), which is indistinguishable from the student closing the
/// app and re-entering. Today a new stream id for a participant already on the grid can only mean
/// a genuine re-entry, and that is worth something to a proctor. Keeping tier one in front is what
/// stops an ordinary network blip from spending that signal; a rebuild should be the rare case,
/// and if the two ever need telling apart the honest fix is a continuity id on the connection
/// rather than more inference at the far end.</para>
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

    /// <summary>
    /// Same shape as RealtimeSessionClient's, deliberately: the exam machine's network is the same
    /// network, and having the two transports back off on different schedules would only make a
    /// shared outage harder to read in the logs.
    /// </summary>
    private static readonly TimeSpan[] ReconnectBackoff =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)
    ];

    /// <summary>
    /// Steady interval once the fast attempts above are spent. Longer than
    /// RealtimeSessionClient's 20s because each attempt here that gets as far as a WebSocket
    /// upgrade mints a stream id, a recording and a pending-assembly entry server-side, whereas
    /// there a failed attempt costs nothing.
    /// </summary>
    private static readonly TimeSpan LongOutageRetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a session has to have been connected before its death is treated as a fresh
    /// outage rather than a continuing one.
    ///
    /// <para>Without this the attempt counter resets on every reconnect, so a link that connects
    /// and fails ICE a few seconds later settles into a ~4 second loop -- and because every
    /// successful upgrade mints a stream, that loop fills the proctor's room and the assembly
    /// queue with dozens of two-second recordings. Carrying the counter across unstable sessions
    /// makes a flapping link decay to one attempt per LongOutageRetryInterval instead.</para>
    /// </summary>
    private static readonly TimeSpan StableConnectionThreshold = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long to let an issued ICE restart prove itself before falling back to a full rebuild.
    ///
    /// <para>Sized for the work it is waiting on: re-gathering host candidates is immediate, a STUN
    /// reflexive candidate is well under a second, and a TURN allocation plus connectivity checks
    /// is the slow end at a few seconds. Generous rather than tight, because what is at stake is
    /// the difference between keeping this stream and minting a new one.</para>
    /// </summary>
    private static readonly TimeSpan IceRestartGrace = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Grace given to a <c>disconnected</c> session that could NOT be offered an ICE restart,
    /// because its signaling socket had already gone.
    ///
    /// <para>Short on purpose: with no signaling there is nothing left that could repair this
    /// session, so the only thing the wait buys is the small chance that ICE heals on its own
    /// before the rebuild starts. Waiting for <c>failed</c> instead would mean roughly thirty
    /// seconds of black video -- that is when ICE consent expires -- for a session already known
    /// to be beyond repair.</para>
    /// </summary>
    private static readonly TimeSpan DisconnectGrace = TimeSpan.FromSeconds(5);

    private readonly string _wsUrl;
    private readonly string _origin;
    private readonly int _videoFps;
    private readonly int _videoBitrate;
    private readonly string _stunUrls;
    private readonly string _turnUrl;
    private readonly string _turnUsername;
    private readonly string _turnCredential;
    private readonly RecordingStreamType _streamType;

    // Only used to seed the very first frame's RTP duration, before any real inter-frame gap is
    // known -- every subsequent frame's duration comes from PushVideoFrame's own captureTimestamp
    // instead (see VideoEncodeWorkerAsync). Its exact value only shifts the whole video timeline by
    // one nominal frame interval, which is harmless; it does not accumulate.
    private readonly uint _firstFrameVideoDuration;

    /// <summary>
    /// Cancelled once for the life of the client, by DisposeAsync. Every session's own token is
    /// linked to it, so disposal stops the supervisor, the in-flight reconnect delay and whichever
    /// session is current, in one move.
    /// </summary>
    private readonly CancellationTokenSource _lifetimeCts = new();

    /// <summary>
    /// The transport as one replaceable unit. Everything in here is created together and dies
    /// together, which is what makes a reconnect a single atomic swap: PushVideoFrame and
    /// PushAudioPcm read this field ONCE into a local and then touch nothing else, so a swap
    /// mid-push can never pair a new peer connection with an old encoder.
    /// </summary>
    private volatile Session? _session;

    private Task? _supervisorTask;

    /// <summary>
    /// Carried across sessions on purpose -- see StableConnectionThreshold.
    /// </summary>
    private int _reconnectAttempt;

    private volatile bool _isDisposed;

    private readonly record struct VideoFrameItem(
        byte[] PixelBytes, int Width, int Height, VideoPixelFormatsEnum PixelFormat, TimeSpan CaptureTimestamp);

    public event Action<RTCPeerConnectionState>? OnConnectionStateChanged;

    /// <summary>Fired when an established session has died and the rebuild loop has started.</summary>
    public event Action? OnReconnecting;

    /// <summary>Fired when a rebuild succeeded. Carries the attempt count it took, for the log.</summary>
    public event Action<int>? OnReconnected;

    /// <summary>
    /// Whether the CURRENT session is carrying media. Read off the session rather than a field on
    /// this class on purpose: a rebuilt peer can reach <c>connected</c> before RebuildAsync has
    /// finished assigning it, and a shared flag written by whichever peer happened to fire last
    /// would then be left false with a perfectly healthy connection underneath it -- every frame
    /// dropped for the rest of the exam, which is the exact failure this whole class is here to
    /// remove. Keeping the flag on the object it describes makes that unrepresentable.
    /// </summary>
    public bool IsConnected => _session?.Connected == true;

    public MonitorStreamClient(
        string streamingBaseUrl, string scheduleId, RecordingStreamType streamType, string token, string origin,
        int videoFps, int videoBitrate,
        string stunUrls, string turnUrl, string turnUsername, string turnCredential)
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
        _streamType = streamType;
        _stunUrls = stunUrls ?? string.Empty;
        _turnUrl = turnUrl ?? string.Empty;
        _turnUsername = turnUsername ?? string.Empty;
        _turnCredential = turnCredential ?? string.Empty;
    }

    /// <summary>
    /// Same STUN/TURN set the proctoring connection uses (Infra/Clients/AIService/WebRtcClient.cs's
    /// BuildRtcConfiguration) -- kept deliberately identical rather than shared, matching this
    /// class's existing "mirrors WebRtcClient's pattern" structure.
    ///
    /// This used to be <c>new RTCPeerConnection(null)</c>, i.e. NO ice servers at all, which meant
    /// the client only ever offered its own LAN address. Over the internet that is unusable twice
    /// over: the exam machine sits behind home NAT so the address is unreachable, and without STUN
    /// it never learns its public address either, so the SDP carries nothing the server can reach.
    /// vox-streaming does allocate a TURN relay of its own, but it can only grant coturn permission
    /// for the peer addresses it sees in that SDP -- the private one -- so the real packets arrived
    /// from an un-permitted address and were dropped. Confirmed live 2026-08-11: camera and screen
    /// both went connecting -> failed after the 31s ICE timeout while HTTP segment upload (a
    /// separate path) kept working, so recordings were fine and only the live view was dead.
    /// </summary>
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

    /// <summary>
    /// Opens the first session and arms the supervisor that keeps it alive.
    ///
    /// <para>This first attempt still throws on failure, unchanged: LiveMonitorStreamService
    /// catches it and degrades that one stream type, which is the documented behaviour for a
    /// machine that cannot stream at all. Only a session that once worked gets rebuilt -- see
    /// RunSupervisorAsync.</para>
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
        _session = await OpenSessionAsync(linked.Token);
        _supervisorTask = Task.Run(RunSupervisorAsync);
    }

    /// <summary>
    /// Builds one complete transport -- WebSocket, peer connection, encoders, video worker -- and
    /// sends the offer. Either returns a live session or throws having disposed everything it
    /// managed to create; it never leaves half a session behind for the supervisor to trip over.
    /// </summary>
    private async Task<Session> OpenSessionAsync(CancellationToken ct)
    {
        var session = new Session(CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token));
        try
        {
            session.VideoEncoder = new MediaFoundationH264Encoder(_videoFps, _videoBitrate);
            session.AudioEncoder = new OpusAudioEncoder();
            session.Pc = new RTCPeerConnection(BuildRtcConfiguration());

            session.VideoQueue = Channel.CreateBounded<VideoFrameItem>(new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            session.VideoWorkerTask = Task.Run(() => VideoEncodeWorkerAsync(session, session.Cts.Token));

            session.Pc.onconnectionstatechange += state => HandleConnectionStateChanged(session, state);

            var videoFormat = new SDPAudioVideoMediaFormat(
                SDPMediaTypesEnum.video,
                102,
                "H264/90000",
                "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f");
            var videoTrack = new MediaStreamTrack(
                SDPMediaTypesEnum.video, false, [videoFormat], MediaStreamStatusEnum.SendOnly);
            session.Pc.addTrack(videoTrack);

            var audioFormat = new SDPAudioVideoMediaFormat(SDPMediaTypesEnum.audio, 111, "opus/48000/2");
            var audioTrack = new MediaStreamTrack(
                SDPMediaTypesEnum.audio, false, [audioFormat], MediaStreamStatusEnum.SendOnly);
            session.Pc.addTrack(audioTrack);

            session.Pc.onicecandidate += candidate =>
            {
                if (candidate is null)
                {
                    return;
                }

                _ = SendSignalAsync(session, new SignalMessage
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

            var ws = new ClientWebSocket();
            session.Ws = ws;
            // vox-streaming's /ws/stream upgrader 403s the handshake unless Origin matches its own
            // ALLOWED_ORIGINS -- ClientWebSocket sends no Origin header on its own, unlike a browser.
            ws.Options.SetRequestHeader("Origin", _origin);
            // Same reasoning as RealtimeSessionClient's: pulling the cable does not fail
            // ReceiveAsync, because TCP keeps the socket "open" until retransmission gives up,
            // which can be minutes. Without these the receive loop below sits there long after the
            // peer connection has already reported the truth, so the WebSocket is the one part of
            // a dead session that never admits it is dead.
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(8);
            ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(5);
            await ws.ConnectAsync(new Uri(_wsUrl), ct);

            session.ReceiveLoopTask = ReceiveLoopAsync(session, session.Cts.Token);

            var offer = session.Pc.createOffer();
            await session.Pc.setLocalDescription(offer);
            await SendSignalAsync(session, new SignalMessage { Type = "offer", Sdp = offer.sdp });

            LocalFileLogger.Info("monitor_stream", "session_opened", new { streamType = _streamType.ToString() });
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Rebuilds the transport whenever a session that was working stops working.
    ///
    /// <para>Runs for the life of the client. It only ever reacts to a session ENDING, so it does
    /// nothing at all on a healthy exam; the loop below is idle on Session.Ended for the full
    /// forty minutes in the normal case.</para>
    /// </summary>
    private async Task RunSupervisorAsync()
    {
        while (!_isDisposed && !_lifetimeCts.IsCancellationRequested)
        {
            var current = _session;
            if (current is null)
            {
                return;
            }

            try
            {
                await current.Ended.Task.WaitAsync(_lifetimeCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_isDisposed || _lifetimeCts.IsCancellationRequested)
            {
                return;
            }

            // A session that held up for a while is evidence the network itself is fine and this
            // was a discrete event, so the next outage starts from the fast attempts again. One
            // that died young is evidence of the opposite, and keeps the counter it inherited.
            if (current.WasStable(StableConnectionThreshold))
            {
                _reconnectAttempt = 0;
            }

            LocalFileLogger.Error(
                "monitor_stream",
                "session_lost_rebuilding",
                new InvalidOperationException($"Live monitor session for {_streamType} ended; rebuilding."),
                new
                {
                    streamType = _streamType.ToString(),
                    reason = current.EndReason,
                    connectedSeconds = current.ConnectedSeconds()
                });
            OnReconnecting?.Invoke();

            await current.DisposeAsync();
            _session = null;

            if (!await RebuildAsync())
            {
                return;
            }
        }
    }

    /// <summary>
    /// Retries OpenSessionAsync until it succeeds or the client is disposed. Returns false only
    /// when it was told to stop -- there is no give-up: an exam runs for tens of minutes and a
    /// proctor who has lost the live view wants it back at any point in that window, not only in
    /// the first thirty seconds.
    /// </summary>
    private async Task<bool> RebuildAsync()
    {
        while (!_isDisposed && !_lifetimeCts.IsCancellationRequested)
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

            if (_isDisposed || _lifetimeCts.IsCancellationRequested)
            {
                return false;
            }

            try
            {
                var session = await OpenSessionAsync(_lifetimeCts.Token);
                _session = session;
                LocalFileLogger.Info("monitor_stream", "session_rebuilt", new
                {
                    streamType = _streamType.ToString(),
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
                LocalFileLogger.Error("monitor_stream", "session_rebuild_failed", ex, new
                {
                    streamType = _streamType.ToString(),
                    attempt = _reconnectAttempt,
                    delaySeconds = delay.TotalSeconds
                });
            }
        }

        return false;
    }

    private void HandleConnectionStateChanged(Session session, RTCPeerConnectionState state)
    {
        // Written to the session this callback belongs to, never to shared client state -- see
        // IsConnected. A replaced peer reporting `closed` during teardown updates only its own
        // dead session, where nothing reads it.
        session.Connected = state == RTCPeerConnectionState.connected;

        OnConnectionStateChanged?.Invoke(state);

        switch (state)
        {
            case RTCPeerConnectionState.connected:
                session.MarkConnected();
                break;

            case RTCPeerConnectionState.disconnected:
                // Repair before replace. An ICE restart keeps the peer -- and with it the stream
                // id, the recording, and the ParticipantReconnected the monitor UI is waiting for
                // -- so it is always worth trying before falling back to a rebuild that mints a
                // new stream and reads on the wire like the student left and came back.
                //
                // Both arms fire together: the timer is the deadline for whatever the restart
                // manages to do, and it is the only thing that gets us out if the restart is
                // impossible or simply does not take. A recovery cancels it (see MarkConnected),
                // so a flapping connection cannot stack timers.
                if (TryBeginIceRestart(session))
                {
                    session.ArmDisconnectTimer(IceRestartGrace);
                }
                else
                {
                    session.ArmDisconnectTimer(DisconnectGrace);
                }

                break;

            case RTCPeerConnectionState.failed:
            case RTCPeerConnectionState.closed:
                session.End(state.ToString());
                break;
        }
    }

    /// <summary>
    /// Asks ICE to re-gather and re-check on the peer we already have, and re-offers it down the
    /// signaling socket we already have. Returns whether an attempt was actually started, which is
    /// what decides how long the caller's deadline should be.
    ///
    /// <para>Fire-and-forget by design: the outcome is not reported back here but observed through
    /// the ordinary connection state callback, because that is the same thing that has to work for
    /// a recovery to count. If the restart succeeds the peer reaches <c>connected</c>, MarkConnected
    /// cancels the deadline, and nothing else happens. If it fails -- or throws, or silently does
    /// nothing -- the deadline expires and the rebuild takes over. There is no path where a failed
    /// restart leaves the session stuck, which is why nothing here needs to be awaited.</para>
    ///
    /// <para>Requires a live signaling socket: the re-offer has nowhere to go without one, and the
    /// answer has no way back. That is the whole reason the second tier exists.</para>
    /// </summary>
    private bool TryBeginIceRestart(Session session)
    {
        if (session.Ws is not { State: WebSocketState.Open } || session.Pc is null)
        {
            return false;
        }

        // One attempt per disconnected spell. Without this a peer that reports disconnected
        // repeatedly without ever reaching connected would queue an offer per report, and the
        // server would answer each one -- renegotiating a connection that is trying to renegotiate.
        if (!session.BeginIceRestart())
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                session.Pc.restartIce();
                var offer = session.Pc.createOffer();
                await session.Pc.setLocalDescription(offer);
                await SendSignalAsync(session, new SignalMessage { Type = "offer", Sdp = offer.sdp });
                LocalFileLogger.Info("monitor_stream", "ice_restart_offered", new
                {
                    streamType = _streamType.ToString()
                });
            }
            catch (Exception ex)
            {
                // Logged, not escalated: the deadline armed by the caller is already the answer to
                // this failing, and racing it with a second teardown path would only make the
                // ordering harder to reason about.
                LocalFileLogger.Error("monitor_stream", "ice_restart_failed", ex, new
                {
                    streamType = _streamType.ToString()
                });
            }
        });

        return true;
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
        // Read the session ONCE. Re-reading the field per use is how a frame ends up half-written
        // into a session that is being torn down underneath it.
        var session = _session;
        if (session?.VideoQueue is null || !session.Connected || _isDisposed)
        {
            return;
        }

        // TryWrite never blocks/fails here: the channel is bounded with DropOldest, so a full
        // queue just silently evicts the stale frame in favor of this newer one.
        session.VideoQueue.Writer.TryWrite(new VideoFrameItem(pixelBytes, width, height, pixelFormat, captureTimestamp));
    }

    // The single consumer of a session's video queue -- see the field's own comment for why
    // encode/send must never run concurrently from more than one caller.
    private async Task VideoEncodeWorkerAsync(Session session, CancellationToken ct)
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
        //
        // Deliberately per-session: a rebuilt session is a NEW stream server-side with its own
        // recording, so its timeline starts fresh here rather than carrying the gap that the
        // outage opened in the capture clock.
        long? prevRtpPosition = null;
        try
        {
            await foreach (var item in session.VideoQueue!.Reader.ReadAllAsync(ct))
            {
                if (session.VideoEncoder is null || session.Pc is null)
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
                    // Acted on HERE, on the single worker, because the encoder is not thread-safe
                    // and the flag is set from the peer's connection callback thread.
                    if (session.KeyFrameRequested)
                    {
                        session.KeyFrameRequested = false;
                        session.VideoEncoder.ForceKeyFrame();
                        LocalFileLogger.Info("monitor_stream", "keyframe_forced_after_recovery", new
                        {
                            streamType = _streamType.ToString()
                        });
                    }

                    var encoded = session.VideoEncoder.Encode(
                        item.PixelBytes, item.Width, item.Height, item.PixelFormat, item.CaptureTimestamp);
                    if (encoded is { Length: > 0 })
                    {
                        session.Pc.SendVideo(durationRtpUnits, encoded);
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
        // One read, for the same reason as PushVideoFrame.
        var session = _session;
        if (session?.Pc is null || session.AudioEncoder is null || !session.Connected || _isDisposed)
        {
            return;
        }

        try
        {
            foreach (var frame in session.AudioEncoder.Encode(pcm16Mono))
            {
                session.Pc.SendAudio(AudioDurationPerFrame, frame);
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

    private static async Task SendSignalAsync(Session session, SignalMessage message)
    {
        var ws = session.Ws;
        if (ws is null || ws.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, SignalJsonOptions));

        await session.SendGate.WaitAsync();
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
            session.SendGate.Release();
        }
    }

    private async Task ReceiveLoopAsync(Session session, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var messageStream = new MemoryStream();

        try
        {
            while (session.Ws is { State: WebSocketState.Open } ws && !ct.IsCancellationRequested)
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
                    HandleSignalMessage(session, message);
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
        finally
        {
            // The signaling channel is the only way ICE candidates and renegotiation reach the
            // server, so a session that has lost it is finished even if its peer connection has
            // not noticed yet. Previously this method simply returned and the stream stayed
            // half-alive until the media path timed out -- or forever.
            session.End("signaling_closed");
        }
    }

    private static void HandleSignalMessage(Session session, SignalMessage message)
    {
        switch (message.Type)
        {
            case "answer":
                if (message.Sdp is not null && session.Pc is not null)
                {
                    session.Pc.setRemoteDescription(new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.answer,
                        sdp = message.Sdp
                    });
                }

                break;

            case "ice-candidate":
                if (message.Candidate is not null && session.Pc is not null)
                {
                    session.Pc.addIceCandidate(new RTCIceCandidateInit
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

        // Before awaiting the supervisor: it parks on Session.Ended and on Task.Delay, and this
        // token is what releases both. Cancelling after the await would deadlock a client disposed
        // while a reconnect is sitting out its backoff.
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
        }

        var session = _session;
        _session = null;
        if (session is not null)
        {
            await session.DisposeAsync();
        }

        _lifetimeCts.Dispose();
    }

    /// <summary>
    /// One transport generation: the WebSocket, the peer connection, the encoders and the worker
    /// that feeds them. Created and destroyed as a unit so a reconnect is a single field swap
    /// rather than eight separate ones that a capture thread can observe half-applied.
    /// </summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly object _endLock = new();
        private Timer? _disconnectTimer;
        private DateTime? _connectedAtUtc;
        private bool _iceRestartInFlight;
        private bool _ended;
        private bool _disposed;

        public Session(CancellationTokenSource cts)
        {
            Cts = cts;
        }

        /// <summary>
        /// Whether THIS generation's peer is carrying media. Written by the connection state
        /// callback, read by the push methods off the same snapshot they took of the session --
        /// see MonitorStreamClient.IsConnected for why it does not live on the client.
        /// </summary>
        public volatile bool Connected;

        public CancellationTokenSource Cts { get; }
        public SemaphoreSlim SendGate { get; } = new(1, 1);

        /// <summary>
        /// Completes exactly once, when this generation is finished for any reason. The supervisor
        /// waits on it; End is safe to call from the peer callback, the receive loop and the
        /// disconnect timer at the same time.
        /// </summary>
        public TaskCompletionSource Ended { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string EndReason { get; private set; } = "";

        public ClientWebSocket? Ws { get; set; }
        public RTCPeerConnection? Pc { get; set; }
        public MediaFoundationH264Encoder? VideoEncoder { get; set; }
        public OpusAudioEncoder? AudioEncoder { get; set; }
        public Channel<VideoFrameItem>? VideoQueue { get; set; }
        public Task? ReceiveLoopTask { get; set; }
        public Task? VideoWorkerTask { get; set; }

        /// <summary>
        /// Set when this session has come back from a disconnect and the encoder therefore owes the
        /// far side an IDR. Read and cleared by the video worker, never acted on here: the encoder
        /// is not thread-safe and this is set from the peer's connection callback.
        /// </summary>
        public volatile bool KeyFrameRequested;

        public void MarkConnected()
        {
            lock (_endLock)
            {
                if (_connectedAtUtc is null)
                {
                    _connectedAtUtc = DateTime.UtcNow;
                }
                else
                {
                    // Already been connected once, so this is a RECOVERY -- an ICE restart took, or
                    // ICE healed on its own. Either way the peer and its encoder were kept while the
                    // far side's decoder lost everything in between, so the next frame has to be an
                    // IDR or the picture stays broken until the GOP happens to come round.
                    KeyFrameRequested = true;
                }

                // Reached connected inside the deadline -- either ICE healed on its own or the
                // restart took. Either way this session lives, and the server has just published
                // ParticipantReconnected for it.
                _disconnectTimer?.Dispose();
                _disconnectTimer = null;
                // Re-arm for a LATER spell. This is one restart per disconnection, not one per
                // session: a second, unrelated blip twenty minutes on deserves its own attempt.
                _iceRestartInFlight = false;
            }
        }

        /// <summary>
        /// Claims the right to issue an ICE restart for the current disconnected spell. Returns
        /// false if one has already been issued and has neither succeeded (MarkConnected) nor been
        /// overtaken by the session ending.
        /// </summary>
        public bool BeginIceRestart()
        {
            lock (_endLock)
            {
                if (_disposed || _ended || _iceRestartInFlight)
                {
                    return false;
                }

                _iceRestartInFlight = true;
                return true;
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
                // Guarded by a flag rather than by Ended.Task.IsCompleted: the completion happens
                // outside the lock (below), so two callers racing -- the peer reporting `failed`
                // and the receive loop noticing the socket is gone, which is the ordinary pairing
                // -- would both pass an IsCompleted check and the second would overwrite the
                // first's reason. The reason is the only account of why the stream was rebuilt,
                // and it wants to name whichever signal actually arrived first.
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

            // Unblocks any supervisor still waiting on a session torn down from elsewhere (client
            // disposal racing a drop), so nothing parks on a task that will never complete.
            End("disposed");

            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (Ws is { State: WebSocketState.Open })
            {
                try
                {
                    await Ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client stop", CancellationToken.None);
                }
                catch
                {
                }
            }

            if (ReceiveLoopTask is not null)
            {
                try
                {
                    await ReceiveLoopTask;
                }
                catch
                {
                }
            }

            // Must finish (it exits on its own once Cts.Cancel() above cancels its ReadAllAsync)
            // before Pc/VideoEncoder are torn down below -- it's the only thing that touches them.
            if (VideoWorkerTask is not null)
            {
                try
                {
                    await VideoWorkerTask;
                }
                catch
                {
                }
            }

            Pc?.close();
            Pc = null;
            VideoEncoder?.Dispose();
            VideoEncoder = null;
            AudioEncoder?.Dispose();
            AudioEncoder = null;
            Ws?.Dispose();
            Ws = null;
            Cts.Dispose();
            SendGate.Dispose();
        }
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
