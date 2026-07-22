using System.Collections.Concurrent;
using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.Infra.Recording.Audio;
using VoxOralExam.DesktopApp.Infra.Recording.VideoEncoding;
using VoxOralExam.DesktopApp.Infra.Recording.Storage;
using VoxOralExam.DesktopApp.State;
using VoxOralExam.DesktopApp.Services;
using System.IO;

namespace VoxOralExam.DesktopApp.Infra.Recording;

public sealed class CameraSegmentRecorder : IDisposable
{
    private abstract record QueueItem(TimeSpan Timestamp);

    private sealed record VideoQueueItem(CameraFrame Frame) : QueueItem(Frame.Timestamp);

    private sealed record AudioQueueItem(byte[] Pcm, TimeSpan Timestamp) : QueueItem(Timestamp);

    private readonly AppSettings _settings;
    private readonly LocalSegmentStore _store;
    private readonly RecordingClock _clock;

    private BlockingCollection<QueueItem>? _queue;
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

    // Set once per StartAsync call by ExamRecordingService, based on whether its mic capture
    // actually opened -- see ScreenSegmentRecorder's identical field for why this isn't just always
    // true.
    private bool _includeAudio;

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

    public Task StartAsync(
        string streamId,
        long initialSequence,
        CancellationToken ct,
        bool includeAudio = false)
    {
        ct.ThrowIfCancellationRequested();
        if (_started)
        {
            return Task.CompletedTask;
        }

        _streamId = streamId;
        _sequence = initialSequence;
        _framesInSegment = 0;
        _fatalError = null;
        _includeAudio = includeAudio;
        _width = 0;
        _height = 0;
        _queue = new BlockingCollection<QueueItem>(
            new ConcurrentQueue<QueueItem>(),
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
            return _acceptFrames && _queue is not null && _queue.TryAdd(new VideoQueueItem(frame));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Called by ExamRecordingService with the exam mic's raw PCM chunks (no mixing -- Camera only
    /// ever gets mic, unlike Screen's mic+system-audio mix, see AudioMixer). Routed through the same
    /// queue/thread that owns _writer so an audio write never races a video-frame-triggered segment
    /// rotation swapping _writer out underneath it.
    /// </summary>
    public void EnqueueAudio(byte[] pcm, TimeSpan timestamp)
    {
        try
        {
            if (!_acceptFrames || _queue is null)
            {
                return;
            }

            _queue.TryAdd(new AudioQueueItem(pcm, timestamp));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void EncodeLoop()
    {
        try
        {
            foreach (var item in _queue!.GetConsumingEnumerable())
            {
                switch (item)
                {
                    case VideoQueueItem video:
                        var frame = video.Frame;
                        EnsureWriter(frame);
                        RotateIfNeeded(frame);
                        _writer!.WriteBgr24(
                            frame.Data,
                            frame.Width,
                            frame.Height,
                            frame.Stride,
                            frame.Timestamp - _segmentStart);
                        _framesInSegment++;
                        break;

                    case AudioQueueItem audio:
                        // _writer is null until the first camera frame (EnsureWriter creates it
                        // lazily) -- any audio arriving before that is dropped, same narrow
                        // recording-start-only window as ScreenSegmentRecorder's
                        // _firstVideoFrameSeen guard.
                        if (_writer is { SupportsAudio: true } writer)
                        {
                            writer.WriteAudio(audio.Pcm, audio.Timestamp - _segmentStart);
                        }

                        break;
                }
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
        Math.Max(250_000, _settings.CameraRecordingBitrate),
        audioSampleRate: _includeAudio ? AudioMixer.TargetSampleRate : null);

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

        // See ScreenSegmentRecorder.StopAsync's comment on the same pattern: CompleteCurrentSegment
        // ends in a blocking GetAwaiter().GetResult() over CommitAsync, which awaits real async I/O
        // while holding LocalSegmentStore's gate. Running that inline here would resume on the UI
        // thread (via the WPF SynchronizationContext captured by the await above) and deadlock
        // against CommitAsync's own continuation needing that same, now-blocked, thread.
        Exception? completionError = null;
        await Task.Run(() =>
        {
            try
            {
                CompleteCurrentSegment(_clock.ToUtc(_clock.Elapsed));
            }
            catch (Exception ex)
            {
                completionError = ex;
            }
        }, CancellationToken.None);

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
