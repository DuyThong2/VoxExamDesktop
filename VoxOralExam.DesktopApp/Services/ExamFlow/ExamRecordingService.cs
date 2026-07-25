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

    public event Action<RecordingStatus>? StatusChanged;

    public bool IsRecording { get; private set; }

    public ExamRecordingService(
        AppSettings settings,
        ExamSessionState sessionState,
        StreamSessionClient sessionClient,
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
