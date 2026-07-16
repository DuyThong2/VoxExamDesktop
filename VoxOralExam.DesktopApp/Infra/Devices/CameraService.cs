using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Infra.Devices;

/// <summary>
/// Captures webcam frames using OpenCV.
///
/// Outputs:
///   - OnRawFrame: raw BGR bytes + width/height â†’ cho WebRtcClient encode H264/VP8
///   - OnPreviewFrame: BitmapImage â†’ cho WPF Image control hiá»ƒn thá»‹
/// </summary>
public class CameraService : IDisposable
{
    private VideoCapture? _capture;
    private readonly AppSettings _settings;
    private readonly object _lock = new();
    private bool _isCapturing;
    private CancellationTokenSource? _cts;
    private int _frameCount;
    private bool _isDisposed;

    /// <summary>
    /// Raw BGR pixel data + dimensions. DÃ¹ng cho WebRTC video encoding.
    /// </summary>
    public event Action<byte[], int, int>? OnRawFrame;

    /// <summary>
    /// BitmapImage cho WPF preview.
    /// </summary>
    public event Action<BitmapImage>? OnPreviewFrame;

    public bool IsCapturing => _isCapturing;

    public CameraService(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Má»Ÿ camera vÃ  báº¯t Ä‘áº§u capture loop.
    /// </summary>
    public Task StartAsync()
    {
        if (_isCapturing) return Task.CompletedTask;

        _capture = new VideoCapture(_settings.CameraDeviceIndex);
        _capture.Set(VideoCaptureProperties.FrameWidth, _settings.CameraWidth);
        _capture.Set(VideoCaptureProperties.FrameHeight, _settings.CameraHeight);
        _capture.Set(VideoCaptureProperties.Fps, _settings.CameraFps);

        if (!_capture.IsOpened())
            throw new InvalidOperationException($"KhÃ´ng thá»ƒ má»Ÿ camera (device {_settings.CameraDeviceIndex})");

        _isCapturing = true;
        _cts = new CancellationTokenSource();
        _ = CaptureLoop(_cts.Token);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Dá»«ng capture vÃ  giáº£i phÃ³ng camera.
    /// </summary>
    public void Stop()
    {
        if (_isDisposed)
        {
            return;
        }

        _isCapturing = false;
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        lock (_lock)
        {
            _capture?.Dispose();
            _capture = null;
        }
    }

    /// <summary>
    /// Capture loop: Ä‘á»c Mat â†’ raw BGR bytes (cho WebRTC) + BitmapImage (cho preview).
    /// </summary>
    private async Task CaptureLoop(CancellationToken ct)
    {
        var frameInterval = TimeSpan.FromMilliseconds(1000.0 / _settings.CameraFps);

        while (!ct.IsCancellationRequested && _isCapturing)
        {
            try
            {
                using var frame = new Mat();

                lock (_lock)
                {
                    _capture?.Read(frame);
                }

                if (frame.Empty())
                {
                    await Task.Delay(10, ct);
                    continue;
                }

                var width = frame.Width;
                var height = frame.Height;

                // 1. Raw BGR bytes cho WebRTC (liÃªn tá»¥c trong bá»™ nhá»›, zero-copy náº¿u Ä‘Æ°á»£c)
                //    Mat.Data lÃ  pointer â†’ copy ra byte[] Ä‘á»ƒ an toÃ n cross-thread
                var rawBytes = new byte[width * height * 3];
                System.Runtime.InteropServices.Marshal.Copy(frame.Data, rawBytes, 0, rawBytes.Length);

                // Debug: log frame Ä‘áº§u Ä‘á»ƒ confirm camera grab Ä‘Æ°á»£c pixel data
                if (_frameCount == 0)
                {
                    bool allZero = rawBytes.All(b => b == 0);
                    System.Diagnostics.Debug.WriteLine(
                        $"[Camera] First frame: {width}x{height}, " +
                        $"sample: {rawBytes[0]},{rawBytes[1]},{rawBytes[2]}, " +
                        $"allZero={allZero}, len={rawBytes.Length}");
                }
                _frameCount++;

                OnRawFrame?.Invoke(rawBytes, width, height);

                // 2. BitmapImage cho WPF preview (chuyá»ƒn Ä‘á»•i trÃªn background thread, Freeze Ä‘á»ƒ cross-thread safe)
                var bitmapImage = MatToBitmapImage(frame);
                OnPreviewFrame?.Invoke(bitmapImage);

                await Task.Delay(frameInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(100, ct);
            }
        }
    }

    /// <summary>
    /// Chuyá»ƒn OpenCV Mat â†’ BitmapImage cho WPF Image control.
    /// </summary>
    private static BitmapImage MatToBitmapImage(Mat mat)
    {
        using var bitmap = BitmapConverter.ToBitmap(mat);
        using var stream = new System.IO.MemoryStream();

        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Bmp);
        stream.Seek(0, System.IO.SeekOrigin.Begin);

        var bitmapImage = new BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
        bitmapImage.StreamSource = stream;
        bitmapImage.EndInit();
        bitmapImage.Freeze();

        return bitmapImage;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        Stop();
        _cts?.Dispose();
        _cts = null;
        _isDisposed = true;
    }
}

