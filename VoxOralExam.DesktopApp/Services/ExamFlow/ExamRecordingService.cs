using System.IO;
using VoxOralExam.Core.Interfaces;
using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Infra.Clients.StreamService;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.Infra.Recording;
using VoxOralExam.DesktopApp.Infra.Recording.Audio;
using VoxOralExam.DesktopApp.Infra.Recording.Storage;
using VoxOralExam.DesktopApp.State;
using VoxOralExam.DesktopApp.Workers;

namespace VoxOralExam.DesktopApp.Services.ExamFlow;

public sealed class ExamRecordingService : IExamRecordingService, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly ExamSessionState _sessionState;
    private readonly StreamSessionClient _sessionClient;
    private readonly UploadCredentialRefresher _credentialRefresher;
    private readonly SegmentUploadWorker _uploadWorker;
    private readonly LocalSegmentStore _store;
    private readonly RecordingClock _clock;
    private readonly ScreenSegmentRecorder _screenRecorder;
    private readonly CameraSegmentRecorder _cameraRecorder;
    private readonly CameraService _camera;
    private readonly LiveMonitorStreamService _liveMonitor;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Dictionary<RecordingStreamType, StreamUploadSession> _uploadSessions = [];
    private readonly HashSet<RecordingStreamType> _startedStreams = [];

    // Tracks which started streams ever actually committed a segment, distinct from
    // _startedStreams (which only means the recorder opened successfully). A stream that opened
    // but captured zero frames for its whole duration (e.g. a very short Start-then-Stop test, or a
    // capture source that silently never fires) has GetOutstandingCountAsync == 0 too -- nothing
    // was ever pending -- but calling /complete for it tells vox-streaming to assemble a stream that
    // never received a single segment, which its Kafka consumer will retry for a very long time
    // before giving up. See StopCoreAsync's completion loop.
    private readonly HashSet<RecordingStreamType> _committedStreams = [];

    private RecordingSessionContext? _context;
    private bool _uploadEnabledForAttempt;
    private bool _disposed;

    // Recording audio (mic always; system/loopback audio for Screen only -- see AudioMixer) is
    // owned here rather than by ScreenSegmentRecorder/CameraSegmentRecorder directly, and is a
    // dedicated, separate NAudio capture from RealtimeExamFlowService's own TurnAudioRecorder
    // (used for STT/VAD): the two flows have different lifecycles (this one spans exactly the
    // local recording session; that one spans the realtime AI conversation) and the demo screen
    // (StreamingDemoViewModel) never runs RealtimeExamFlowService at all, so this recorder needs to
    // work standalone. Windows WASAPI shared-mode capture supports the same physical mic device
    // being opened more than once, so this does not conflict with the STT capture when both run.
    private TurnAudioRecorder? _micRecorder;
    private SystemAudioLoopbackCapture? _loopbackCapture;
    private AudioMixer? _audioMixer;

    // Periodic health checks that run for as long as recording does: free disk space (see
    // AppSettings.RecordingDiskCheckSeconds for why the start-of-attempt check is not enough) and
    // upload credentials approaching expiry.
    private Timer? _recordingWatchdog;

    // Guards the credential refresh against re-entering itself on a later tick while an earlier
    // one is still waiting on the network.
    private int _refreshingCredentials;

    // Same guard for the inventory declaration: a slow upload must not have a later tick start a
    // second one behind it.
    private int _declaringInventory;

    // Latches so crossing the threshold is reported once on the way down and once on the way back
    // up, instead of every tick for the rest of the exam.
    private bool _diskSpaceLow;

    public event Action<RecordingStatus>? StatusChanged;

    public bool IsRecording { get; private set; }

    public ExamRecordingService(
        AppSettings settings,
        ExamSessionState sessionState,
        StreamSessionClient sessionClient,
        UploadCredentialRefresher credentialRefresher,
        SegmentUploadWorker uploadWorker,
        LocalSegmentStore store,
        RecordingClock clock,
        ScreenSegmentRecorder screenRecorder,
        CameraSegmentRecorder cameraRecorder,
        CameraService camera,
        LiveMonitorStreamService liveMonitor)
    {
        _settings = settings;
        _sessionState = sessionState;
        _sessionClient = sessionClient;
        _credentialRefresher = credentialRefresher;
        _uploadWorker = uploadWorker;
        _store = store;
        _clock = clock;
        _screenRecorder = screenRecorder;
        _cameraRecorder = cameraRecorder;
        _camera = camera;
        _liveMonitor = liveMonitor;
    }

    public async Task StartAsync(
        RecordingSessionContext context,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRecording || !_settings.EnableLocalRecording)
            {
                return;
            }

            ValidateContext(context);
            _store.EnsureFreeSpace(_settings.MinimumRecordingDiskBytes);
            await _store.InitializeAsync(context, cancellationToken);

            _context = context;
            _startedStreams.Clear();
            _committedStreams.Clear();
            _uploadSessions.Clear();
            _uploadEnabledForAttempt = _settings.EnableSegmentUpload;
            _clock.Start();

            var micAvailable = await TryStartMicAsync(cancellationToken);
            await TryStartLoopbackAsync(cancellationToken);
            _audioMixer?.Start();

            _screenRecorder.SegmentCompleted += OnSegmentCompleted;
            _cameraRecorder.SegmentCompleted += OnSegmentCompleted;
            _screenRecorder.RecordingFailed += OnScreenRecordingFailed;
            _cameraRecorder.RecordingFailed += OnCameraRecordingFailed;

            var streamIds = await CreateStreamIdsAsync(context, cancellationToken);
            if (_uploadEnabledForAttempt)
            {
                // Written to the manifest before the first segment exists, so the credentials are
                // already on disk no matter how abruptly this run ends -- a crash or a power cut
                // one second later still leaves a later run able to finish the upload.
                await _store.SaveUploadSessionsAsync(
                    [.. _uploadSessions.Values.Select(session => new StoredUploadSession
                    {
                        StreamId = session.StreamId,
                        StreamType = session.StreamType,
                        UploadToken = session.UploadToken,
                        ExpiresAt = session.ExpiresAt
                    })],
                    cancellationToken);
                _uploadWorker.Start(_uploadSessions.Values);
            }

            if (context.StreamTypes.Contains(RecordingStreamType.Screen))
            {
                try
                {
                    await _screenRecorder.StartAsync(
                        streamIds[RecordingStreamType.Screen],
                        await _store.GetNextSequenceAsync(streamIds[RecordingStreamType.Screen], cancellationToken),
                        cancellationToken,
                        includeAudio: micAvailable);
                    _startedStreams.Add(RecordingStreamType.Screen);
                }
                catch (Exception ex)
                {
                    LocalFileLogger.Error("recording", "screen_start_failed", ex);
                    PublishStatus(
                        "screen_recording_unavailable",
                        $"Screen recording is unavailable: {ex.Message}",
                        isDegraded: true);
                }
            }

            if (context.StreamTypes.Contains(RecordingStreamType.Camera))
            {
                try
                {
                    await _cameraRecorder.StartAsync(
                        streamIds[RecordingStreamType.Camera],
                        await _store.GetNextSequenceAsync(streamIds[RecordingStreamType.Camera], cancellationToken),
                        cancellationToken,
                        includeAudio: micAvailable);
                    _camera.OnCapturedFrame += OnCameraFrame;
                    _startedStreams.Add(RecordingStreamType.Camera);
                }
                catch (Exception ex)
                {
                    LocalFileLogger.Error("recording", "camera_start_failed", ex);
                    PublishStatus(
                        "camera_recording_unavailable",
                        $"Camera recording is unavailable: {ex.Message}",
                        isDegraded: true);
                }
            }

            if (_startedStreams.Count == 0)
            {
                await StopCoreAsync(RecordingStopReason.CaptureFailure, CancellationToken.None);
                throw new InvalidOperationException("No local recording source could be started.");
            }

            // Best-effort, independent of local recording: a failure to connect/stream live must
            // never roll back or block the local recording + segment upload above, which is the
            // durable evidence path. LiveMonitorStreamService itself no-ops when
            // EnableLiveMonitorStream is off.
            try
            {
                await _liveMonitor.StartAsync(context, cancellationToken);
            }
            catch (Exception ex)
            {
                LocalFileLogger.Error("recording", "live_monitor_start_failed", ex);
            }

            StartRecordingWatchdog();

            IsRecording = true;
            PublishStatus("recording_started", "Local recording has started.");
        }
        catch
        {
            try
            {
                await StopCoreAsync(RecordingStopReason.CaptureFailure, CancellationToken.None);
            }
            catch (Exception cleanupError)
            {
                LocalFileLogger.Error("recording", "start_rollback_failed", cleanupError);
            }

            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<Dictionary<RecordingStreamType, string>> CreateStreamIdsAsync(
        RecordingSessionContext context,
        CancellationToken ct)
    {
        var ids = new Dictionary<RecordingStreamType, string>();
        foreach (var streamType in context.StreamTypes.Distinct())
        {
            var wireType = ToWireValue(streamType);
            if (!_uploadEnabledForAttempt)
            {
                ids[streamType] = $"local-{wireType}-{Guid.CreateVersion7():D}";
                continue;
            }

            try
            {
                var session = await _sessionClient.CreateAsync(
                    wireType,
                    context.StreamToken,
                    ct);
                _uploadSessions[streamType] = session;
                ids[streamType] = session.StreamId;
            }
            catch (Exception ex)
            {
                _uploadEnabledForAttempt = false;
                ids[streamType] = $"local-{wireType}-{Guid.CreateVersion7():D}";
                LocalFileLogger.Error("recording", "upload_session_create_failed", ex, new { streamType });
                PublishStatus(
                    "segment_upload_unavailable",
                    "The upload session is unavailable; segments will remain safely stored on this device.",
                    isDegraded: true);
            }
        }

        // If one server-side session failed, keep the whole attempt local to avoid uploading a
        // mixed set where only one evidence stream can be finalized.
        if (!_uploadEnabledForAttempt)
        {
            _uploadSessions.Clear();
            foreach (var streamType in context.StreamTypes.Distinct())
            {
                ids[streamType] = $"local-{ToWireValue(streamType)}-{Guid.CreateVersion7():D}";
            }
        }

        return ids;
    }

    private void OnCameraFrame(CameraFrame frame)
    {
        _cameraRecorder.TryEnqueue(frame);
    }

    /// <summary>
    /// Opens this recording session's dedicated mic capture (separate from
    /// RealtimeExamFlowService's own TurnAudioRecorder -- see the field comment on _micRecorder).
    /// Returns whether it succeeded so StartAsync can decide whether Screen/Camera should request
    /// an audio stream at all: a mic that never opens must not leave either video file with an
    /// audio stream that receives zero samples.
    /// </summary>
    private async Task<bool> TryStartMicAsync(CancellationToken ct)
    {
        var recorder = new TurnAudioRecorder(deviceNumber: _sessionState.SelectedAudioInputDeviceIndex);
        var mixer = new AudioMixer(_clock);
        try
        {
            recorder.StreamChunkAvailable += OnMicAudioChunk;
            await recorder.StartAsync(ct);
            mixer.MixedAudioAvailable += OnMixedScreenAudio;
            _micRecorder = recorder;
            _audioMixer = mixer;
            return true;
        }
        catch (Exception ex)
        {
            recorder.StreamChunkAvailable -= OnMicAudioChunk;
            recorder.Dispose();
            mixer.Dispose();
            LocalFileLogger.Error("recording", "mic_start_failed", ex);
            PublishStatus(
                "recording_audio_unavailable",
                $"Recording audio is unavailable: {ex.Message}",
                isDegraded: true);
            return false;
        }
    }

    /// <summary>
    /// Adds system/device audio (e.g. the avatar's TTS voice) to Screen's audio track via
    /// AudioMixer. Optional and independent of the mic: if this fails to open (e.g. no default
    /// playback device), Screen's recording simply keeps its mic-only audio -- see the
    /// degrade-not-roll-back decision for loopback capture.
    /// </summary>
    private async Task TryStartLoopbackAsync(CancellationToken ct)
    {
        if (_audioMixer is null)
        {
            return;
        }

        var loopback = new SystemAudioLoopbackCapture();
        try
        {
            await loopback.StartAsync(ct);
            loopback.DataAvailable += OnLoopbackAudioChunk;
            _audioMixer.EnableLoopback(loopback.WaveFormat!);
            _loopbackCapture = loopback;
        }
        catch (Exception ex)
        {
            loopback.Dispose();
            LocalFileLogger.Error("recording", "loopback_start_failed", ex);
            PublishStatus(
                "system_audio_unavailable",
                "System/device audio is unavailable; Screen recording will still include the microphone.",
                isDegraded: true);
        }
    }

    private void OnMicAudioChunk(byte[] pcm)
    {
        _audioMixer?.AddMicSamples(pcm);
        if (_startedStreams.Contains(RecordingStreamType.Camera))
        {
            _cameraRecorder.EnqueueAudio(pcm, _clock.Elapsed);
        }
    }

    private void OnLoopbackAudioChunk(byte[] raw) => _audioMixer?.AddLoopbackSamples(raw);

    private void OnMixedScreenAudio(byte[] pcm, TimeSpan timestamp)
    {
        if (_startedStreams.Contains(RecordingStreamType.Screen))
        {
            _screenRecorder.EnqueueAudio(pcm, timestamp);
        }
    }

    private async Task StopAudioCaptureAsync()
    {
        if (_micRecorder is not null)
        {
            var recorder = _micRecorder;
            _micRecorder = null;
            recorder.StreamChunkAvailable -= OnMicAudioChunk;
            await recorder.StopAsync();
            recorder.Dispose();
        }

        if (_loopbackCapture is not null)
        {
            var loopback = _loopbackCapture;
            _loopbackCapture = null;
            loopback.DataAvailable -= OnLoopbackAudioChunk;
            await loopback.StopAsync();
            loopback.Dispose();
        }

        if (_audioMixer is not null)
        {
            var mixer = _audioMixer;
            _audioMixer = null;
            mixer.MixedAudioAvailable -= OnMixedScreenAudio;
            mixer.Dispose();
        }
    }

    private void StartRecordingWatchdog()
    {
        _diskSpaceLow = false;
        var period = TimeSpan.FromSeconds(_settings.RecordingDiskCheckSeconds);
        if (period <= TimeSpan.Zero)
        {
            return;
        }

        _recordingWatchdog = new Timer(
            _ =>
            {
                CheckDiskSpace();
                // Not awaited: a timer callback must not block, and work still in flight when the
                // next tick arrives is skipped by its own re-entrancy guard.
                _ = RefreshExpiringCredentialsAsync();
                _ = DeclareInventoriesAsync(complete: false, CancellationToken.None);
            },
            null,
            period,
            period);
    }

    /// <summary>
    /// Tells vox-streaming what this device has captured, so it can tell a stream that finished from
    /// one that merely stopped.
    ///
    /// Sent on every watchdog tick, not just at the end, because the failure it guards against is
    /// this process not reaching the end at all -- and best-effort throughout, since a declaration
    /// that does not get through costs only the precision of a later gap report, never a segment.
    /// </summary>
    private async Task DeclareInventoriesAsync(bool complete, CancellationToken ct)
    {
        if (!_uploadEnabledForAttempt)
        {
            return;
        }

        if (Interlocked.Exchange(ref _declaringInventory, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var (streamType, session) in _uploadSessions.ToList())
            {
                if (!_startedStreams.Contains(streamType))
                {
                    continue;
                }

                try
                {
                    var declared = await _store.GetDeclaredSegmentsAsync(session.StreamId, ct);
                    if (declared.Count == 0)
                    {
                        continue;
                    }

                    await _sessionClient.DeclareInventoryAsync(
                        session.StreamId, session.UploadToken, complete, declared, ct);
                }
                catch (Exception ex)
                {
                    LocalFileLogger.Error(
                        "recording",
                        "declare_inventory_failed",
                        ex,
                        new { session.StreamId, session.StreamType, complete });
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _declaringInventory, 0);
        }
    }

    /// <summary>
    /// Renews upload credentials before they expire, so a long network outage cannot leave this
    /// attempt holding a token the server has stopped accepting.
    ///
    /// Proactive rather than reactive on purpose. Waiting for the first 410 means discovering the
    /// problem only once the credential is already dead, and at that point the exam may also be
    /// over -- which is exactly when the Java endpoint behind the refresh stops issuing tokens.
    /// Renewing while the exam is still running is the only window that reliably exists.
    /// </summary>
    private async Task RefreshExpiringCredentialsAsync()
    {
        if (!_uploadEnabledForAttempt || _context is not { } context)
        {
            return;
        }

        if (Interlocked.Exchange(ref _refreshingCredentials, 1) == 1)
        {
            return;
        }

        try
        {
            var lead = TimeSpan.FromMinutes(Math.Max(1, _settings.UploadCredentialRefreshLeadMinutes));
            var deadline = DateTimeOffset.UtcNow.Add(lead);
            var expiring = _uploadSessions
                .Where(pair => pair.Value.ExpiresAt <= deadline)
                .ToList();
            if (expiring.Count == 0)
            {
                return;
            }

            foreach (var (streamType, session) in expiring)
            {
                var renewed = await _credentialRefresher.TryRefreshAsync(
                    context.AttemptId, session.StreamType, CancellationToken.None);
                if (renewed is null)
                {
                    continue;
                }

                if (!string.Equals(renewed.StreamId, session.StreamId, StringComparison.Ordinal))
                {
                    // vox-streaming only opens a new stream id when the previous one was already
                    // completed. Adopting it here would silently split this attempt's evidence
                    // across two streams, so keep the original and let it run to its own expiry.
                    LocalFileLogger.Error(
                        "recording",
                        "credential_refresh_returned_different_stream",
                        new InvalidOperationException(
                            $"Refresh for {session.StreamId} returned {renewed.StreamId}."),
                        new { session.StreamId, renewedStreamId = renewed.StreamId, session.StreamType });
                    continue;
                }

                _uploadSessions[streamType] = renewed;
                _uploadWorker.UpdateUploadToken(renewed.StreamId, renewed.UploadToken);
                await _store.SaveUploadSessionsAsync(
                    [new StoredUploadSession
                    {
                        StreamId = renewed.StreamId,
                        StreamType = renewed.StreamType,
                        UploadToken = renewed.UploadToken,
                        ExpiresAt = renewed.ExpiresAt
                    }],
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("recording", "credential_refresh_sweep_failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshingCredentials, 0);
        }
    }

    /// <summary>
    /// Reports the recording drive running out of room, and deliberately does no more than that.
    ///
    /// The two ways to actually free space -- stop recording, or delete already-captured segments --
    /// both destroy evidence for the period they cover, and which is less bad is a policy question
    /// about the exam, not something this timer should decide on its own. Surfacing it as a degraded
    /// status gives a proctor the chance to act while there is still room to act in; if nothing is
    /// done, writes eventually fail and the recorders' own RecordingFailed path takes over.
    /// </summary>
    private void CheckDiskSpace()
    {
        long available;
        try
        {
            available = _store.AvailableFreeSpace();
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("recording", "disk_space_check_failed", ex);
            return;
        }

        var minimum = _settings.MinimumRecordingDiskBytes;
        if (available < minimum)
        {
            if (_diskSpaceLow)
            {
                return;
            }

            _diskSpaceLow = true;
            LocalFileLogger.Error(
                "recording",
                "disk_space_low_while_recording",
                new IOException($"{available} bytes free, below the {minimum} byte minimum."),
                new { availableBytes = available, minimumBytes = minimum });
            PublishStatus(
                "recording_disk_space_low",
                $"The recording drive is low on space ({available / (1024 * 1024)} MB free). " +
                "Free up space now: recording will fail if the drive fills.",
                isDegraded: true);
            return;
        }

        if (!_diskSpaceLow)
        {
            return;
        }

        _diskSpaceLow = false;
        LocalFileLogger.Info(
            "recording",
            "disk_space_recovered",
            new { availableBytes = available, minimumBytes = minimum });
        PublishStatus("recording_disk_space_recovered", "The recording drive has space again.");
    }

    private void OnSegmentCompleted(CompletedSegment segment)
    {
        _committedStreams.Add(segment.StreamType);

        if (_uploadEnabledForAttempt)
        {
            _uploadWorker.NotifyPendingSegment();
        }

        PublishStatus(
            "segment_completed",
            $"Segment {segment.StreamType}/{segment.Sequence:D6} was completed.");
    }

    private void OnScreenRecordingFailed(Exception exception)
    {
        LocalFileLogger.Error("recording", "screen_runtime_failed", exception);
        PublishStatus(
            "screen_recording_failed",
            $"Screen recording stopped because of an error: {exception.Message}",
            isDegraded: true);
    }

    private void OnCameraRecordingFailed(Exception exception)
    {
        LocalFileLogger.Error("recording", "camera_runtime_failed", exception);
        PublishStatus(
            "camera_recording_failed",
            $"Camera recording stopped because of an error: {exception.Message}",
            isDegraded: true);
    }

    public async Task StopAsync(
        RecordingStopReason reason,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(reason, cancellationToken);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopCoreAsync(
        RecordingStopReason reason,
        CancellationToken cancellationToken)
    {
        if (_context is null)
        {
            return;
        }

        _camera.OnCapturedFrame -= OnCameraFrame;
        await StopAudioCaptureAsync();

        try
        {
            await _liveMonitor.StopAsync();
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("recording", "live_monitor_stop_failed", ex);
        }

        var recordingFailed = false;

        if (_startedStreams.Contains(RecordingStreamType.Screen))
        {
            try
            {
                await _screenRecorder.StopAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                recordingFailed = true;
                LocalFileLogger.Error("recording", "screen_stop_failed", ex);
            }
        }

        if (_startedStreams.Contains(RecordingStreamType.Camera))
        {
            try
            {
                await _cameraRecorder.StopAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                recordingFailed = true;
                LocalFileLogger.Error("recording", "camera_stop_failed", ex);
            }
        }

        var allUploaded = false;
        if (_uploadEnabledForAttempt)
        {
            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var drainSeconds = reason == RecordingStopReason.ApplicationShutdown
                ? 2
                : Math.Max(1, _settings.RecordingFinalDrainSeconds);
            drainCts.CancelAfter(TimeSpan.FromSeconds(drainSeconds));
            // Best-effort combined wait so a fast stream doesn't have to poll on its own while a
            // slower sibling is still draining. Whether this times out or not, each stream below
            // is still checked and completed independently -- camera and screen must not share a
            // single pass/fail gate: one stream stuck retrying (or, as with camera when the local
            // device never opened, having nothing to upload at all) must not stop a perfectly
            // finished sibling stream from ever calling /complete and getting assembled.
            await _uploadWorker.WaitUntilIdleAsync(drainCts.Token);

            // The final declaration, marked complete: from here on a missing tail is a real gap
            // rather than just the next segment not existing yet. Sent before /complete so the
            // server already knows the expected set when it assembles.
            await DeclareInventoriesAsync(complete: true, cancellationToken);

            var completedStreams = new List<bool>();
            foreach (var pair in _uploadSessions.Where(pair =>
                         _startedStreams.Contains(pair.Key)))
            {
                var streamId = pair.Value.StreamId;

                if (!_committedStreams.Contains(pair.Key))
                {
                    // Outstanding == 0 here would be trivially true too -- nothing was ever
                    // produced for this stream, so there is nothing to wait on locally. But calling
                    // /complete for it tells vox-streaming to assemble a stream with zero segments,
                    // which its Kafka consumer retries for a very long time before giving up. Skip
                    // it instead: there is nothing to assemble either way.
                    completedStreams.Add(false);
                    LocalFileLogger.Info("recording", "complete_skipped_no_segments", new { streamId });
                    continue;
                }

                var outstanding = await _store.GetOutstandingCountAsync(
                    new HashSet<string> { streamId }, cancellationToken);
                if (outstanding > 0)
                {
                    completedStreams.Add(false);
                    // Loud on purpose. Skipping /complete here means vox-streaming is never asked to
                    // assemble this stream, so however many segments already reached S3 stay there
                    // as loose parts with no recording.mp4 and nothing to trigger one later. That
                    // happened once for a whole camera recording -- one straggling final segment --
                    // and left no trace in any log, which is why it took an S3 inspection to find.
                    LocalFileLogger.Error(
                        "recording",
                        "complete_skipped_segments_outstanding",
                        new InvalidOperationException(
                            $"{outstanding} segment(s) still pending after the {drainSeconds}s drain; " +
                            "this stream will not be assembled."),
                        new { streamId, streamType = pair.Key.ToString(), outstanding, drainSeconds });
                    continue;
                }

                await AuditBeforeCompleteAsync(pair.Key, streamId, pair.Value.UploadToken, cancellationToken);

                try
                {
                    await _sessionClient.CompleteAsync(
                        streamId,
                        pair.Value.UploadToken,
                        cancellationToken);
                    completedStreams.Add(true);
                    // Recorded so a later run skips this stream instead of re-completing a stream
                    // vox-streaming has already finalized.
                    await _store.MarkUploadSessionCompletedAsync(streamId, cancellationToken);
                }
                catch (Exception ex)
                {
                    completedStreams.Add(false);
                    LocalFileLogger.Error(
                        "recording",
                        "upload_session_complete_failed",
                        ex,
                        new { streamId });
                }
            }

            allUploaded = completedStreams.Count > 0 && completedStreams.All(completed => completed);
        }

        _screenRecorder.SegmentCompleted -= OnSegmentCompleted;
        _cameraRecorder.SegmentCompleted -= OnSegmentCompleted;
        _screenRecorder.RecordingFailed -= OnScreenRecordingFailed;
        _cameraRecorder.RecordingFailed -= OnCameraRecordingFailed;
        _recordingWatchdog?.Dispose();
        _recordingWatchdog = null;
        _diskSpaceLow = false;
        _clock.Stop();

        if (_uploadEnabledForAttempt && !allUploaded)
        {
            PublishStatus(
                "segments_pending",
                "Some segments are still pending and remain stored on this device.",
                isDegraded: true);
        }
        else if (!_uploadEnabledForAttempt)
        {
            PublishStatus(
                "segments_saved_locally",
                _settings.EnableSegmentUpload
                    ? "Segments remain on this device because the upload session is unavailable."
                    : "Segments were stored locally because upload is disabled in configuration.",
                isDegraded: _settings.EnableSegmentUpload);
        }

        if (recordingFailed)
        {
            PublishStatus(
                "recording_stopped_with_errors",
                "Recording stopped, but at least one capture source reported an error.",
                isDegraded: true);
        }
        else
        {
            PublishStatus(
                "recording_stopped",
                $"Recording stopped ({reason}).");
        }

        _uploadSessions.Clear();
        _startedStreams.Clear();
        _committedStreams.Clear();
        _context = null;
        _uploadEnabledForAttempt = false;
        IsRecording = false;
    }

    /// <summary>
    /// Cross-checks vox-streaming's own segment coverage right before /complete, instead of only
    /// finding out about gaps minutes later from the async assembly event. Purely informational:
    /// bounded by its own short timeout so a slow/unreachable server never delays or skips the
    /// /complete call below, and any failure here is swallowed -- this is a best-effort early
    /// warning, not a gate.
    /// </summary>
    private async Task AuditBeforeCompleteAsync(
        RecordingStreamType streamType,
        string streamId,
        string uploadToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var auditCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            auditCts.CancelAfter(TimeSpan.FromSeconds(_settings.RecordingAuditTimeoutSeconds));
            var audit = await _sessionClient.AuditAsync(streamId, uploadToken, auditCts.Token);

            if (audit.HasGaps)
            {
                LocalFileLogger.Info(
                    "recording",
                    "segment_audit_gaps_detected",
                    new { streamId, streamType, audit.TotalSegments, gapCount = audit.Gaps.Count });
                PublishStatus(
                    "recording_incomplete",
                    $"The {streamType} recording may be missing some segments (server-side audit found gaps).",
                    isDegraded: true);
            }
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("recording", "segment_audit_failed", ex, new { streamId, streamType });
        }
    }

    private void PublishStatus(string code, string message, bool isDegraded = false)
    {
        try
        {
            StatusChanged?.Invoke(new RecordingStatus(code, message, isDegraded));
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("recording", "status_consumer_failed", ex, new { code });
        }
    }

    private void ValidateContext(RecordingSessionContext context)
    {
        if (context.AttemptId == Guid.Empty ||
            string.IsNullOrWhiteSpace(context.SessionId) ||
            context.StreamTypes.Count == 0)
        {
            throw new InvalidOperationException("Recording context is incomplete.");
        }

        if (_settings.EnableSegmentUpload &&
            (string.IsNullOrWhiteSpace(context.ScheduleId) ||
             context.ScheduleId == "local" ||
             string.IsNullOrWhiteSpace(context.StreamToken)))
        {
            throw new InvalidOperationException(
                "Streaming context is incomplete while segment upload is enabled.");
        }
    }

    private static string ToWireValue(RecordingStreamType streamType) =>
        streamType == RecordingStreamType.Camera ? "camera" : "screen";

    public async Task ShutdownAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _uploadWorker.DisposeAsync();
        _lifecycleGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        // Fallback path: StartAsync/StopAsync's caller (ExamViewModel/StreamingDemoViewModel's
        // Window.Closing cleanup) should already have called StopAsync then ShutdownAsync directly.
        // This only does real work if that didn't happen for some reason (e.g. the DI container
        // disposing this singleton on ServiceProvider teardown without the window cleanup having
        // run) -- the _disposed guard above makes calling both orders safe.
        await StopAsync(RecordingStopReason.ApplicationShutdown, CancellationToken.None);
        await ShutdownAsync();
    }
}
