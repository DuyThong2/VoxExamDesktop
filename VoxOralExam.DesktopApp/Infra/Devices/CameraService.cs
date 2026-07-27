using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using VoxOralExam.DesktopApp.Infra.Recording;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Infra.Devices;


public class CameraService : IDisposable
{
    private VideoCapture? _capture;
    private readonly AppSettings _settings;
    private readonly RecordingClock _recordingClock;
    private readonly object _lock = new();
    private bool _isCapturing;
    private CancellationTokenSource? _cts;
    private int _frameCount;
    private bool _isDisposed;


    public event Action<byte[], int, int>? OnRawFrame;

    /// <summary>
    /// Immutable camera frame for local recording. The camera remains single-owner: consumers
    /// fan out from this event instead of opening the physical device a second time.
    /// </summary>
    public event Action<CameraFrame>? OnCapturedFrame;

    /// <summary>
    /// BitmapImage cho WPF preview.
    /// </summary>
    public event Action<BitmapImage>? OnPreviewFrame;

    public bool IsCapturing => _isCapturing;

    public CameraService(AppSettings settings, RecordingClock recordingClock)
    {
        _settings = settings;
        _recordingClock = recordingClock;
    }


    public Task StartAsync()
    {
        if (_isCapturing) return Task.CompletedTask;

        _capture = new VideoCapture(_settings.CameraDeviceIndex);
        _capture.Set(VideoCaptureProperties.FrameWidth, _settings.CameraWidth);
        _capture.Set(VideoCaptureProperties.FrameHeight, _settings.CameraHeight);
        _capture.Set(VideoCaptureProperties.Fps, _settings.CameraFps);

        if (!_capture.IsOpened())
            throw new InvalidOperationException($"Không tìm thấy thiết bị camera (device {_settings.CameraDeviceIndex})");

        _isCapturing = true;
        _cts = new CancellationTokenSource();
        _ = CaptureLoop(_cts.Token);

        return Task.CompletedTask;
    }


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


                var rawBytes = new byte[width * height * 3];
                System.Runtime.InteropServices.Marshal.Copy(frame.Data, rawBytes, 0, rawBytes.Length);
                var capturedAt = _recordingClock.Elapsed;


                if (_frameCount == 0)
                {
                    bool allZero = rawBytes.All(b => b == 0);
                    System.Diagnostics.Debug.WriteLine(
                        $"[Camera] First frame: {width}x{height}, " +
                        $"sample: {rawBytes[0]},{rawBytes[1]},{rawBytes[2]}, " +
                        $"allZero={allZero}, len={rawBytes.Length}");
                }
                _frameCount++;

                try
                {
                    OnCapturedFrame?.Invoke(new CameraFrame(
                        rawBytes,
                        width,
                        height,
                        width * 3,
                        capturedAt));
                }
                catch (Exception ex)
                {
                    LocalFileLogger.Error("camera", "recording_frame_consumer_failed", ex);
                }

                try
                {
                    OnRawFrame?.Invoke(rawBytes, width, height);
                }
                catch (Exception ex)
                {
                    LocalFileLogger.Error("camera", "raw_frame_consumer_failed", ex);
                }


                var bitmapImage = MatToBitmapImage(frame);
                try
                {
                    OnPreviewFrame?.Invoke(bitmapImage);
                }
                catch (Exception ex)
                {
                    LocalFileLogger.Error("camera", "preview_frame_consumer_failed", ex);
                }

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

