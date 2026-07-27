using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using VoxOralExam.DesktopApp.Infra.Recording.Interop;

namespace VoxOralExam.DesktopApp.Infra.Recording.Capture;

internal sealed record ScreenCaptureInfo(int Width, int Height);

internal sealed class ScreenCaptureSource : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly IDirect3DDevice _winRtDevice;
    private readonly RecordingClock _clock;
    private readonly object _contextLock;
    private readonly object _lastFrameLock = new();
    private readonly long _frameIntervalTicks;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _captureSize;
    private volatile bool _acceptFrames;

    // Windows Graphics Capture (backed by IDXGIOutputDuplication) only delivers a frame when
    // on-screen pixels actually change -- during a mostly-static screen (typical for an oral exam,
    // where the student is looking at a question and speaking rather than interacting), that can
    // mean tens of seconds between real frames. Left alone, segment/recording duration would
    // reflect "how much the screen changed" instead of real elapsed time, which breaks any use of
    // the recording's own timeline (correlating with AI proctoring alert timestamps, the planned
    // client/server audio-duration reconciliation in docs/RECORDING.md, or just answering "what was
    // on screen at 10:32"). _lastFrameTexture/_keepAliveTimer duplicate the most recent real frame
    // on a timer so the pipeline still advances at the target cadence even when nothing changes.
    private ID3D11Texture2D? _lastFrameTexture;
    private long _lastEmittedTimestampTicks;
    private Timer? _keepAliveTimer;

    public event Action<ID3D11Texture2D, TimeSpan>? FrameArrived;

    public event Action<Exception>? CaptureFailed;

    public ScreenCaptureSource(
        ID3D11Device device,
        IDirect3DDevice winRtDevice,
        RecordingClock clock,
        object contextLock,
        int targetFps)
    {
        _device = device;
        _winRtDevice = winRtDevice;
        _clock = clock;
        _contextLock = contextLock;
        _frameIntervalTicks = TimeSpan.TicksPerSecond / Math.Clamp(targetFps, 1, 60);
    }

    public ScreenCaptureInfo Initialize()
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new PlatformNotSupportedException(
                "Windows Graphics Capture is not supported on this device.");
        }

        _item = GraphicsCaptureInterop.CreateItemForMonitor(
            NativeMethods.GetPrimaryMonitorHandle());
        _captureSize = _item.Size;

        if (_captureSize.Width <= 0 || _captureSize.Height <= 0)
        {
            throw new InvalidOperationException("The primary monitor has an invalid capture size.");
        }

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _captureSize);
        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = true;

        return new ScreenCaptureInfo(_captureSize.Width, _captureSize.Height);
    }

    public void Start()
    {
        if (_session is null)
        {
            throw new InvalidOperationException("Screen capture has not been initialized.");
        }

        _acceptFrames = true;
        _lastEmittedTimestampTicks = _clock.Elapsed.Ticks;
        _session.StartCapture();

        // Check twice per target frame interval so the actual gap between emitted frames stays
        // close to 1/fps instead of drifting up to a full extra interval late.
        var periodMs = Math.Max(20, (int)(_frameIntervalTicks / TimeSpan.TicksPerMillisecond / 2));
        _keepAliveTimer = new Timer(OnKeepAliveTick, null, periodMs, periodMs);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!_acceptFrames)
        {
            return;
        }

        ID3D11Texture2D? ownedTexture = null;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            if (frame.ContentSize.Width != _captureSize.Width ||
                frame.ContentSize.Height != _captureSize.Height)
            {
                throw new InvalidOperationException(
                    "The screen resolution changed while recording.");
            }

            using var sourceTexture = Direct3D11Interop.GetTexture2D(frame.Surface);
            var description = sourceTexture.Description;
            description.MiscFlags = ResourceOptionFlags.None;

            ownedTexture = _device.CreateTexture2D(description);
            var retained = _device.CreateTexture2D(description);
            lock (_contextLock)
            {
                _device.ImmediateContext.CopyResource(ownedTexture, sourceTexture);
                _device.ImmediateContext.CopyResource(retained, sourceTexture);
            }

            ReplaceLastFrame(retained);
            EmitFrame(ownedTexture, _clock.Elapsed);
            ownedTexture = null; // ownership moved to the receiver
        }
        catch (Exception ex)
        {
            ownedTexture?.Dispose();
            _acceptFrames = false;
            CaptureFailed?.Invoke(ex);
        }
    }

    private void OnKeepAliveTick(object? state)
    {
        if (!_acceptFrames)
        {
            return;
        }

        try
        {
            var now = _clock.Elapsed;
            if (now.Ticks - Interlocked.Read(ref _lastEmittedTimestampTicks) < _frameIntervalTicks)
            {
                return;
            }

            ID3D11Texture2D? duplicate = null;
            lock (_lastFrameLock)
            {
                if (_lastFrameTexture is null)
                {
                    return; // nothing captured yet to duplicate from
                }

                duplicate = _device.CreateTexture2D(_lastFrameTexture.Description);
                lock (_contextLock)
                {
                    _device.ImmediateContext.CopyResource(duplicate, _lastFrameTexture);
                }
            }

            EmitFrame(duplicate, now);
        }
        catch (Exception ex)
        {
            _acceptFrames = false;
            CaptureFailed?.Invoke(ex);
        }
    }

    private void EmitFrame(ID3D11Texture2D texture, TimeSpan timestamp)
    {
        Interlocked.Exchange(ref _lastEmittedTimestampTicks, timestamp.Ticks);

        var handler = FrameArrived;
        if (handler is null)
        {
            texture.Dispose();
            return;
        }

        handler(texture, timestamp);
    }

    private void ReplaceLastFrame(ID3D11Texture2D texture)
    {
        ID3D11Texture2D? previous;
        lock (_lastFrameLock)
        {
            previous = _lastFrameTexture;
            _lastFrameTexture = texture;
        }
        previous?.Dispose();
    }

    public void Stop()
    {
        _acceptFrames = false;

        if (_keepAliveTimer is not null)
        {
            // Block until any in-flight tick finishes before tearing down the device/textures it
            // touches -- a plain Dispose() does not wait for a currently-running callback.
            using var drained = new ManualResetEvent(false);
            _keepAliveTimer.Dispose(drained);
            drained.WaitOne(TimeSpan.FromSeconds(2));
            _keepAliveTimer = null;
        }

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        _session?.Dispose();
        _session = null;

        _framePool?.Dispose();
        _framePool = null;
        _item = null;

        ID3D11Texture2D? lastFrame;
        lock (_lastFrameLock)
        {
            lastFrame = _lastFrameTexture;
            _lastFrameTexture = null;
        }
        lastFrame?.Dispose();
    }

    public void Dispose() => Stop();
}
