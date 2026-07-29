using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoxOralExam.DesktopApp.Infra.Recording;
using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Infra.Recording.Audio;

/// <summary>
/// Combines the exam mic (fixed 16kHz/16-bit/mono, matching TurnAudioRecorder's capture format)
/// with system/loopback audio (whatever the default playback device's native mix format happens to
/// be -- typically 44.1/48kHz float stereo, not under this app's control) into a single mono 16kHz
/// PCM16 stream for Screen's recorded audio track. Runs on its own timer tick, independent of both
/// capture devices' callback threads -- BufferedWaveProvider.ReadFully absorbs the jitter between
/// "when audio actually arrives" and "when this mixer reads it".
///
/// Loopback is optional: if it never gets enabled (see EnableLoopback) -- e.g. because
/// SystemAudioLoopbackCapture failed to open the default playback device -- this mixer degrades to
/// forwarding mic audio alone, by design (see the "loopback failure should degrade, not roll back
/// the whole recording" decision).
/// </summary>
public sealed class AudioMixer : IDisposable
{
    public const int TargetSampleRate = 16_000;
    private const int TickMilliseconds = 20;

    /// <summary>
    /// Upper bound on chunks emitted per tick, i.e. how fast OnTick may work off a backlog. Five
    /// chunks is 100ms of audio, so the mixer can run five times real time while catching up --
    /// enough to clear the two-second buffer in under half a second -- without handing the segment
    /// writer an unbounded burst if something pauses the timer for a long time.
    /// </summary>
    private const int MaxChunksPerTick = 5;

    private readonly RecordingClock _clock;
    private readonly BufferedWaveProvider _micBuffer;
    private readonly int _bytesPerTick;
    private BufferedWaveProvider? _loopbackRawBuffer;
    private IWaveProvider? _loopbackResampled;
    private Timer? _timer;
    private volatile bool _loopbackEnabled;
    private int _captureFaultLogged;

    public event Action<byte[], TimeSpan>? MixedAudioAvailable;

    public AudioMixer(RecordingClock clock)
    {
        _clock = clock;
        var micFormat = new WaveFormat(TargetSampleRate, 16, 1);
        _micBuffer = new BufferedWaveProvider(micFormat)
        {
            ReadFully = true,
            BufferDuration = TimeSpan.FromSeconds(2),
            // Without this NAudio throws InvalidOperationException("Buffer full") from AddSamples --
            // which runs on the capture device's own callback thread, where nothing catches it and
            // an unhandled exception takes the whole app down mid-exam. Losing the oldest fraction
            // of a second of audio is not a good outcome, but it is not remotely comparable to
            // ending the candidate's exam. See OnTick for why the buffer can fill at all.
            DiscardOnBufferOverflow = true
        };
        _bytesPerTick = micFormat.AverageBytesPerSecond * TickMilliseconds / 1000;
    }

    /// <summary>
    /// Wires in system audio. Safe to call before Start(); must not be called after a failed
    /// SystemAudioLoopbackCapture.StartAsync() -- callers should simply skip this call in that case
    /// so the mixer stays in mic-only mode.
    /// </summary>
    public void EnableLoopback(WaveFormat loopbackFormat)
    {
        _loopbackRawBuffer = new BufferedWaveProvider(loopbackFormat)
        {
            ReadFully = true,
            BufferDuration = TimeSpan.FromSeconds(2),
            // Same reason as _micBuffer's, and the same callback-thread consequence.
            DiscardOnBufferOverflow = true
        };

        ISampleProvider sample = _loopbackRawBuffer.ToSampleProvider();
        sample = loopbackFormat.Channels switch
        {
            1 => sample,
            2 => sample.ToMono(),
            _ => throw new NotSupportedException(
                $"Unsupported loopback channel count: {loopbackFormat.Channels}.")
        };

        if (loopbackFormat.SampleRate != TargetSampleRate)
        {
            sample = new WdlResamplingSampleProvider(sample, TargetSampleRate);
        }

        _loopbackResampled = sample.ToWaveProvider16();
        _loopbackEnabled = true;
    }

    public void Start()
    {
        _timer ??= new Timer(OnTick, null, TickMilliseconds, TickMilliseconds);
    }

    // Both of these run on their capture device's callback thread, where an escaping exception is
    // unhandled and terminates the process. DiscardOnBufferOverflow above already removes the one
    // throw that was actually reachable; these exist so that no future change to NAudio or to the
    // buffer configuration can turn a recoverable audio fault back into a dead exam. Logged once so
    // a fault that repeats every 20ms cannot itself become the problem.
    public void AddMicSamples(byte[] pcm)
    {
        try
        {
            ReportIfOverflowing(_micBuffer, pcm.Length, "mic");
            _micBuffer.AddSamples(pcm, 0, pcm.Length);
        }
        catch (Exception ex)
        {
            LogCaptureFaultOnce("mic_samples_rejected", ex);
        }
    }

