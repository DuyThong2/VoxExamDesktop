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

    // Ticks UTC của khung hình cuối cùng và của lúc mở thiết bị; 0 = chưa có. Lưu dưới dạng long
    // và truy cập qua Interlocked vì chúng bị ghi ở thread gọi StartAsync/Stop rồi đọc ở thread
    // timer của CameraSignalGuard -- một `DateTimeOffset?` (struct 16 byte) không đọc/ghi nguyên
    // tử được, và một mốc đọc rách sẽ thành một cảnh báo mất camera bịa ra từ hư không.
    private long _lastFrameAtTicksUtc;
    private long _captureStartedAtTicksUtc;

    private static DateTimeOffset? ReadUtcTicks(ref long field)
    {
        var ticks = Interlocked.Read(ref field);
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }


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

    /// <summary>
    /// Mốc UTC của khung hình cuối cùng đọc được, null khi chưa có khung nào kể từ
    /// <see cref="StartAsync"/>.
    ///
    /// <para>Tồn tại vì vòng lặp capture NUỐT trọn tình huống mất thiết bị: rút cáp thì
    /// <c>Read()</c> trả khung rỗng, vòng lặp ngủ 10ms rồi thử lại, mãi mãi, không sự kiện, không
    /// log, không lỗi. Nhìn từ ngoài, một camera đã biến mất giống hệt một camera đang chạy.
    /// Con số này là thứ duy nhất phân biệt được hai trạng thái đó, và
    /// <c>CameraSignalGuard</c> là nơi diễn giải nó thành cảnh báo.</para>
    ///
    /// <para>Cố ý là dữ liệu chứ không phải sự kiện: vòng lặp chạy ở tần số khung hình, nên phát
    /// sự kiện từ đây là bắt mọi consumer chạy 30 lần/giây để nhận về "vẫn ổn".</para>
    /// </summary>
    public DateTimeOffset? LastFrameAtUtc => ReadUtcTicks(ref _lastFrameAtTicksUtc);

    /// <summary>
    /// Mốc UTC lúc mở thiết bị, null khi không chạy. Cùng với <see cref="LastFrameAtUtc"/> nó cho
    /// phép phân biệt "đang chạy rồi mất tín hiệu" với "chưa bao giờ gửi được khung nào".
    /// </summary>
    public DateTimeOffset? CaptureStartedAtUtc => ReadUtcTicks(ref _captureStartedAtTicksUtc);

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

        // Trước khi vòng lặp chạy: một mốc còn sót lại từ phiên trước sẽ khiến guard tưởng vừa có
        // khung hình và bỏ qua trọn khoảng khởi động.
        Interlocked.Exchange(ref _lastFrameAtTicksUtc, 0);
        Interlocked.Exchange(ref _captureStartedAtTicksUtc, DateTimeOffset.UtcNow.UtcTicks);
        _frameCount = 0;

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
        // Dừng có chủ ý thì không còn gì để canh; để nguyên mốc cũ sẽ khiến guard báo mất tín hiệu
        // đúng lúc bài thi vừa kết thúc bình thường.
        Interlocked.Exchange(ref _captureStartedAtTicksUtc, 0);
        Interlocked.Exchange(ref _lastFrameAtTicksUtc, 0);
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
                // Sau khi đã đọc được khung thật, trước khi phát cho consumer: một consumer ném lỗi
                // không có nghĩa là camera hỏng, nên nó không được làm mốc này đứng lại.
                Interlocked.Exchange(ref _lastFrameAtTicksUtc, DateTimeOffset.UtcNow.UtcTicks);

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

