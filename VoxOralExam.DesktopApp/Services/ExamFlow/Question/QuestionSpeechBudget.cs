using VoxOralExam.DesktopApp.Services.ExamFlow.Turn;

namespace VoxOralExam.DesktopApp.Services.ExamFlow.Question;

internal sealed class QuestionSpeechBudget : ISpeechBudget, IDisposable
{
    private readonly object _sync = new();
    private readonly TimeSpan _limit;
    private readonly TaskCompletionSource _exceeded =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Timer _progressTimer;
    private readonly Func<bool> _isConnectionAlive;
    private TimeSpan _elapsed = TimeSpan.Zero;
    // Mốc bắt đầu của đoạn CHƯA được cộng vào _elapsed, không phải mốc bắt đầu cả lượt nói: mỗi lần
    // Accrue chạy là nó được dời tới hiện tại. Xem Accrue.
    private DateTime? _speakingStartedAtUtc;
    private bool _disposed;

    /// <param name="isConnectionAlive">
    /// Kênh realtime tới server còn sống hay không. Ngân sách CHỈ cộng giờ khi còn sống: mất mạng
    /// thì server không nghe được gì, tính vào hạn mức nói của thí sinh là tính oan.
    ///
    /// <para>Vì sao phải là hàm chứ không phải một cờ đọc một lần: trạng thái đổi giữa chừng lượt
    /// nói, mà ngân sách được hỏi liên tục qua bộ đếm 250ms.</para>
    ///
    /// <para>Bỏ trống thì coi như luôn sống, giữ nguyên hành vi cũ.</para>
    /// </param>
    public QuestionSpeechBudget(
        int maxResponseSeconds,
        double initialElapsedSeconds = 0,
        Func<bool>? isConnectionAlive = null)
    {
        _isConnectionAlive = isConnectionAlive ?? (static () => true);
        _limit = TimeSpan.FromSeconds(Math.Max(0, maxResponseSeconds));
        _elapsed = TimeSpan.FromSeconds(Math.Max(0, initialElapsedSeconds));
        _progressTimer = new Timer(
            _ => PublishProgress(),
            null,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(250));
    }

    public event Action<TimeSpan, TimeSpan>? ProgressChanged;

    public Task ExceededTask => _exceeded.Task;

    public double ElapsedSeconds
    {
        get
        {
            lock (_sync)
            {
                return GetElapsedLocked(DateTime.UtcNow).TotalSeconds;
            }
        }
    }

    public bool IsExceeded
    {
        get
        {
            lock (_sync)
            {
                return IsExceededLocked(DateTime.UtcNow);
            }
        }
    }

    public void StartSpeaking()
    {
        lock (_sync)
        {
            if (!_disposed && _speakingStartedAtUtc is null)
            {
                _speakingStartedAtUtc = DateTime.UtcNow;
            }
        }
        PublishProgress();
    }

    public void StopSpeaking()
    {
        lock (_sync)
        {
            // Qua AccrueLocked chứ không cộng thẳng: đoạn cuối cùng cũng phải tôn trọng trạng thái
            // kết nối như mọi đoạn khác.
            AccrueLocked(DateTime.UtcNow);
            _speakingStartedAtUtc = null;
        }
        PublishProgress();
    }

    private void PublishProgress()
    {
        TimeSpan elapsed;
        bool exceeded;
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            elapsed = GetElapsedLocked(now);
            exceeded = IsExceededLocked(now);
        }

        if (exceeded)
        {
            _exceeded.TrySetResult();
        }
        ProgressChanged?.Invoke(elapsed, _limit);
    }

    /// <summary>
    /// Dồn khoảng thời gian kể từ lần dồn trước vào <c>_elapsed</c> -- nhưng CHỈ khi kết nối còn
    /// sống -- rồi dời mốc tới hiện tại.
    ///
    /// <para>Đây là lý do phải tích luỹ dần thay vì tính ngược từ mốc bắt đầu lượt nói. Một đoạn
    /// nói bị mất mạng giữa chừng gồm cả phần server nghe được lẫn phần không; tính ngược từ mốc
    /// đầu thì hai phần đó dính liền, không tách ra được nữa. Dồn đều đặn thì phần chết đơn giản là
    /// không bao giờ được cộng vào.</para>
    ///
    /// <para>Dời mốc VÔ ĐIỀU KIỆN kể cả khi mất kết nối: có vậy thời gian chết mới bị bỏ qua thay
    /// vì dồn lại rồi cộng một cục lúc nối lại được.</para>
    ///
    /// <para>Bộ đếm 250ms gọi hàm này, nên nhiều nhất chỉ rò rỉ 250ms quanh mỗi lần đổi trạng thái.</para>
    /// </summary>
    private void AccrueLocked(DateTime now)
    {
        if (_speakingStartedAtUtc is not DateTime startedAt)
        {
            return;
        }

        if (_isConnectionAlive())
        {
            _elapsed += now - startedAt;
        }

        _speakingStartedAtUtc = now;
    }

    private TimeSpan GetElapsedLocked(DateTime now)
    {
        AccrueLocked(now);
        return _elapsed < TimeSpan.Zero ? TimeSpan.Zero : _elapsed;
    }

    private bool IsExceededLocked(DateTime now) =>
        _limit > TimeSpan.Zero && GetElapsedLocked(now) >= _limit;

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        StopSpeaking();
        _progressTimer.Dispose();
        PublishProgress();
    }
}
