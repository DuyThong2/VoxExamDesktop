using VoxOralExam.Core.Interfaces;
using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Infra.Clients.StreamService;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.Infra.Recording;
using VoxOralExam.DesktopApp.Infra.Recording.Storage;
using VoxOralExam.DesktopApp.State;
using VoxOralExam.DesktopApp.Workers;

namespace VoxOralExam.DesktopApp.Services.ExamFlow;

public sealed class ExamRecordingService : IExamRecordingService, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly StreamSessionClient _sessionClient;
    private readonly SegmentUploadWorker _uploadWorker;
    private readonly LocalSegmentStore _store;
    private readonly RecordingClock _clock;
    private readonly ScreenSegmentRecorder _screenRecorder;
    private readonly CameraSegmentRecorder _cameraRecorder;
    private readonly CameraService _camera;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Dictionary<RecordingStreamType, StreamUploadSession> _uploadSessions = [];
    private readonly HashSet<RecordingStreamType> _startedStreams = [];

    private RecordingSessionContext? _context;
    private bool _uploadEnabledForAttempt;
    private bool _disposed;

    public event Action<RecordingStatus>? StatusChanged;

    public bool IsRecording { get; private set; }

    public ExamRecordingService(
        AppSettings settings,
        StreamSessionClient sessionClient,
        SegmentUploadWorker uploadWorker,
        LocalSegmentStore store,
        RecordingClock clock,
        ScreenSegmentRecorder screenRecorder,
        CameraSegmentRecorder cameraRecorder,
        CameraService camera)
    {
        _settings = settings;
        _sessionClient = sessionClient;
        _uploadWorker = uploadWorker;
        _store = store;
        _clock = clock;
        _screenRecorder = screenRecorder;
        _cameraRecorder = cameraRecorder;
        _camera = camera;
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
            _uploadSessions.Clear();
            _uploadEnabledForAttempt = _settings.EnableSegmentUpload;
            _clock.Start();

            _screenRecorder.SegmentCompleted += OnSegmentCompleted;
            _cameraRecorder.SegmentCompleted += OnSegmentCompleted;
            _screenRecorder.RecordingFailed += OnScreenRecordingFailed;
            _cameraRecorder.RecordingFailed += OnCameraRecordingFailed;

            var streamIds = await CreateStreamIdsAsync(context, cancellationToken);

            if (context.StreamTypes.Contains(RecordingStreamType.Screen))
            {
                try
                {
                    await _screenRecorder.StartAsync(
                        streamIds[RecordingStreamType.Screen],
                        cancellationToken);
                    _startedStreams.Add(RecordingStreamType.Screen);
                }
                catch (Exception ex)
                {
                    LocalFileLogger.Error("recording", "screen_start_failed", ex);
                    PublishStatus(
                        "screen_recording_unavailable",
                        $"Không thể ghi màn hình: {ex.Message}",
                        isDegraded: true);
                }
            }

            if (context.StreamTypes.Contains(RecordingStreamType.Camera))
            {
                try
                {
                    await _cameraRecorder.StartAsync(
                        streamIds[RecordingStreamType.Camera],
                        cancellationToken);
                    _camera.OnCapturedFrame += OnCameraFrame;
                    _startedStreams.Add(RecordingStreamType.Camera);
                }
                catch (Exception ex)
                {
                    LocalFileLogger.Error("recording", "camera_start_failed", ex);
                    PublishStatus(
                        "camera_recording_unavailable",
                        $"Không thể ghi camera: {ex.Message}",
                        isDegraded: true);
                }
            }

            if (_startedStreams.Count == 0)
            {
                await StopCoreAsync(RecordingStopReason.CaptureFailure, CancellationToken.None);
                throw new InvalidOperationException("No local recording source could be started.");
            }

            if (_uploadEnabledForAttempt)
            {
                _uploadWorker.Start(context.StreamToken);
            }

            IsRecording = true;
            PublishStatus("recording_started", "Ghi hình cục bộ đã bắt đầu.");
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
                    "Không thể mở phiên upload; segment vẫn được giữ an toàn trên máy.",
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

    private void OnSegmentCompleted(CompletedSegment segment)
    {
        if (_uploadEnabledForAttempt)
        {
            _uploadWorker.NotifyPendingSegment();
        }

        PublishStatus(
            "segment_completed",
            $"Đã hoàn tất segment {segment.StreamType}/{segment.Sequence:D6}.");
    }

    private void OnScreenRecordingFailed(Exception exception)
    {
        LocalFileLogger.Error("recording", "screen_runtime_failed", exception);
        PublishStatus(
            "screen_recording_failed",
            $"Ghi màn hình đã dừng do lỗi: {exception.Message}",
            isDegraded: true);
    }

    private void OnCameraRecordingFailed(Exception exception)
    {
        LocalFileLogger.Error("recording", "camera_runtime_failed", exception);
        PublishStatus(
            "camera_recording_failed",
            $"Ghi camera đã dừng do lỗi: {exception.Message}",
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
            allUploaded = await _uploadWorker.WaitUntilIdleAsync(drainCts.Token);

            if (allUploaded)
            {
                foreach (var pair in _uploadSessions.Where(pair =>
                             _startedStreams.Contains(pair.Key)))
                {
                    try
                    {
                        await _sessionClient.CompleteAsync(
                            pair.Value.StreamId,
                            _context.StreamToken,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        allUploaded = false;
                        LocalFileLogger.Error(
                            "recording",
                            "upload_session_complete_failed",
                            ex,
                            new { pair.Value.StreamId });
                    }
                }
            }
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
                "Một số segment chưa upload xong và vẫn được giữ trên máy.",
                isDegraded: true);
        }
        else if (!_uploadEnabledForAttempt)
        {
            PublishStatus(
                "segments_saved_locally",
                _settings.EnableSegmentUpload
                    ? "Các segment đã được giữ trên máy do phiên upload chưa khả dụng."
                    : "Các segment đã được lưu cục bộ; upload đang tắt trong cấu hình.",
                isDegraded: _settings.EnableSegmentUpload);
        }

        if (recordingFailed)
        {
            PublishStatus(
                "recording_stopped_with_errors",
                "Ghi hình đã dừng nhưng có nguồn ghi gặp lỗi.",
                isDegraded: true);
        }
        else
        {
            PublishStatus(
                "recording_stopped",
                $"Ghi hình đã dừng ({reason}).");
        }

        _uploadSessions.Clear();
        _startedStreams.Clear();
        _context = null;
        _uploadEnabledForAttempt = false;
        IsRecording = false;
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(RecordingStopReason.ApplicationShutdown, CancellationToken.None);
        _disposed = true;
        _lifecycleGate.Dispose();
    }
}
