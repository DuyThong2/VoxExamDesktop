using NAudio.Wave;
using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Infra.Devices;

/// <summary>
/// Phát tài nguyên dạng AUDIO của câu hỏi ra ĐÚNG thiết bị học sinh đã chọn ở màn kiểm tra thiết bị.
///
/// <para>Vì sao không dùng <c>MediaElement</c> như video: MediaElement của WPF không có cách nào
/// chọn thiết bị ra, nó luôn phát vào thiết bị mặc định của Windows. Trong khi giọng AI đi qua
/// <c>WaveOut</c> với <c>DeviceNumber</c> lấy từ lựa chọn của học sinh. Hai đường khác nhau nghĩa
/// là học sinh đeo tai nghe mà Windows vẫn để loa laptop làm mặc định thì <b>giọng AI vào tai nghe
/// còn bản ghi phát ra loa ngoài</b> -- nghe nhỏ và xa, rồi mic thu lại chính nó (mic không có khử
/// vọng), làm bẩn transcript.</para>
///
/// <para>Video vẫn để MediaElement lo cả hình lẫn tiếng: tách tiếng ra NAudio thì phải tự đồng bộ
/// hình-tiếng, và lệch môi trên một đoạn 80 giây khó chịu hơn hẳn chuyện sai thiết bị.</para>
///
/// <para>Đọc bằng <see cref="MediaFoundationReader"/> nên nhận cả mp3/m4a/aac/wav — cùng bộ định
/// dạng mà màn soạn câu hỏi cho upload.</para>
/// </summary>
public sealed class QuestionAssetAudioPlayer : IDisposable
{
    private readonly object _sync = new();
    private WaveOut? _output;
    private MediaFoundationReader? _reader;
    private bool _disposed;

    /// <summary>Phát hết bình thường. KHÔNG bắn khi bị <see cref="Stop"/> cắt giữa chừng.</summary>
    public event Action? PlaybackEnded;

    public event Action<string>? PlaybackFailed;

    public void Play(Uri source, int deviceNumber)
    {
        Stop();

        try
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                // MediaFoundationReader nhận cả đường dẫn tệp cục bộ lẫn URL; tệp cục bộ là đường
                // thường gặp vì tài nguyên đã được tải sẵn trước khi vào thi (QuestionAssetCache).
                _reader = new MediaFoundationReader(source.IsFile ? source.LocalPath : source.ToString());
                _output = new WaveOut
                {
                    DeviceNumber = deviceNumber,
                    Volume = 1.0f
                };
                _output.PlaybackStopped += HandlePlaybackStopped;
                _output.Init(_reader);
                _output.Play();
            }
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("asset_audio", "play_failed", ex, new { deviceNumber });
            DisposePlayback();
            PlaybackFailed?.Invoke(ex.Message);
        }
    }

    /// <summary>
    /// Dừng và giải phóng. Cố ý KHÔNG bắn <see cref="PlaybackEnded"/>: người gọi dừng là người đang
    /// điều khiển luồng (chạm trần an toàn, sang câu khác), bắn thêm sự kiện "đã phát xong" ở đây sẽ
    /// đẩy luồng thi đi tiếp một lần nữa.
    /// </summary>
    public void Stop()
    {
        WaveOut? output;
        lock (_sync)
        {
            output = _output;
            if (output is not null)
            {
                output.PlaybackStopped -= HandlePlaybackStopped;
            }
        }

        try
        {
            output?.Stop();
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("asset_audio", "stop_failed", ex);
        }

        DisposePlayback();
    }

    private void HandlePlaybackStopped(object? sender, StoppedEventArgs e)
    {
        DisposePlayback();

        if (e.Exception is not null)
        {
            LocalFileLogger.Error("asset_audio", "playback_stopped_with_error", e.Exception);
            PlaybackFailed?.Invoke(e.Exception.Message);
            return;
        }

        PlaybackEnded?.Invoke();
    }

    private void DisposePlayback()
    {
        WaveOut? output;
        MediaFoundationReader? reader;
        lock (_sync)
        {
            output = _output;
            reader = _reader;
            _output = null;
            _reader = null;
        }

        try
        {
            output?.Dispose();
            reader?.Dispose();
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("asset_audio", "dispose_failed", ex);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }

        Stop();
    }
}
