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

    private readonly RecordingClock _clock;
    private readonly BufferedWaveProvider _micBuffer;
    private readonly int _bytesPerTick;
    private BufferedWaveProvider? _loopbackRawBuffer;
    private IWaveProvider? _loopbackResampled;
    private Timer? _timer;
    private volatile bool _loopbackEnabled;

    public event Action<byte[], TimeSpan>? MixedAudioAvailable;

    public AudioMixer(RecordingClock clock)
    {
        _clock = clock;
        var micFormat = new WaveFormat(TargetSampleRate, 16, 1);
        _micBuffer = new BufferedWaveProvider(micFormat)
        {
            ReadFully = true,
            BufferDuration = TimeSpan.FromSeconds(2)
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
            BufferDuration = TimeSpan.FromSeconds(2)
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

    public void AddMicSamples(byte[] pcm) => _micBuffer.AddSamples(pcm, 0, pcm.Length);

    public void AddLoopbackSamples(byte[] raw) => _loopbackRawBuffer?.AddSamples(raw, 0, raw.Length);

    private void OnTick(object? state)
    {
        try
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
        catch (Exception ex)
        {
            LocalFileLogger.Error("audio_mixer", "tick_failed", ex);
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
    }
}
