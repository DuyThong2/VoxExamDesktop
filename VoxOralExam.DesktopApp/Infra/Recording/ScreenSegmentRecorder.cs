using System.Collections.Concurrent;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using Windows.Graphics.DirectX.Direct3D11;
using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Infra.Recording.Capture;
using VoxOralExam.DesktopApp.Infra.Recording.Encoding;
using VoxOralExam.DesktopApp.Infra.Recording.Interop;
using VoxOralExam.DesktopApp.Infra.Recording.Storage;
using VoxOralExam.DesktopApp.State;
using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Infra.Recording;

public sealed class ScreenSegmentRecorder : IDisposable
{
    private sealed record FrameItem(ID3D11Texture2D Texture, TimeSpan Timestamp) : IDisposable
    {
        public void Dispose() => Texture.Dispose();
    }

    private readonly AppSettings _settings;
    private readonly LocalSegmentStore _store;
    private readonly RecordingClock _clock;
    private readonly object _contextLock = new();

    private BlockingCollection<FrameItem>? _queue;
    private Thread? _encodeThread;
    private ID3D11Device? _device;
    private IDirect3DDevice? _winRtDevice;
    private ScreenCaptureSource? _capture;
    private VideoSegmentWriter? _writer;
    private Exception? _fatalError;
    private string _streamId = string.Empty;
    private long _sequence;
    private int _framesInSegment;
    private TimeSpan _segmentStart;
    private DateTimeOffset _segmentStartedAtUtc;
    private volatile bool _acceptFrames;
    private long _lastEnqueuedTimestampTicks;
    private bool _mediaFoundationAcquired;
    private bool _started;

    public event Action<CompletedSegment>? SegmentCompleted;

    public event Action<Exception>? RecordingFailed;

    public ScreenSegmentRecorder(
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
        _lastEnqueuedTimestampTicks = -TimeSpan.TicksPerSecond /
            Math.Clamp(_settings.ScreenRecordingFps, 1, 60);
        _queue = new BlockingCollection<FrameItem>(
            new ConcurrentQueue<FrameItem>(),
            Math.Max(2, _settings.RecordingQueueCapacity));

        try
        {
            MediaFoundationRuntime.Acquire();
            _mediaFoundationAcquired = true;

            (_device, _winRtDevice) = Direct3D11Interop.CreateSharedDevice();
            _capture = new ScreenCaptureSource(
                _device,
                _winRtDevice,
                _clock,
                _contextLock);
            _capture.FrameArrived += OnFrameArrived;
            _capture.CaptureFailed += OnCaptureFailed;
            var info = _capture.Initialize();

            _segmentStart = _clock.Elapsed;
            _segmentStartedAtUtc = _clock.ToUtc(_segmentStart);
            _writer = CreateWriter(
                EnsureEven(info.Width),
                EnsureEven(info.Height),
                _sequence);

            _encodeThread = new Thread(EncodeLoop)
            {
                IsBackground = true,
                Name = "ScreenSegmentEncoder"
            };
            _acceptFrames = true;
            _encodeThread.Start();
            _capture.Start();
            _started = true;
            return Task.CompletedTask;
        }
        catch
        {
            CleanupResources();
            throw;
        }
    }

    private void OnFrameArrived(ID3D11Texture2D texture, TimeSpan timestamp)
    {
        var item = new FrameItem(texture, timestamp);
        try
        {
            var frameInterval = TimeSpan.TicksPerSecond /
                Math.Clamp(_settings.ScreenRecordingFps, 1, 60);
            while (true)
            {
                var previous = Interlocked.Read(ref _lastEnqueuedTimestampTicks);
                if (timestamp.Ticks - previous < frameInterval)
                {
                    item.Dispose();
                    return;
                }

                if (Interlocked.CompareExchange(
                        ref _lastEnqueuedTimestampTicks,
                        timestamp.Ticks,
                        previous) == previous)
                {
                    break;
                }
            }

            if (!_acceptFrames || _queue is null || !_queue.TryAdd(item))
            {
                item.Dispose();
            }
        }
        catch (InvalidOperationException)
        {
            item.Dispose();
        }
    }

    private void OnCaptureFailed(Exception exception)
    {
        _fatalError ??= exception;
        _acceptFrames = false;
        InvokeRecordingFailed(exception);
    }

    private void EncodeLoop()
    {
        try
        {
            foreach (var frame in _queue!.GetConsumingEnumerable())
            {
                using (frame)
                {
                    if (_framesInSegment == 0)
                    {
                        _segmentStart = frame.Timestamp;
                        _segmentStartedAtUtc = _clock.ToUtc(frame.Timestamp);
                    }

                    RotateIfNeeded(frame.Timestamp);
                    _writer!.WriteTexture(
                        frame.Texture,
                        frame.Timestamp - _segmentStart);
                    _framesInSegment++;
                }
            }
        }
        catch (Exception ex)
        {
            _fatalError = ex;
            _acceptFrames = false;
            InvokeRecordingFailed(ex);
        }
        finally
        {
            while (_queue!.TryTake(out var remaining))
            {
                remaining.Dispose();
            }
        }
    }

    private void RotateIfNeeded(TimeSpan timestamp)
    {
        var duration = TimeSpan.FromSeconds(
            Math.Max(1, _settings.RecordingSegmentSeconds));
        if (_framesInSegment == 0 || timestamp - _segmentStart < duration)
        {
            return;
        }

        CompleteCurrentSegment(_clock.ToUtc(timestamp));
        _sequence++;
        _segmentStart = timestamp;
        _segmentStartedAtUtc = _clock.ToUtc(timestamp);
        var description = _captureSizeFromWriter;
        _writer = CreateWriter(description.Width, description.Height, _sequence);
    }

    private (int Width, int Height) _captureSizeFromWriter;

    private VideoSegmentWriter CreateWriter(int width, int height, long sequence)
    {
        _captureSizeFromWriter = (width, height);
        return new VideoSegmentWriter(
            _store.CreatePartialPath(RecordingStreamType.Screen, _streamId, sequence),
            width,
            height,
            Math.Clamp(_settings.ScreenRecordingFps, 1, 60),
            Math.Max(250_000, _settings.ScreenRecordingBitrate),
            _device,
            _contextLock);
    }

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
                    RecordingStreamType.Screen,
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
        _capture?.Stop();
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
            throw new InvalidOperationException("Screen recording failed.", fatalError);
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
            LocalFileLogger.Error("screen_recording", "segment_callback_failed", ex);
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
            LocalFileLogger.Error("screen_recording", "failure_callback_failed", ex);
        }
    }

    private void CleanupResources()
    {
        if (_capture is not null)
        {
            _capture.FrameArrived -= OnFrameArrived;
            _capture.CaptureFailed -= OnCaptureFailed;
            _capture.Dispose();
            _capture = null;
        }

        _writer?.Abort();
        _writer = null;
        (_winRtDevice as IDisposable)?.Dispose();
        _winRtDevice = null;
        _device?.Dispose();
        _device = null;
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
            // A zero-frame partial is safe to leave for startup cleanup.
        }
    }

    private static int EnsureEven(int value) => Math.Max(2, value & ~1);

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();
}
