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
/// <para>Second tier -- a signaling resume, used when the socket itself is gone but the peer is
/// not (see SignalingLoopAsync/TryResumeSignalingAsync). A new WebSocket is opened naming the
/// stream we already own, vox-streaming re-attaches it to the same Peer instead of minting a new
/// one, and an ICE restart then runs over the fresh socket. The RTCPeerConnection is untouched on
/// both ends, so there is no DTLS re-handshake -- and the stream id, recording and HLS playlist
/// survive exactly as they do in tier one. This is the tier that used not to exist, and its absence
/// is why a dropped socket always cost a rebuild.</para>
///
/// <para>Third tier -- a full rebuild, now the genuine last resort: the resume ladder was spent, or
/// the server answered with a different stream id because ours had aged out of its grace. ServeStream
/// calls NewPeer for a connection that names no resumable stream, so this necessarily produces a NEW
/// stream id; sessions.Replace then closes the peer the old connection owned, so there is never
/// double ingest. The monitor UI copes -- useMonitoringBoard's pickCurrentStreams keeps one live
/// stream per type per candidate and lets it outrank the ended one, so the proctor keeps a single
/// tile, while allStreams retains the old id so an alert raised before the drop still resolves to
/// its own recording.</para>
///
/// <para>The tiers are ordered by what they COST in meaning, not just in time. A rebuild is
/// disconnected -> left -> joined(new id) on the wire, which is indistinguishable from the student
/// closing the app and re-entering -- and a new stream id for a participant already on the grid is
/// worth keeping as a signal that means a genuine re-entry. Tiers one and two exist to stop an
/// ordinary network blip from spending it; the continuity id they ride on (?resumeStreamId=, answered
/// by stream-ready/stream-resumed) is what makes telling the two apart possible at all, instead of
/// leaving the far end to infer it.</para>
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

    /// <summary>
    /// Delays before each attempt to put a new signaling socket under an existing peer.
    ///
    /// <para>Short and few by design. This ladder runs INSIDE the peer's disconnect deadline
    /// (SignalingResumeGrace below), so it is not a place to wait out a real outage -- that is the
    /// rebuild's job, and it retries for as long as the exam lasts. All this has to cover is the
    /// blip case: a socket that can be replaced within a few seconds, on a peer that is still
    /// there.</para>
    /// </summary>
    private static readonly TimeSpan[] SignalingResumeAttempts =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)
    ];

    /// <summary>
    /// Deadline for a peer that went disconnected while its signaling socket was ALSO down -- the
    /// ordinary blip, since one link usually carries both.
    ///
    /// <para>Longer than DisconnectGrace because there is now something worth waiting for: the
    /// resume ladder above needs ~7s to spend itself, and the ICE restart it enables needs room to
    /// take afterwards. At the old 5s the session would be torn down and rebuilt while the resume
    /// that would have saved it was still in its first retry -- the recovery would exist and never
    /// once get to finish.</para>
    /// </summary>
    private static readonly TimeSpan SignalingResumeGrace = TimeSpan.FromSeconds(20);

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
    /// Starts the transport and arms the supervisor that keeps it alive.
    ///
    /// <para>A failed first attempt is no longer terminal. It used to throw, and
    /// LiveMonitorStreamService would degrade that stream type for the WHOLE exam -- no retry, ever,
    /// even though the supervisor sitting right below this method already knows how to retry
    /// indefinitely and does so for any session that manages to connect even once. The asymmetry was
    /// the bug: it made the very first attempt the only one that had to succeed, on exactly the
    /// networks where a first attempt is least likely to.</para>
    ///
    /// <para>So a failure here arms the supervisor with no session, and RunSupervisorAsync's
    /// no-session branch takes it from there. The caller carries on: frames pushed before the
    /// transport is up are dropped by PushVideoFrame's own connected check, exactly as they are
    /// during a mid-exam reconnect.</para>
    ///
    /// <para>Cancellation still propagates. A caller that gave up during startup is not a network
    /// failure and must not leave a retry loop running behind it.</para>
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Only the FIRST attempt answers to the caller's token; the retries below are governed by
        // this client's own lifetime, because by then nobody is waiting on them.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
        try
        {
            _session = await OpenSessionAsync(linked.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("monitor_stream", "first_connect_failed_retrying", ex, new
            {
                streamType = _streamType.ToString()
            });
            // Same signal a mid-exam drop raises: the picture is not there yet and something is
            // still working on it.
            OnReconnecting?.Invoke();
        }

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
            await ws.ConnectAsync(new Uri(BuildWsUrl(null)), ct);

            session.ReceiveLoopTask = SignalingLoopAsync(session, session.Cts.Token);

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
                else if (session.StreamId is not null)
                {
                    // The restart was refused for want of a live socket, but this session knows its
                    // stream id -- so SignalingLoopAsync is already trying to put a new socket under
                    // this same peer, and the restart will be issued the moment one lands (see the
                    // "stream-resumed" case). Give that sequence room to finish instead of tearing
                    // the peer down underneath it.
                    session.ArmDisconnectTimer(SignalingResumeGrace);
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
        if (session.Ws is not { State: WebSocketState.Open })
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, SignalJsonOptions));

        await session.SendGate.WaitAsync();
        try
        {
            // Re-read the socket INSIDE the gate rather than trusting the snapshot above. A
            // signaling resume swaps session.Ws under this same gate, so without this an ICE
            // candidate gathered mid-resume would be written to the socket being replaced -- lost,
            // and lost precisely during the reconnect that needs it.
            if (session.Ws is { State: WebSocketState.Open } ws)
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

    /// <summary>
    /// Owns the signaling socket for the whole life of a session: pumps messages, and when the
    /// socket dies, tries to put a NEW one under the same peer before giving up on the session.
    ///
    /// <para>This is the middle recovery tier, and it exists because losing the control socket is
    /// not the same event as losing the media path, however often they happen together. Before it,
    /// PumpSignalingAsync's exit ended the session outright, so a dropped WebSocket always cost a
    /// full rebuild -- a new peer, a new stream id, a new HLS playlist and a split recording -- even
    /// when ICE, the tracks and the recording had never faltered. On the wire that reads as
    /// disconnected -> left -> joined(new id), indistinguishable from the student closing the app
    /// and coming back, which is a signal worth far more than an ordinary blip should be allowed to
    /// spend.</para>
    ///
    /// <para>What makes the resume safe is that the peer connection is never touched: the same
    /// RTCPeerConnection stays on this end and the same Peer stays on the server's, so there is no
    /// DTLS re-handshake to negotiate -- only a new socket carrying the same conversation. That is
    /// also why a rebuild remains the fallback rather than something to be avoided at all costs: a
    /// new peer here genuinely cannot re-attach to the old stream.</para>
    /// </summary>
    private async Task SignalingLoopAsync(Session session, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PumpSignalingAsync(session, ct);

            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (!await TryResumeSignalingAsync(session, ct))
            {
                break;
            }
        }

        // The signaling channel is the only way ICE candidates and renegotiation reach the server,
        // so a session that has lost it for good is finished even if its peer connection has not
        // noticed yet. Reached only once every resume attempt has been spent.
        session.End("signaling_closed");
    }

    /// <summary>
    /// Reads signaling messages until the current socket stops delivering them. Returns rather than
    /// ending the session -- deciding whether the session survives is SignalingLoopAsync's job.
    /// </summary>
    private async Task PumpSignalingAsync(Session session, CancellationToken ct)
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
    }

    /// <summary>
    /// Re-opens the signaling socket against the stream this session already owns, naming it so the
    /// server re-attaches instead of minting a new one. Returns whether a socket is live again.
    ///
    /// <para>Bounded on purpose. The peer's own disconnect deadline is running in parallel (see
    /// HandleConnectionStateChanged), and a resume that outlasts it would be racing the rebuild it
    /// is trying to avoid. Spending the short ladder and then conceding to a rebuild is the honest
    /// outcome for a link that is properly down rather than blipping.</para>
    /// </summary>
    private async Task<bool> TryResumeSignalingAsync(Session session, CancellationToken ct)
    {
        var streamId = session.StreamId;
        if (streamId is null)
        {
            // Never got a stream-ready, so there is nothing to re-attach to: either the server
            // predates continuity or this session died before its first exchange.
            return false;
        }

        for (var attempt = 0; attempt < SignalingResumeAttempts.Length; attempt++)
        {
            try
            {
                await Task.Delay(SignalingResumeAttempts[attempt], ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (ct.IsCancellationRequested || session.Ended.Task.IsCompleted)
            {
                return false;
            }

            ClientWebSocket? ws = null;
            try
            {
                ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Origin", _origin);
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(8);
                ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(5);
                await ws.ConnectAsync(new Uri(BuildWsUrl(streamId)), ct);

                // Swapped in only once the new socket is actually up: a failed attempt must leave
                // the session holding its old (dead) socket rather than a null, so that everything
                // reading session.Ws keeps seeing a closed socket instead of crashing.
                //
                // Under the send gate so the swap cannot interleave with a send: SendSignalAsync
                // re-reads session.Ws inside the same gate, which together mean every signal either
                // went out on the old socket before the swap or goes out on the new one after it.
                ClientWebSocket? old;
                await session.SendGate.WaitAsync(ct);
                try
                {
                    old = session.Ws;
                    session.Ws = ws;
                }
                finally
                {
                    session.SendGate.Release();
                }

                old?.Dispose();

                LocalFileLogger.Info("monitor_stream", "signaling_resume_connected", new
                {
                    streamType = _streamType.ToString(),
                    streamId,
                    attempt = attempt + 1
                });
                return true;
            }
            catch (OperationCanceledException)
            {
                ws?.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                ws?.Dispose();
                LocalFileLogger.Error("monitor_stream", "signaling_resume_failed", ex, new
                {
                    streamType = _streamType.ToString(),
                    streamId,
                    attempt = attempt + 1
                });
            }
        }

        return false;
    }

    private void HandleSignalMessage(Session session, SignalMessage message)
    {
        switch (message.Type)
        {
            // First connect on this session: remember which stream we are on so a later socket loss
            // has something to name.
            case "stream-ready":
                if (session.StreamId is null)
                {
                    session.StreamId = message.StreamId;
                    break;
                }

                // We asked to resume and were given a different stream instead, so the server no
                // longer had ours -- it aged out of its grace, or ICE tore it down underneath us.
                // Our peer is now talking to a server peer that never saw its offer, and re-offering
                // an established DTLS transport at a fresh one is not a negotiation worth attempting.
                // End instead and let the supervisor build a clean session.
                if (session.StreamId != message.StreamId)
                {
                    LocalFileLogger.Info("monitor_stream", "signaling_resume_rejected", new
                    {
                        streamType = _streamType.ToString(),
                        requested = session.StreamId,
                        assigned = message.StreamId
                    });
                    session.End("stream_not_resumed");
                }

                break;

            // The server kept our stream and re-attached this socket to it. The peer never moved, so
            // the recording, the stream id and the HLS playlist are all intact.
            case "stream-resumed":
                LocalFileLogger.Info("monitor_stream", "signaling_resumed", new
                {
                    streamType = _streamType.ToString(),
                    streamId = message.StreamId
                });
                // If ICE went down with the socket -- the ordinary case, since one link carries both
                // -- the restart could not be attempted while there was nowhere to send the offer.
                // Now there is. A peer that stayed connected throughout needs nothing here.
                if (session.Pc is { connectionState: RTCPeerConnectionState.disconnected }
                    && TryBeginIceRestart(session))
                {
                    // Same pairing as the tier-one path in HandleConnectionStateChanged: the timer
                    // is the deadline for whatever the restart manages to do. Re-armed rather than
                    // armed, because the SignalingResumeGrace deadline is still running and has
                    // already had most of its budget spent on getting this socket back.
                    session.RearmDisconnectTimer(IceRestartGrace);
                }

                break;
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

    /// <summary>
    /// The signaling URL, optionally asking vox-streaming to re-attach this connection to a stream
    /// it already has rather than opening a new one. A server that does not know the parameter
    /// ignores it and behaves exactly as before.
    /// </summary>
    /// <summary>
    /// How long the goodbye is allowed to take. Short on purpose: it is a courtesy that saves the
    /// server a grace period, not something worth delaying an exam's shutdown for, and the socket it
    /// travels on may already be half-dead.
    /// </summary>
    private static readonly TimeSpan ByeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Tells vox-streaming this stream is finished, as opposed to merely disconnected.
    ///
    /// <para>Best-effort and silent on failure: not being able to say goodbye costs one grace period
    /// on the server, which is exactly where an unannounced client ends up anyway.</para>
    /// </summary>
    private async Task SendByeAsync()
    {
        var session = _session;
        if (session?.Ws is not { State: WebSocketState.Open })
        {
            return;
        }

        try
        {
            await SendSignalAsync(session, new SignalMessage { Type = "bye" }).WaitAsync(ByeTimeout);
            LocalFileLogger.Info("monitor_stream", "bye_sent", new
            {
                streamType = _streamType.ToString(),
                streamId = session.StreamId
            });
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("monitor_stream", "bye_failed", ex, new
            {
                streamType = _streamType.ToString()
            });
        }
    }

    private string BuildWsUrl(string? resumeStreamId) =>
        resumeStreamId is null
            ? _wsUrl
            : $"{_wsUrl}&resumeStreamId={Uri.EscapeDataString(resumeStreamId)}";

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

        // FIRST, before the cancel below, because the cancel is what makes it impossible.
        //
        // vox-streaming can no longer infer a deliberate stop from the socket closing -- a dropped
        // socket is now a resumable event, so an unannounced end waits out the grace and is then
        // closed as a FAILURE, which puts a false STREAM_DROPPED in the student's proctoring record
        // and suppresses MarkComplete on their recording. "bye" is what says otherwise.
        //
        // It cannot be left to a close frame in Session.DisposeAsync: cancelling _lifetimeCts
        // cancels the linked token the signaling pump is parked on inside ReceiveAsync, and
        // cancelling a pending ClientWebSocket receive ABORTS the socket. By the time that
        // CloseAsync runs, Ws.State is Aborted rather than Open, so the frame is never sent and the
        // server sees an ordinary abnormal close.
        await SendByeAsync();

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

        /// <summary>
        /// The server-side stream this session is attached to, learned from "stream-ready" on the
        /// first connect. Null until then, which is also the signal that no signaling resume is
        /// possible yet -- there is nothing to name in ?resumeStreamId=.
        /// </summary>
        public string? StreamId { get; set; }
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

                StartDisconnectTimerLocked(grace);
            }
        }

        /// <summary>
        /// Replaces a running deadline with a fresh one, unlike ArmDisconnectTimer which leaves an
        /// existing timer alone.
        ///
        /// <para>For the moment a signaling resume lands: the deadline still running is
        /// SignalingResumeGrace, and most of it has already been spent getting the socket back. The
        /// ICE restart that follows would inherit only whatever is left -- a sliver, if the resume
        /// took several attempts -- and get torn down mid-negotiation for no reason. Restarting the
        /// clock gives it the same full window it gets on the tier-one path.</para>
        /// </summary>
        public void RearmDisconnectTimer(TimeSpan grace)
        {
            lock (_endLock)
            {
                if (_disposed || _ended)
                {
                    return;
                }

                _disconnectTimer?.Dispose();
                StartDisconnectTimerLocked(grace);
            }
        }

        private void StartDisconnectTimerLocked(TimeSpan grace)
        {
            _disconnectTimer = new Timer(
                _ => End("disconnected_grace_expired"),
                null,
                grace,
                Timeout.InfiniteTimeSpan);
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

        /// <summary>
        /// Carried by the server's "stream-ready"/"stream-resumed" messages, naming the stream this
        /// connection is attached to. Handed back as ?resumeStreamId= when the signaling socket has
        /// to be re-opened -- see TryResumeSignalingAsync.
        /// </summary>
        [JsonPropertyName("streamId")]
        public string? StreamId { get; set; }
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
