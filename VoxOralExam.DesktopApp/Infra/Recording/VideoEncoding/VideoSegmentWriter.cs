using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace VoxOralExam.DesktopApp.Infra.Recording.VideoEncoding;

internal sealed class VideoSegmentWriter : IDisposable
{
    // Every audio sample this writer accepts is fixed 16-bit mono PCM (see AudioMixer/
    // TurnAudioRecorder) -- WriteAudio only needs the sample rate to compute each sample's duration.
    private const int AudioBitsPerSample = 16;
    private const int AudioChannels = 1;

    private readonly IMFSinkWriter _writer;
    private readonly int _streamIndex;
    private readonly int? _audioStreamIndex;
    private readonly int _audioSampleRate;
    private readonly int _width;
    private readonly int _height;
    private readonly long _frameDurationTicks;
    private readonly ID3D11Device? _device;
    private readonly object? _contextLock;
    private ID3D11Texture2D? _stagingTexture;

    private long _lastSampleTime = -1;
    private long _lastAudioSampleTime = -1;
    private bool _completed;
    private bool _disposed;

    public string OutputPath { get; }

    public bool SupportsAudio => _audioStreamIndex is not null;

    public VideoSegmentWriter(
        string outputPath,
        int width,
        int height,
        int framesPerSecond,
        int bitrate,
        ID3D11Device? device = null,
        object? contextLock = null,
        int? audioSampleRate = null)
    {
        OutputPath = outputPath;
        _width = width;
        _height = height;
        _frameDurationTicks = TimeSpan.TicksPerSecond / framesPerSecond;
        _device = device;
        _contextLock = contextLock;
        _audioSampleRate = audioSampleRate ?? 0;
        (_writer, _streamIndex, _audioStreamIndex) = VideoSinkWriterFactory.Create(
            outputPath,
            width,
            height,
            framesPerSecond,
            bitrate,
            audioSampleRate is { } sampleRate
                ? new AudioFormatSpec(sampleRate, AudioChannels)
                : null);
    }

    public void WriteTexture(ID3D11Texture2D texture, TimeSpan localTimestamp)
    {
        if (_device is null || _contextLock is null)
        {
            throw new InvalidOperationException("This writer has no D3D device.");
        }

        var staging = _stagingTexture ??= CreateStagingTexture(texture);
        var rowBytes = _width * 4;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(rowBytes * _height);

        lock (_contextLock)
        {
            _device.ImmediateContext.CopyResource(staging, texture);
            var mapped = _device.ImmediateContext.Map(
                staging,
                0,
                MapMode.Read,
                Vortice.Direct3D11.MapFlags.None);

            try
            {
                unsafe
                {
                    buffer.Lock(out var destinationPointer, out _, out _);
                    try
                    {
                        var source = (byte*)mapped.DataPointer;
                        var destination = (byte*)destinationPointer;
                        for (var row = 0; row < _height; row++)
                        {
                            Buffer.MemoryCopy(
                                source + row * mapped.RowPitch,
                                destination + row * rowBytes,
                                rowBytes,
                                rowBytes);
                        }
                    }
                    finally
                    {
                        buffer.Unlock();
                    }
                }
            }
            finally
            {
                _device.ImmediateContext.Unmap(staging, 0);
            }
        }

        buffer.CurrentLength = rowBytes * _height;
        WriteSample(buffer, localTimestamp);
    }

    public void WriteBgr24(
        byte[] data,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        TimeSpan localTimestamp)
    {
        if (sourceWidth < _width || sourceHeight < _height)
        {
            throw new InvalidOperationException(
                $"Camera frame became smaller than the configured {_width}x{_height} output.");
        }

        var outputStride = _width * 4;
        using var buffer = MediaFactory.MFCreateMemoryBuffer(outputStride * _height);
        buffer.Lock(out var destinationPointer, out _, out _);
        try
        {
            unsafe
            {
                var destination = (byte*)destinationPointer;
                fixed (byte* sourceStart = data)
                {
                    for (var row = 0; row < _height; row++)
                    {
                        var source = sourceStart + row * sourceStride;
                        var output = destination + row * outputStride;
                        for (var column = 0; column < _width; column++)
                        {
                            output[column * 4] = source[column * 3];
                            output[column * 4 + 1] = source[column * 3 + 1];
                            output[column * 4 + 2] = source[column * 3 + 2];
                            output[column * 4 + 3] = 255;
                        }
                    }
                }
            }
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = outputStride * _height;
        WriteSample(buffer, localTimestamp);
    }

    public void WriteAudio(byte[] pcm, TimeSpan localTimestamp)
    {
        if (_audioStreamIndex is not { } audioStreamIndex)
        {
            throw new InvalidOperationException("This writer was not configured with an audio stream.");
        }

        using var buffer = MediaFactory.MFCreateMemoryBuffer(pcm.Length);
        buffer.Lock(out var destinationPointer, out _, out _);
        try
        {
            unsafe
            {
                fixed (byte* source = pcm)
                {
                    Buffer.MemoryCopy(source, (byte*)destinationPointer, pcm.Length, pcm.Length);
                }
            }
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = pcm.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);

        var bytesPerSample = AudioChannels * (AudioBitsPerSample / 8);
        var frameCount = pcm.Length / bytesPerSample;
        var duration = frameCount * TimeSpan.TicksPerSecond / _audioSampleRate;

        var requestedTime = Math.Max(0, localTimestamp.Ticks);
        var sampleTime = Math.Max(requestedTime, _lastAudioSampleTime + 1);
        sample.SampleTime = sampleTime;
        sample.SampleDuration = duration;
        _writer.WriteSample(audioStreamIndex, sample);
        _lastAudioSampleTime = sampleTime + duration - 1;
    }

    private void WriteSample(IMFMediaBuffer buffer, TimeSpan localTimestamp)
    {
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);

        var requestedTime = Math.Max(0, localTimestamp.Ticks);
        var sampleTime = Math.Max(requestedTime, _lastSampleTime + 1);
        sample.SampleTime = sampleTime;
        sample.SampleDuration = _frameDurationTicks;
        _writer.WriteSample(_streamIndex, sample);
        _lastSampleTime = sampleTime;
    }

    private ID3D11Texture2D CreateStagingTexture(ID3D11Texture2D texture)
    {
        var description = texture.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;
        return _device!.CreateTexture2D(description);
    }

    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _writer.Finalize();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Complete();
        }
        finally
        {
            _stagingTexture?.Dispose();
            _stagingTexture = null;
            _writer.Dispose();
        }
    }

    public void Abort()
    {
        if (_disposed)
        {
            return;
        }

        _completed = true;
        _disposed = true;
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _writer.Dispose();
    }
}
