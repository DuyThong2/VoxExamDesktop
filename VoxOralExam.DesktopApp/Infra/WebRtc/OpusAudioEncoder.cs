using Concentus.Enums;
using Concentus.Structs;

namespace VoxOralExam.DesktopApp.Infra.WebRtc;

/// <summary>
/// Buffers arbitrary-sized PCM16 mono chunks (from TurnAudioRecorder.StreamChunkAvailable or
/// AudioMixer.MixedAudioAvailable -- neither guarantees alignment to a clean Opus frame boundary)
/// and re-chunks them into fixed-duration frames before encoding: Opus only accepts a small fixed
/// set of frame durations (2.5/5/10/20/40/60ms), not arbitrary byte counts.
/// </summary>
public sealed class OpusAudioEncoder : IDisposable
{
    public const int FrameMilliseconds = 20;

    private readonly OpusEncoder _encoder;
    private readonly int _frameSizeSamples;
    private readonly List<byte> _buffer = [];
    private readonly byte[] _outputBuffer = new byte[4000];
    private readonly object _lock = new();
    private bool _disposed;

    public OpusAudioEncoder(int sampleRate = 16_000, int bitrate = 24_000)
    {
        _frameSizeSamples = sampleRate * FrameMilliseconds / 1000;
        _encoder = new OpusEncoder(sampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP)
        {
            Bitrate = bitrate
        };
    }

    /// <summary>
    /// Appends raw PCM16 mono bytes and returns zero or more encoded Opus frames, each covering
    /// exactly FrameMilliseconds of audio -- ready to hand off to an RTP audio track one at a time.
    /// </summary>
    public List<byte[]> Encode(byte[] pcm16Mono)
    {
        var frames = new List<byte[]>();
        var frameBytes = _frameSizeSamples * 2; // 16-bit samples

        lock (_lock)
        {
            if (_disposed)
            {
                return frames;
            }

            _buffer.AddRange(pcm16Mono);

            while (_buffer.Count >= frameBytes)
            {
                var pcmShorts = new short[_frameSizeSamples];
                for (var i = 0; i < _frameSizeSamples; i++)
                {
                    pcmShorts[i] = (short)(_buffer[i * 2] | (_buffer[i * 2 + 1] << 8));
                }
                _buffer.RemoveRange(0, frameBytes);

                var encodedLength = _encoder.Encode(
                    pcmShorts, 0, _frameSizeSamples,
                    _outputBuffer, 0, _outputBuffer.Length);

                var frame = new byte[encodedLength];
                Array.Copy(_outputBuffer, frame, encodedLength);
                frames.Add(frame);
            }
        }

        return frames;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _encoder.Dispose();
            _buffer.Clear();
        }
    }
}
