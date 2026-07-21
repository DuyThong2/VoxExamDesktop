using System.Collections.Concurrent;
using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.Infra.Recording.Encoding;
using VoxOralExam.DesktopApp.Infra.Recording.Storage;
using VoxOralExam.DesktopApp.State;
using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Infra.Recording;

public sealed class CameraSegmentRecorder : IDisposable
{
    private readonly AppSettings _settings;
    private readonly LocalSegmentStore _store;
    private readonly RecordingClock _clock;

    private BlockingCollection<CameraFrame>? _queue;
    private Thread? _encodeThread;
    private VideoSegmentWriter? _writer;
    private Exception? _fatalError;
    private string _streamId = string.Empty;
    private long _sequence;
    private int _framesInSegment;
    private int _width;
    private int _height;
    private TimeSpan _segmentStart;
    private DateTimeOffset _segmentStartedAtUtc;
    private volatile bool _acceptFrames;
    private bool _mediaFoundationAcquired;
    private bool _started;

    public event Action<CompletedSegment>? SegmentCompleted;

    public event Action<Exception>? RecordingFailed;

    public CameraSegmentRecorder(
        AppSettings settings,
        LocalSegmentStore store,
        RecordingClock clock)
    {
        _settings = settings;
        _store = store;
        _clock = clock;
    }

    public Task StartAsync(string streamId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_started)
        {
            return Task.CompletedTask;
        }

        _streamId = streamId;
        _sequence = 0;
        _framesInSegment = 0;
        _fatalError = null;
        _width = 0;
        _height = 0;
        _queue = new BlockingCollection<CameraFrame>(
            new ConcurrentQueue<CameraFrame>(),
            Math.Max(2, _settings.RecordingQueueCapacity));
        MediaFoundationRuntime.Acquire();
        _mediaFoundationAcquired = true;
        _encodeThread = new Thread(EncodeLoop)
        {
            IsBackground = true,
            Name = "CameraSegmentEncoder"
        };
        _acceptFrames = true;
        _encodeThread.Start();
        _started = true;
        return Task.CompletedTask;
    }

    public bool TryEnqueue(CameraFrame frame)
    {
        try
        {
            return _acceptFrames && _queue is not null && _queue.TryAdd(frame);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void EncodeLoop()
    {
        try
        {
            foreach (var frame in _queue!.GetConsumingEnumerable())
            {
                EnsureWriter(frame);
                RotateIfNeeded(frame);
                _writer!.WriteBgr24(
                    frame.Data,
                    frame.Width,
                    frame.Height,
                    frame.Stride,
                    frame.Timestamp - _segmentStart);
                _framesInSegment++;
            }
        }
        catch (Exception ex)
        {
            _fatalError = ex;
            _acceptFrames = false;
            InvokeRecordingFailed(ex);
        }
    }

    private void EnsureWriter(CameraFrame frame)
    {
        if (_writer is not null)
        {
            return;
        }

        _width = EnsureEven(frame.Width);
        _height = EnsureEven(frame.Height);
        _segmentStart = frame.Timestamp;
        _segmentStartedAtUtc = _clock.ToUtc(frame.Timestamp);
        _writer = CreateWriter(_sequence);
    }

    private void RotateIfNeeded(CameraFrame frame)
    {
        var duration = TimeSpan.FromSeconds(
            Math.Max(1, _settings.RecordingSegmentSeconds));
        if (_framesInSegment == 0 || frame.Timestamp - _segmentStart < duration)
        {
            return;
        }

        CompleteCurrentSegment(_clock.ToUtc(frame.Timestamp));
        _sequence++;
        _segmentStart = frame.Timestamp;
        _segmentStartedAtUtc = _clock.ToUtc(frame.Timestamp);
        _writer = CreateWriter(_sequence);
    }

    private VideoSegmentWriter CreateWriter(long sequence) => new(
        _store.CreatePartialPath(RecordingStreamType.Camera, _streamId, sequence),
        _width,
        _height,
        Math.Clamp(_settings.CameraFps, 1, 60),
        Math.Max(250_000, _settings.CameraRecordingBitrate));

    private void CompleteCurrentSegment(DateTimeOffset endedAtUtc)
    {
        var writer = _writer;
        _writer = null;
        if (writer is null)
        {
            return;
        }

        var path = writer.OutputPath;
        try
        {
            if (_framesInSegment == 0)
            {
                writer.Abort();
                TryDelete(path);
                return;
            }

            writer.Complete();
            writer.Dispose();

            var segment = _store.CommitAsync(
                    _streamId,
                    RecordingStreamType.Camera,
                    _sequence,
                    path,
                    _segmentStartedAtUtc,
                    endedAtUtc)
                .GetAwaiter()
                .GetResult();
            InvokeSegmentCompleted(segment);
        }
        catch
        {
            writer.Abort();
            throw;
        }
        finally
        {
            _framesInSegment = 0;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (!_started && !_mediaFoundationAcquired)
        {
            return;
        }

        _acceptFrames = false;
        _queue?.CompleteAdding();
        if (_encodeThread is not null)
        {
            await Task.Run(() => _encodeThread.Join(), CancellationToken.None);
        }

        Exception? completionError = null;
        try
        {
            CompleteCurrentSegment(_clock.ToUtc(_clock.Elapsed));
        }
        catch (Exception ex)
        {
            completionError = ex;
        }

        var fatalError = _fatalError;
        CleanupResources();

        if (completionError is not null)
        {
            throw completionError;
        }

        if (fatalError is not null)
        {
            throw new InvalidOperationException("Camera recording failed.", fatalError);
        }
    }

    private void InvokeSegmentCompleted(CompletedSegment segment)
    {
        try
        {
            SegmentCompleted?.Invoke(segment);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("camera_recording", "segment_callback_failed", ex);
        }
    }

    private void InvokeRecordingFailed(Exception exception)
    {
        try
        {
            RecordingFailed?.Invoke(exception);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("camera_recording", "failure_callback_failed", ex);
        }
    }

    private void CleanupResources()
    {
        _writer?.Abort();
        _writer = null;
        _queue?.Dispose();
        _queue = null;
        _encodeThread = null;
        if (_mediaFoundationAcquired)
        {
            MediaFoundationRuntime.Release();
            _mediaFoundationAcquired = false;
        }

        _started = false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static int EnsureEven(int value) => Math.Max(2, value & ~1);

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();
}
