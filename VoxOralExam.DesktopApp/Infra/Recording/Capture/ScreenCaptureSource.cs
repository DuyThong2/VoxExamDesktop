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

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SizeInt32 _captureSize;
    private volatile bool _acceptFrames;

    public event Action<ID3D11Texture2D, TimeSpan>? FrameArrived;

    public event Action<Exception>? CaptureFailed;

    public ScreenCaptureSource(
        ID3D11Device device,
        IDirect3DDevice winRtDevice,
        RecordingClock clock,
        object contextLock)
    {
        _device = device;
        _winRtDevice = winRtDevice;
        _clock = clock;
        _contextLock = contextLock;
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
        _session.StartCapture();
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
            lock (_contextLock)
            {
                _device.ImmediateContext.CopyResource(ownedTexture, sourceTexture);
            }

            var handler = FrameArrived;
            if (handler is null)
            {
                ownedTexture.Dispose();
                return;
            }

            handler(ownedTexture, _clock.Elapsed);
            ownedTexture = null; // ownership moved to the receiver
        }
        catch (Exception ex)
        {
            ownedTexture?.Dispose();
            _acceptFrames = false;
            CaptureFailed?.Invoke(ex);
        }
    }

    public void Stop()
    {
        _acceptFrames = false;

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
        }

        _session?.Dispose();
        _session = null;

        _framePool?.Dispose();
        _framePool = null;
        _item = null;
    }

    public void Dispose() => Stop();
}
