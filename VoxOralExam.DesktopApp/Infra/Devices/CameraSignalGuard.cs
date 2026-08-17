using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Infra.Devices;

/// <summary>
/// Một lần mất tín hiệu camera.
/// </summary>
/// <param name="StoppedAt">
/// Mốc khung hình cuối cùng -- tức lúc sự việc THỰC SỰ xảy ra, không phải lúc bộ đếm kêu. Cảnh báo
/// mang mốc này đi để khoảng trống trong sổ bằng chứng trùng với khoảng trống trong bản ghi; lấy
/// thời điểm phát hiện sẽ làm mọi khoảng lệch đi đúng bằng ngưỡng.
/// </param>
/// <param name="Duration">Đã mất bao lâu tính tới lúc phát sự kiện này.</param>
/// <param name="NeverDelivered">
/// Camera mở được nhưng chưa từng gửi nổi một khung nào. Đây là ca "rút cáp ngay sau khi qua
/// preflight", và nó đáng nói khác đi: không có gì để mất, bản ghi camera rỗng từ đầu.
/// </param>
public sealed record CameraSignalOutage(
    DateTimeOffset StoppedAt,
    TimeSpan Duration,
    bool NeverDelivered);

/// <summary>
/// Canh luồng khung hình của <see cref="CameraService"/> và phân loại một khoảng lặng thành gián
/// đoạn thoáng qua hay mất tín hiệu thật.
///
/// <para>Hai ngưỡng chứ không phải một, vì hai đối tượng đọc có nhu cầu khác nhau. Học viên cần
/// biết NGAY để còn cắm lại dây, nên ngưỡng thứ nhất ngắn và chỉ hiện banner tại chỗ. Giám thị chỉ
/// cần biết khi nó đã thành sự cố thật, nên ngưỡng thứ hai dài hơn nhiều và mới là cái sinh cảnh
/// báo. Gộp làm một thì hoặc học viên biết quá muộn, hoặc lưới giám thị nhấp nháy vì mọi lần USB
/// tái liệt kê, driver reset hay nắp che privacy -- và một cảnh báo kêu ở mọi phiên thì không còn
/// nói lên điều gì.</para>
///
/// <para>Phát hiện đặt ở máy trạm chứ không phải ở vox-streaming, và đó là quyết định có chủ đích:
/// vox-streaming chỉ thấy "không có media" nên không phân biệt nổi camera bị rút với đường truyền
/// của học viên chết. Máy trạm thì biết -- nó thấy khung hình đứng lại trong khi tiến trình vẫn
/// khoẻ. Đó là hai lời buộc tội rất khác nhau và gộp chúng lại là bất công với học viên đang ngồi
/// trên mạng kém.</para>
///
/// <para>Chỉ QUAN SÁT: không đụng vào thiết bị, không dừng ghi hình, không kết thúc bài thi. Trong
/// mười lăm giây đầu, sự cố phần cứng và phá hoại cố ý là không phân biệt được, nên mọi hành động
/// không đảo ngược được đều là quyết định của con người.</para>
/// </summary>
public sealed class CameraSignalGuard : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly CameraService _camera;
    private readonly AppSettings _settings;
    private readonly object _gate = new();

    private Timer? _timer;
    private DateTimeOffset? _stoppedAt;
    private bool _lostRaised;
    private bool _disposed;

    /// <summary>Ngưỡng 1: khung hình đã ngừng. Chỉ hiện banner tại chỗ, KHÔNG phát cảnh báo.</summary>
    public event Action<CameraSignalOutage>? Interrupted;

    /// <summary>Ngưỡng 2: vẫn mất sau ngưỡng cảnh báo. Bắn đúng một lần cho mỗi lần mất.</summary>
    public event Action<CameraSignalOutage>? Lost;

    /// <summary>Khung hình đã trở lại. Chỉ bắn nếu <see cref="Interrupted"/> đã bắn trước đó.</summary>
    public event Action<CameraSignalOutage>? Restored;

    public CameraSignalGuard(CameraService camera, AppSettings settings)
    {
        _camera = camera;
        _settings = settings;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timer is not null)
            {
                return;
            }

            _stoppedAt = null;
            _lostRaised = false;
            _timer = new Timer(_ => Tick(), null, PollInterval, PollInterval);
        }

        LocalFileLogger.Info("camera_signal", "guard_started", new
        {
            interruptSeconds = InterruptThreshold.TotalSeconds,
            lostSeconds = LostThreshold.TotalSeconds
        });
    }

    public void Stop()
    {
        Timer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
            _stoppedAt = null;
            _lostRaised = false;
        }

        timer?.Dispose();
    }

    private TimeSpan InterruptThreshold =>
        TimeSpan.FromSeconds(Math.Max(1, _settings.CameraSignalInterruptSeconds));

    private TimeSpan LostThreshold =>
        // Không bao giờ thấp hơn ngưỡng 1: cấu hình sai thứ tự hai ngưỡng sẽ khiến cảnh báo bắn
        // cùng lúc với banner, tức mất hẳn nấc "thoáng qua" mà không ai nhận ra.
        TimeSpan.FromSeconds(Math.Max(
            _settings.CameraSignalInterruptSeconds + 1,
            _settings.CameraSignalLostSeconds));

    private void Tick()
    {
        CameraSignalOutage? interrupted = null;
        CameraSignalOutage? lost = null;
        CameraSignalOutage? restored = null;

        lock (_gate)
        {
            if (_timer is null)
            {
                return;
            }

            var startedAt = _camera.CaptureStartedAtUtc;
            if (!_camera.IsCapturing || startedAt is null)
            {
                // Camera dừng có chủ ý (kết thúc bài, chuyển màn). Không phải mất tín hiệu, và
                // không được để trạng thái đang mất treo lại cho lần chạy sau.
                _stoppedAt = null;
                _lostRaised = false;
                return;
            }

            var lastFrameAt = _camera.LastFrameAtUtc;
            // Chưa có khung nào thì tính từ lúc mở thiết bị: nếu không, camera chưa bao giờ chạy sẽ
            // vĩnh viễn không bị coi là mất tín hiệu.
            var referenceAt = lastFrameAt ?? startedAt.Value;
            var now = DateTimeOffset.UtcNow;
            var silentFor = now - referenceAt;

            if (silentFor >= InterruptThreshold)
            {
                if (_stoppedAt is null)
                {
                    _stoppedAt = referenceAt;
                    interrupted = new CameraSignalOutage(referenceAt, silentFor, lastFrameAt is null);
                }

                if (!_lostRaised && silentFor >= LostThreshold)
                {
                    _lostRaised = true;
                    lost = new CameraSignalOutage(_stoppedAt.Value, silentFor, lastFrameAt is null);
                }
            }
            else if (_stoppedAt is not null)
            {
                // Thời lượng đo tới khung hình ĐẦU TIÊN trở lại, không tới lúc tick này chạy --
                // nếu không, mỗi khoảng đều bị cộng thêm một chu kỳ poll.
                restored = new CameraSignalOutage(
                    _stoppedAt.Value,
                    referenceAt - _stoppedAt.Value,
                    NeverDelivered: false);
                _stoppedAt = null;
                _lostRaised = false;
            }
        }

        // Ngoài lock: handler gọi ngược vào UI và vào WS, giữ lock qua đó là mời deadlock.
        if (interrupted is not null)
        {
            LocalFileLogger.Info("camera_signal", "interrupted", new
            {
                stoppedAt = interrupted.StoppedAt,
                interrupted.NeverDelivered
            });
            Interrupted?.Invoke(interrupted);
        }

        if (lost is not null)
        {
            LocalFileLogger.Error(
                "camera_signal",
                "lost",
                new InvalidOperationException(
                    $"Camera ngừng gửi khung hình {lost.Duration.TotalSeconds:F0}s"),
                new { stoppedAt = lost.StoppedAt, lost.NeverDelivered });
            Lost?.Invoke(lost);
        }

        if (restored is not null)
        {
            LocalFileLogger.Info("camera_signal", "restored", new
            {
                stoppedAt = restored.StoppedAt,
                seconds = restored.Duration.TotalSeconds
            });
            Restored?.Invoke(restored);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
