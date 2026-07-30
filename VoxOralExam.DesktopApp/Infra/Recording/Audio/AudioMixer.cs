using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoxOralExam.DesktopApp.Infra.Recording;
using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Infra.Recording.Audio;

/// <summary>
/// How much audio the mixer aims to keep buffered per source, and how far behind real time a
/// source may fall before the mixer gives up on the backlog and skips forward.
///
/// Note which mechanism does the work: OnTick's catch-up loop is what normally clears a backlog,
/// and it clears it by *sending* the audio, at four times real time. The ceiling here is only for
/// a backlog that loop cannot work off -- which is why it is nowhere near as tight as "a live view
/// should not be a second behind" would suggest. A ceiling below what catch-up can deliver would
/// throw away real speech to save latency that was about to recover on its own.
///
/// The two consumers do still want different things once that point is reached. The live monitor is
/// watched in real time, so it gives up on stale audio sooner. The recorded segments are the graded
/// evidence and nothing downstream cares how late a sample reaches the sink writer, so there the
/// ceiling exists only to stay clear of BufferDuration, where NAudio would start dropping the
/// newest audio instead of the oldest.
/// </summary>
/// <param name="TargetMilliseconds">
/// Buffer depth to aim for: what each source is primed with, and what a trim cuts back to. Must
/// exceed one capture callback (TurnAudioRecorder's BufferMilliseconds is 50) -- otherwise the
/// 20ms tick keeps outrunning the 50ms arrivals and ReadFully punches a silence hole on every
/// boundary, which is audible as a continuous rasp rather than as a dropout.
/// </param>
/// <param name="CeilingMilliseconds">
/// Backlog depth above which the mixer discards a source's oldest audio. Keep it comfortably below
/// the two-second BufferDuration and comfortably above what catch-up covers.
/// </param>
public readonly record struct AudioMixerLatencyPolicy(int TargetMilliseconds, int CeilingMilliseconds)
{
    public static AudioMixerLatencyPolicy LiveMonitor { get; } = new(60, 500);

    public static AudioMixerLatencyPolicy Recording { get; } = new(60, 1_500);
}