    public void AddLoopbackSamples(byte[] raw)
    {
        try
        {
            var buffer = _loopbackRawBuffer;
            if (buffer is null)
            {
                return;
            }
            ReportIfOverflowing(buffer, raw.Length, "loopback");
            buffer.AddSamples(raw, 0, raw.Length);
        }
        catch (Exception ex)
        {
            LogCaptureFaultOnce("loopback_samples_rejected", ex);
        }
    }

    /// <summary>
    /// Logs the overflow that DiscardOnBufferOverflow is about to swallow.
    /// </summary>
    /// <remarks>
    /// Turning the crash into a silent discard fixed the freeze but would otherwise have made
    /// audio loss invisible, which for an oral exam is the worse of the two failures: a frozen app
    /// is at least noticed while the candidate is still in the room. NAudio reports nothing when it
    /// drops, so the condition has to be checked before handing the samples over.
    ///
    /// Reaching here at all means OnTick's catch-up could not keep pace, so it is worth an entry
    /// even though the recording continues.
    /// </remarks>
    private void ReportIfOverflowing(BufferedWaveProvider buffer, int incomingBytes, string source)
    {
        if (buffer.BufferedBytes + incomingBytes <= buffer.BufferLength)
        {
            return;
        }

        LogCaptureFaultOnce(
            "audio_buffer_overflow_discarding",
            new InvalidOperationException(
                $"{source} buffer full ({buffer.BufferedBytes}/{buffer.BufferLength} bytes); " +
                "the oldest audio is being discarded because the mixer tick fell behind real time."));
    }

    private void LogCaptureFaultOnce(string @event, Exception ex)
    {
        if (Interlocked.Exchange(ref _captureFaultLogged, 1) == 0)
        {
            LocalFileLogger.Error("audio_mixer", @event, ex);
        }
    }

    /// <summary>
    /// Drains one tick's worth of audio, plus whatever backlog has built up.
    /// </summary>
    /// <remarks>
    /// The catch-up loop is the whole point. Capture devices produce at exactly real time, but this
    /// runs on a <see cref="Timer"/> whose 20ms period is below Windows' default timer resolution
    /// and whose callbacks are queued on the thread pool -- so ticks arrive late whenever the pool
    /// is busy, and a burst of failing HTTP uploads is more than enough to do that.
    ///
    /// Draining exactly one chunk per tick made every late tick a permanent debt, because
    /// ReadFully pads a short read with silence instead of leaving the backlog to be picked up
    /// next time. That gave the entire recording session a fixed budget of BufferDuration -- two
    /// seconds, a hundred ticks -- of accumulated lateness, after which the buffer filled and
    /// AddSamples threw on the capture thread and froze the app. Six minutes of recording is
    /// eighteen thousand ticks; the budget works out to a tenth of a millisecond each, which no
    /// thread-pool timer can hold to.
    /// </remarks>
    private void OnTick(object? state)
    {
        try
        {
            // Bounded so a large backlog is worked off over several ticks rather than emitted as
            // one burst that the segment writer has to absorb at once.
            for (var chunk = 0; chunk < MaxChunksPerTick; chunk++)
            {
                EmitChunk();
                if (_micBuffer.BufferedBytes < _bytesPerTick)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("audio_mixer", "tick_failed", ex);
        }
    }

    private void EmitChunk()
    {
        var micChunk = new byte[_bytesPerTick];
        _micBuffer.Read(micChunk, 0, _bytesPerTick);

        if (!_loopbackEnabled || _loopbackResampled is null)
        {
            MixedAudioAvailable?.Invoke(micChunk, _clock.Elapsed);
            return;
        }

        var loopbackChunk = new byte[_bytesPerTick];
        _loopbackResampled.Read(loopbackChunk, 0, _bytesPerTick);

        var mixed = new byte[_bytesPerTick];
        for (var i = 0; i < _bytesPerTick; i += 2)
        {
            var micSample = (short)(micChunk[i] | (micChunk[i + 1] << 8));
            var loopbackSample = (short)(loopbackChunk[i] | (loopbackChunk[i + 1] << 8));
            var sum = Math.Clamp(micSample + loopbackSample, short.MinValue, short.MaxValue);
            mixed[i] = (byte)(sum & 0xFF);
            mixed[i + 1] = (byte)((sum >> 8) & 0xFF);
        }

        MixedAudioAvailable?.Invoke(mixed, _clock.Elapsed);
    }

    public void Dispose()
    {
        var timer = _timer;
        _timer = null;
        // Nothing signals `drained` when there is no timer to dispose, so waiting on it would just
        // burn the full timeout. Reachable now that a mixer can be torn down without ever having
        // been started (a caller whose audio device failed to open), and on a second Dispose.
        if (timer is null)
        {
            return;
        }

        using var drained = new ManualResetEvent(false);
        timer.Dispose(drained);
        drained.WaitOne(TimeSpan.FromSeconds(1));
    }
}