/// <summary>
/// Combines the exam mic (fixed 16kHz/16-bit/mono, matching TurnAudioRecorder's capture format)
/// with system/loopback audio (whatever the default playback device's native mix format happens to
/// be -- typically 44.1/48kHz float stereo, not under this app's control) into a single mono 16kHz
/// PCM16 stream for Screen's recorded audio track. Runs on its own timer tick, independent of both
/// capture devices' callback threads -- BufferedWaveProvider.ReadFully absorbs the jitter between
/// "when audio actually arrives" and "when this mixer reads it".
///
/// Emission is anchored to <see cref="RecordingClock"/>, not to the tick rate and not to how much
/// audio happens to be buffered: each tick emits however many 20ms chunks the clock says are owed
/// (see OnTick). That is what keeps the audio timeline honest, and it is load-bearing for both
/// consumers -- the segment writer lays chunks down contiguously and MonitorStreamClient advances
/// the RTP audio clock a fixed 20ms per frame, so in both cases the audio timeline is exactly the
/// count of chunks this class emits. Emitting on any other schedule desyncs audio from a video
/// timeline that is derived from real capture timestamps.
///
/// It also means the two sources stay aligned with each other by construction: every emitted chunk
/// reads exactly one chunk from each, so neither can advance past the other. What each source does
/// when it cannot keep up is handled per source instead -- padded with silence on underrun, and
/// skipped forward by TrimToTarget when it falls further behind than the policy allows.
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
    /// chunks is 100ms of audio, so the mixer can run five times real time while catching up
    /// without handing the segment writer or the Opus encoder an unbounded burst if something
    /// pauses the timer for a long time. Capping only spreads the catch-up rather than losing it:
    /// the deficit is recomputed against the clock on every tick, so whatever this tick cannot
    /// cover is still owed on the next one.
    /// </summary>
    private const int MaxChunksPerTick = 5;

    private readonly RecordingClock _clock;
    private readonly AudioMixerLatencyPolicy _latency;
    private readonly BufferedWaveProvider _micBuffer;
    private readonly SourceState _micState = new("mic");
    private readonly SourceState _loopbackState = new("loopback");
    private readonly int _bytesPerTick;

    // Only ever written to by TrimToTarget, which discards what it reads. Sized well above one
    // tick's worth of any plausible capture format so a trim is a couple of reads, not a hundred.
    private readonly byte[] _discardScratch = new byte[16 * 1024];

    private BufferedWaveProvider? _loopbackRawBuffer;
    private IWaveProvider? _loopbackResampled;
    private Timer? _timer;
    private volatile bool _loopbackEnabled;

    // Timeline bookkeeping. Touched only from OnTick, which _tickInProgress serializes.
    private long _framesEmitted;
    private long _timelineOffsetFrames;
    private bool _timelineAnchored;
    private int _tickInProgress;

    public event Action<byte[], TimeSpan>? MixedAudioAvailable;

    public AudioMixer(RecordingClock clock, AudioMixerLatencyPolicy latency)
    {
        _clock = clock;
        _latency = latency;
        var micFormat = new WaveFormat(TargetSampleRate, 16, 1);
        _micBuffer = new BufferedWaveProvider(micFormat)
        {
            ReadFully = true,
            BufferDuration = TimeSpan.FromSeconds(2),
            // Backstop only -- TrimToTarget is what actually bounds the backlog, and it cuts in
            // well below this. Without the flag NAudio throws InvalidOperationException("Buffer
            // full") from AddSamples, which runs on the capture device's own callback thread where
            // nothing catches it and an unhandled exception takes the whole app down mid-exam.
            // Note this discards the NEWEST samples and leaves the buffer pinned full (see
            // TrimToTarget's remarks), so reaching it is a bug, not a policy -- hence the log.
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
        var buffer = new BufferedWaveProvider(loopbackFormat)
        {
            ReadFully = true,
            BufferDuration = TimeSpan.FromSeconds(2),
            // Same reason as _micBuffer's, and the same callback-thread consequence.
            DiscardOnBufferOverflow = true
        };
        ResetToTarget(buffer);
        _loopbackRawBuffer = buffer;

        ISampleProvider sample = buffer.ToSampleProvider();
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
        if (_timer is not null)
        {
            return;
        }

        // Every source starts at exactly the target depth, no matter how long its capture device
        // has been running. Both callers open their devices before starting the mixer -- opening a
        // WASAPI loopback device is not instant -- so by now there can be hundreds of milliseconds
        // banked, and clock-anchored emission has no way to work a head start off: it would sit in
        // front of every later chunk for the rest of the session as a fixed offset against video.
        ResetToTarget(_micBuffer);
        if (_loopbackRawBuffer is { } loopback)
        {
            ResetToTarget(loopback);
        }

        _timer = new Timer(OnTick, null, TickMilliseconds, TickMilliseconds);
    }

    /// <summary>
    /// Drops whatever a source has banked and gives it a head start of silence instead, so that
    /// ordinary jitter cannot starve it.
    /// </summary>
    /// <remarks>
    /// The mic arrives in 50ms chunks and is consumed 20ms at a time, so without the head start the
    /// buffered depth sits near zero and any late callback makes ReadFully pad a 20ms hole --
    /// routinely, not exceptionally. Costs a fixed TargetMilliseconds of audio-behind-video, which
    /// at 60ms is well inside the ~125ms that lipsync tolerates and is invisible next to the drift
    /// it replaces. Zeroed bytes are silence for PCM16 and for the float formats a loopback device
    /// may hand us.
    /// </remarks>
    private void ResetToTarget(BufferedWaveProvider buffer)
    {
        buffer.ClearBuffer();
        var bytes = BytesForMilliseconds(buffer.WaveFormat, _latency.TargetMilliseconds);
        if (bytes > 0)
        {
            buffer.AddSamples(new byte[bytes], 0, bytes);
        }
    }

    // Both of these run on their capture device's callback thread, where an escaping exception is
    // unhandled and terminates the process. DiscardOnBufferOverflow above already removes the one
    // throw that was actually reachable; these exist so that no future change to NAudio or to the
    // buffer configuration can turn a recoverable audio fault back into a dead exam.
    public void AddMicSamples(byte[] pcm)
    {
        try
        {
            ReportIfOverflowing(_micBuffer, pcm.Length, _micState);
            _micBuffer.AddSamples(pcm, 0, pcm.Length);
        }
        catch (Exception ex)
        {
            LogSourceFaultOnce(_micState, AudioFault.Rejected, "mic_samples_rejected", ex);
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

            ReportIfOverflowing(buffer, raw.Length, _loopbackState);
            buffer.AddSamples(raw, 0, raw.Length);
        }
        catch (Exception ex)
        {
            LogSourceFaultOnce(_loopbackState, AudioFault.Rejected, "loopback_samples_rejected", ex);
        }
    }

    /// <summary>
    /// Logs the overflow that DiscardOnBufferOverflow is about to swallow. NAudio reports nothing
    /// when it drops, so the condition has to be checked before handing the samples over.
    /// </summary>
    private static void ReportIfOverflowing(BufferedWaveProvider buffer, int incomingBytes, SourceState state)
    {
        if (buffer.BufferedBytes + incomingBytes <= buffer.BufferLength)
        {
            return;
        }

        LogSourceFaultOnce(
            state,
            AudioFault.Overflow,
            "audio_buffer_overflow_discarding",
            new InvalidOperationException(
                $"{state.Name} buffer full ({buffer.BufferedBytes}/{buffer.BufferLength} bytes); " +
                "the newest audio is being discarded. TrimToTarget should have bounded this backlog " +
                "long before the buffer filled."));
    }

    /// <summary>
    /// Drains the chunks the clock says are owed, having first skipped any source that has fallen
    /// too far behind.
    /// </summary>
    /// <remarks>
    /// Capture devices produce at exactly real time, but this runs on a <see cref="Timer"/> whose
    /// 20ms period is below Windows' default timer resolution and whose callbacks are queued on the
    /// thread pool -- so ticks arrive late whenever the pool is busy, and a burst of failing HTTP
    /// uploads is more than enough to do that.
    ///
    /// Emitting one chunk per tick made every late tick a permanent debt, because ReadFully pads a
    /// short read with silence instead of leaving the backlog to be picked up next time. That gave
    /// the session a fixed budget of BufferDuration -- two seconds, a hundred ticks -- of
    /// accumulated lateness, after which the buffer filled and AddSamples threw on the capture
    /// thread and froze the app. Deciding the count from the clock instead means lateness is repaid
    /// on the next tick rather than accumulating, and it holds the audio timeline against the same
    /// clock the video timestamps come from.
    /// </remarks>
    private void OnTick(object? state)
    {
        // A tick emitting up to five chunks runs five Opus encodes inline, so overrunning the 20ms
        // period is realistic and Timer would then start a second callback on another pool thread.
        // Two of those interleave their reads, mixing one source's chunk against the other source's
        // next one and delivering chunks out of order -- and concurrent Read on
        // WdlResamplingSampleProvider corrupts resampler state outright. Skipping the overlapping
        // tick is correct: the work is still owed, and the tick already running will pick it up.
        if (Interlocked.Exchange(ref _tickInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            TrimToTarget(_micBuffer, _micState);
            if (_loopbackRawBuffer is { } loopback)
            {
                TrimToTarget(loopback, _loopbackState);
            }

            var owed = FramesOwed();
            for (var chunk = 0; chunk < owed; chunk++)
            {
                EmitChunk();
                _framesEmitted++;
            }
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("audio_mixer", "tick_failed", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _tickInProgress, 0);
        }
    }

    /// <summary>
    /// How many 20ms chunks the clock is owed, capped at <see cref="MaxChunksPerTick"/>.
    /// </summary>
    private int FramesOwed()
    {
        if (!_clock.IsRunning)
        {
            // Nothing to anchor to yet. LiveMonitorStreamService can start before
            // ExamRecordingService starts the shared clock, and the clock is stopped again while
            // this mixer is still being torn down. Fall back to tick pacing so the audio track
            // keeps flowing -- vox-streaming only begins its ffmpeg ingest once an audio track has
            // arrived, so a silent-but-flowing track is what keeps a live view working at all.
            return 1;
        }

        var elapsedFrames = (long)(_clock.Elapsed.TotalMilliseconds / TickMilliseconds);
        if (!_timelineAnchored)
        {
            // Whatever the clock already read when this mixer began emitting is not a backlog this
            // mixer owes; only advances from here on are.
            _timelineOffsetFrames = elapsedFrames - _framesEmitted;
            _timelineAnchored = true;
            return 0;
        }

        var owed = elapsedFrames - _timelineOffsetFrames - _framesEmitted;
        if (owed < 0)
        {
            // The clock went backwards, which means it was restarted underneath us (it is a
            // singleton shared across attempts). Re-anchor rather than stall until it catches up.
            _timelineOffsetFrames = elapsedFrames - _framesEmitted;
            return 0;
        }

        return (int)Math.Min(owed, MaxChunksPerTick);
    }

    /// <summary>
    /// Skips a source forward when its backlog exceeds the policy ceiling, discarding its OLDEST
    /// audio down to the target depth.
    /// </summary>
    /// <remarks>
    /// This is the discard that DiscardOnBufferOverflow cannot do for us, and the direction matters.
    /// NAudio's circular buffer clamps a write to the space available and keeps what fits, so an
    /// overflow drops the NEWEST samples and leaves the buffer pinned at its maximum depth -- the
    /// worst of both outcomes for a live view, which then stays permanently late and loses audio
    /// anyway. Dropping from the front bounds the latency instead, and what it loses is the audio
    /// nobody is waiting for any more.
    ///
    /// Discarding costs content but never time: OnTick emits what the clock owes regardless of what
    /// is buffered, so the chunks covering a discarded span still go out, carrying silence or
    /// fresher audio. That is what keeps a drop from permanently shifting everything after it
    /// against the video.
    /// </remarks>
    private void TrimToTarget(BufferedWaveProvider buffer, SourceState state)
    {
        var format = buffer.WaveFormat;
        if (buffer.BufferedBytes <= BytesForMilliseconds(format, _latency.CeilingMilliseconds))
        {
            return;
        }

        // Both of these are rounded down to whole frames, because discarding a partial one would
        // shift every following byte and reinterpret the rest of the stream as noise. Capture
        // callbacks do deliver whole frames today, so this only matters if that ever stops being
        // true -- but the failure it prevents is a burst of white noise in an exam recording.
        var excess = buffer.BufferedBytes - BytesForMilliseconds(format, _latency.TargetMilliseconds);
        excess -= excess % format.BlockAlign;
        var readSize = _discardScratch.Length - (_discardScratch.Length % format.BlockAlign);
        if (readSize <= 0)
        {
            return;
        }

        var discarded = 0;
        while (excess > 0)
        {
            // Reading is discarding.
            var take = Math.Min(excess, readSize);
            discarded += buffer.Read(_discardScratch, 0, take);
            excess -= take;
        }

        state.DiscardedMilliseconds += (long)discarded * 1000 / format.AverageBytesPerSecond;
        LogSourceFaultOnce(
            state,
            AudioFault.Trimmed,
            "audio_backlog_trimmed",
            new InvalidOperationException(
                $"{state.Name} fell more than {_latency.CeilingMilliseconds}ms behind real time; " +
                $"skipped forward by discarding {discarded} bytes of the oldest audio. Totals are " +
                "logged on teardown."));
    }

    private void EmitChunk()
    {
        // Clock-domain, so it lines up with the video timestamps the recorders stamp from the same
        // clock -- and exactly 20ms apart, which matters now that a catch-up tick emits several
        // chunks between two reads of the clock.
        var timestamp = TimeSpan.FromMilliseconds((_timelineOffsetFrames + _framesEmitted) * TickMilliseconds);

        var micChunk = new byte[_bytesPerTick];
        _micBuffer.Read(micChunk, 0, _bytesPerTick);

        if (!_loopbackEnabled || _loopbackResampled is null)
        {
            MixedAudioAvailable?.Invoke(micChunk, timestamp);
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

        MixedAudioAvailable?.Invoke(mixed, timestamp);
    }

    private static int BytesForMilliseconds(WaveFormat format, int milliseconds)
    {
        var bytes = format.AverageBytesPerSecond * milliseconds / 1000;
        return bytes - (bytes % format.BlockAlign);
    }

    private static void LogSourceFaultOnce(SourceState state, AudioFault fault, string @event, Exception ex)
    {
        // Once per source per fault kind: a fault that repeats every 20ms must not itself become
        // the problem, but one latch shared across sources and kinds (as it briefly was) hides a
        // loopback failure behind an unrelated mic one.
        if (state.TryLatch(fault))
        {
            LocalFileLogger.Error("audio_mixer", @event, ex);
        }
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

        // A first-occurrence log says a source struggled; only a total says whether that was one
        // hiccup or a session the recording cannot be trusted for.
        ReportDiscards(_micState);
        ReportDiscards(_loopbackState);
    }

    private static void ReportDiscards(SourceState state)
    {
        if (state.DiscardedMilliseconds > 0)
        {
            LocalFileLogger.Info("audio_mixer", "audio_discarded_total", new
            {
                source = state.Name,
                discardedMilliseconds = state.DiscardedMilliseconds
            });
        }
    }

    private enum AudioFault
    {
        Overflow,
        Rejected,
        Trimmed
    }

    /// <summary>
    /// Per-source diagnostics, one instance each so a mic fault cannot mask a loopback one.
    /// </summary>
    private sealed class SourceState(string name)
    {
        private readonly int[] _latched = new int[Enum.GetValues<AudioFault>().Length];

        public string Name { get; } = name;

        /// <summary>Total audio skipped by TrimToTarget, in milliseconds of this source's own rate.</summary>
        public long DiscardedMilliseconds;

        /// <summary>True the first time this source hits this fault, false on every repeat.</summary>
        public bool TryLatch(AudioFault fault) => Interlocked.Exchange(ref _latched[(int)fault], 1) == 0;
    }
}
