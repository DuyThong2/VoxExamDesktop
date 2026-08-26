using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Infra.Devices;

namespace VoxOralExam.DesktopApp.Services.ExamFlow.Turn;

internal sealed class SpeechTurnCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly RealtimeSessionClient _sessionClient;
    private readonly TurnAudioRecorder _recorder;
    private TaskCompletionSource<bool> _speechStarted = NewSignal();
    private TaskCompletionSource<bool> _speechEnded = NewSignal();
    private ISpeechBudget? _budget;
    private bool _speechWindowOpen;
    private bool _isSpeaking;
    private bool _disposed;

    public SpeechTurnCoordinator(
        RealtimeSessionClient sessionClient,
        TurnAudioRecorder recorder)
    {
        _sessionClient = sessionClient;
        _recorder = recorder;
        _sessionClient.OnVadSpeechStart += HandleSpeechStart;
        _sessionClient.OnVadSpeechEnd += HandleSpeechEnd;
    }

    public event Action<bool>? StudentSpeakingChanged;

    public bool IsSpeechWindowOpen
    {
        get
        {
            lock (_sync)
            {
                return _speechWindowOpen;
            }
        }
    }

    public Task SpeechStartedTask
    {
        get
        {
            lock (_sync)
            {
                return _speechStarted.Task;
            }
        }
    }

    public void OpenSpeechWindow(ISpeechBudget budget)
    {
        lock (_sync)
        {
            _budget = budget;
            _speechWindowOpen = true;
            _isSpeaking = false;
            _speechStarted = NewSignal();
            _speechEnded = NewSignal();
        }
    }

    public void CloseSpeechWindow()
    {
        ISpeechBudget? budget;
        lock (_sync)
        {
            _speechWindowOpen = false;
            _isSpeaking = false;
            budget = _budget;
            _budget = null;
            _speechStarted.TrySetResult(false);
            _speechEnded.TrySetResult(false);
        }
        budget?.StopSpeaking();
        StudentSpeakingChanged?.Invoke(false);
    }

    /// <summary>
    /// Rescues the recorder's turn buffer after CaptureAsync unwound on cancellation without ever
    /// reaching CompleteCapture -- the student was mid-answer when the exam clock hit zero, when
    /// they pressed "Nop bai", or when proctoring force-ended the attempt. Nobody has drained the
    /// recorder at that point, and ExamAttemptRunner's finally is about to call recorder.StopAsync()
    /// which clears the buffer, so without this the answer is simply gone.
    ///
    /// Returns null when there is nothing to rescue -- either no turn was active, or CaptureAsync
    /// completed normally and already drained it. Never throws: the caller is on a shutdown path
    /// and losing one answer is preferable to derailing the submit.
    ///
    /// Cannot double-drain with CompleteCapture: TurnAudioRecorder.GetTurnBufferAndReset clears
    /// _turnBuffer and _isTurnActive under the recorder's own lock, so whichever of the two runs
    /// first is the only one that ever sees IsTurnActive == true.
    /// </summary>
    public CapturedTurn? TrySalvageInFlightCapture(int turnOrder)
    {
        ISpeechBudget? budget;
        byte[] pcm;
        try
        {
            lock (_sync)
            {
                // Close the window and read the buffer inside the SAME lock HandleSpeechStart takes.
                // MicAudioStreamer is still streaming here (ExamAttemptRunner only stops it in its
                // finally) and CaptureAsync throws without calling CloseSpeechWindow, so
                // _speechWindowOpen is still true -- a vad_speech_start landing right now would
                // reach _recorder.BeginTurnCapture(), which clears _turnBuffer, destroying exactly
                // the audio we came here to save.
                _speechWindowOpen = false;
                _isSpeaking = false;
                budget = _budget;
                _budget = null;
                _speechStarted.TrySetResult(false);
                _speechEnded.TrySetResult(false);

                pcm = _recorder.IsTurnActive ? _recorder.GetTurnBufferAndReset() : [];
            }

            budget?.StopSpeaking();
            StudentSpeakingChanged?.Invoke(false);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "turn_salvage_failed", ex, new { turnOrder });
            return null;
        }

        if (pcm.Length == 0)
        {
            LocalFileLogger.Info("exam_flow", "turn_salvage_skipped", new
            {
                turnOrder,
                reason = "no_active_turn"
            });
            return null;
        }

        // GetTurnBufferAndReset just stopped the recorder's own stopwatch and published this
        // capture's wall-clock length -- a more honest duration than a budget.ElapsedSeconds delta,
        // which needs the elapsedAtStart local that CaptureAsync took with it when it unwound (and
        // reads from a budget HandleForceEnded may already have detached via CloseSpeechWindow).
        return new CapturedTurn(
            turnOrder,
            pcm,
            _recorder.LastTurnDurationSeconds,
            SpeechCaptureEndReason.Salvaged);
    }

    public async Task<CapturedTurn> CaptureAsync(
        int turnOrder,
        TimeSpan initialTimeout,
        TimeSpan overallTimeout,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
    {
        ISpeechBudget? budget;
        Task speechStartedTask;
        lock (_sync)
        {
            budget = _budget;
            speechStartedTask = _speechStarted.Task;
        }

        if (!IsSpeechWindowOpen || budget is null)
        {
            return CompleteCapture(
                turnOrder,
                0,
                SpeechCaptureEndReason.SpeechWindowClosed);
        }

        if (budget.IsExceeded)
        {
            CloseSpeechWindow();
            return CompleteCapture(
                turnOrder,
                budget.ElapsedSeconds,
                SpeechCaptureEndReason.SpeechBudgetExceeded);
        }

        var elapsedAtStart = budget.ElapsedSeconds;
        var spoke = _recorder.IsTurnActive
            || await WaitForSpeechStartAsync(
                speechStartedTask,
                initialTimeout,
                overallTimeout,
                cancellationToken);
        if (!spoke)
        {
            CloseSpeechWindow();
            return CompleteCapture(
                turnOrder,
                budget.ElapsedSeconds - elapsedAtStart,
                SpeechCaptureEndReason.InitialSilenceTimeout);
        }

        var endReason = await WaitForSpeechEndWithGraceAsync(
            budget,
            overallTimeout,
            gracePeriod,
            cancellationToken);
        CloseSpeechWindow();
        return CompleteCapture(
            turnOrder,
            budget.ElapsedSeconds - elapsedAtStart,
            endReason);
    }

    private CapturedTurn CompleteCapture(
        int turnOrder,
        double durationSeconds,
        SpeechCaptureEndReason reason)
    {
        var pcm = _recorder.GetTurnBufferAndReset();
        return new CapturedTurn(
            turnOrder,
            pcm,
            Math.Round(Math.Max(0, durationSeconds), 2),
            reason);
    }

    private async Task<SpeechCaptureEndReason> WaitForSpeechEndWithGraceAsync(
        ISpeechBudget budget,
        TimeSpan overallTimeout,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + overallTimeout;
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return SpeechCaptureEndReason.OverallTimeout;
            }

            Task speechEndedTask;
            lock (_sync)
            {
                speechEndedTask = _speechEnded.Task;
            }

            var completed = await Task.WhenAny(
                speechEndedTask,
                budget.ExceededTask,
                Task.Delay(remaining, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();

            if (completed == budget.ExceededTask || budget.IsExceeded)
            {
                return SpeechCaptureEndReason.SpeechBudgetExceeded;
            }
            if (completed != speechEndedTask)
            {
                return SpeechCaptureEndReason.OverallTimeout;
            }

            remaining = deadline - DateTime.UtcNow;
            var grace = remaining < gracePeriod ? remaining : gracePeriod;
            if (grace <= TimeSpan.Zero)
            {
                return SpeechCaptureEndReason.Completed;
            }

            Task resumedTask;
            lock (_sync)
            {
                resumedTask = _isSpeaking
                    ? Task.CompletedTask
                    : _speechStarted.Task;
            }
            var resumed = await WaitForSignalAsync(
                resumedTask,
                grace,
                cancellationToken);
            if (!resumed)
            {
                return SpeechCaptureEndReason.Completed;
            }
        }
    }

    /// <summary>
    /// Chờ thí sinh cất tiếng, NGỪNG ĐẾM hạn im lặng trong lúc mất kết nối.
    ///
    /// <para>Vì sao: tín hiệu VAD do server bắn ra. Mất mạng thì không có tín hiệu nào tới, bất kể
    /// thí sinh có đang nói hay không -- nên đếm ngược trong khoảng đó là kết luận "không trả lời"
    /// từ một sự im lặng mà máy trạm không có cách nào quan sát được.</para>
    ///
    /// <para>Đo thật 2026-08-26, ca 01a03d85: đứt mạng lúc 17:04:39, thí sinh nói ngay sau đó,
    /// 17:04:53 lượt đóng với <c>capturedBytes: 0, durationSeconds: 0</c> -- đúng 12 giây hạn im
    /// lặng của lượt đầu, không một dòng <c>vad_speech_start</c> nào. Cả câu mất trắng.</para>
    ///
    /// <para>Cùng nguyên tắc với đồng hồ thi (<c>ExamViewModel</c> ngừng trừ giây khi
    /// <c>IsRealtimeAlive</c> false), nhưng hậu quả nặng hơn nên đáng vá riêng: mất giờ thì còn thi
    /// tiếp, mất lượt là mất câu.</para>
    ///
    /// <para>Hỏi CẢ <c>IsConnected</c> chứ không chỉ <c>IsServerAlive</c>: cái sau đo bằng "15 giây
    /// không nghe thấy gì", chậm hơn cả hạn im lặng 12 giây -- riêng nó thì tới lúc biết là mất
    /// mạng, lượt đã đóng xong rồi. <c>IsConnected</c> tắt ngay giây socket chết, còn luật im lặng
    /// giữ lại để bắt kiểu đứt mà socket vẫn báo Open.</para>
    ///
    /// <para><paramref name="wallClockCeiling"/> là trần tuyệt đối: mất mạng vĩnh viễn thì không
    /// được treo thí sinh vô hạn. Dùng chính hạn một lượt (<c>QuestionTurnTimeoutSeconds</c>), vì
    /// một lượt vốn không được phép dài hơn thế.</para>
    /// </summary>
    private async Task<bool> WaitForSpeechStartAsync(
        Task signal,
        TimeSpan silenceBudget,
        TimeSpan wallClockCeiling,
        CancellationToken cancellationToken)
    {
        var tick = TimeSpan.FromMilliseconds(250);
        var deadline = DateTime.UtcNow + wallClockCeiling;
        var remaining = silenceBudget;
        var paused = false;

        while (remaining > TimeSpan.Zero)
        {
            if (DateTime.UtcNow >= deadline)
            {
                LocalFileLogger.Info("exam_flow", "han_im_lang_het_tran_cho", new
                {
                    wallClockCeilingSeconds = wallClockCeiling.TotalSeconds,
                    remainingBudgetSeconds = Math.Round(remaining.TotalSeconds, 1)
                });
                return false;
            }

            var step = remaining < tick ? remaining : tick;
            var completed = await Task.WhenAny(signal, Task.Delay(step, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (completed == signal)
            {
                return true;
            }

            if (_sessionClient.IsConnected && _sessionClient.IsServerAlive)
            {
                if (paused)
                {
                    paused = false;
                    LocalFileLogger.Info("exam_flow", "han_im_lang_chay_tiep", new
                    {
                        remainingSeconds = Math.Round(remaining.TotalSeconds, 1)
                    });
                }
                remaining -= step;
            }
            else if (!paused)
            {
                paused = true;
                LocalFileLogger.Info("exam_flow", "han_im_lang_tam_dung_mat_ket_noi", new
                {
                    remainingSeconds = Math.Round(remaining.TotalSeconds, 1)
                });
            }
        }

        return false;
    }

    private static async Task<bool> WaitForSignalAsync(
        Task signal,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var delay = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(signal, delay);
        cancellationToken.ThrowIfCancellationRequested();
        return completed == signal;
    }

    private void HandleSpeechStart()
    {
        ISpeechBudget? budget;
        lock (_sync)
        {
            if (!_speechWindowOpen)
            {
                // TẠM THỜI, THÊM 2026-08-26 ĐỂ CHẨN ĐOÁN -- xem chú thích ở HandleSpeechEnd.
                LocalFileLogger.Info("exam_flow", "vad_start_ignored_window_closed", null);
                return;
            }

            _isSpeaking = true;

            // CHỈ thay tín hiệu kết thúc khi bản cũ ĐÃ được bắn.
            //
            // Trước bản này dòng dưới thay mới vô điều kiện, và nó bỏ rơi người đang chờ:
            // WaitForSpeechEndWithGraceAsync CHỤP `_speechEnded.Task` rồi mới await. Thay đối
            // tượng lúc nó đang đậu nghĩa là cái `end` sau đó bắn vào bản MỚI, còn người chờ vẫn
            // ôm bản CŨ -- không bao giờ tỉnh, lượt chạy tới hết QuestionTurnTimeoutSeconds (180s).
            //
            // Bình thường không lộ ra vì VAD luôn xen kẽ start -> end -> start: sau mỗi `end`, vòng
            // lặp quay lên đầu và đọc lại `_speechEnded`, nên luôn ôm bản mới nhất.
            //
            // NỐI LẠI GIỮA LÚC ĐANG NÓI thì phá đúng giả định đó: phiên Voice Live mới phát
            // `vad_speech_start` từ đầu, trong khi client vẫn đang chờ `end` của đoạn nói TRƯỚC lúc
            // đứt. Một `start` không có `end` đi trước là tình huống duy nhất gây lỗi này.
            //
            // Đo thật 2026-08-26, ca 01a03d3f: nối lại lúc 15:47:01, `vad_speech_start` 15:47:03,
            // `vad_speech_end` 15:47:04 -- tín hiệu TỚI ĐỦ, không có dòng ignored nào, mà lượt vẫn
            // chạy tiếp 57 giây tới lúc thí sinh bấm nộp.
            if (_speechEnded.Task.IsCompleted)
            {
                _speechEnded = NewSignal();
            }

            budget = _budget;
            _speechStarted.TrySetResult(true);
        }

        if (!_recorder.IsTurnActive)
        {
            _recorder.BeginTurnCapture();
        }
        budget?.StartSpeaking();
        StudentSpeakingChanged?.Invoke(true);
    }

    private void HandleSpeechEnd()
    {
        ISpeechBudget? budget;
        lock (_sync)
        {
            if (!_speechWindowOpen)
            {
                // TẠM THỜI, THÊM 2026-08-26 ĐỂ CHẨN ĐOÁN -- xoá khi xong.
                //
                // Nhánh thoát im lặng này là ứng viên hàng đầu cho ca "nối lại xong thì đứng":
                // vad_speech_end tới nhưng cửa sổ đã đóng nên bị bỏ, lượt không bao giờ kết thúc,
                // chạy 96 giây tới lúc thí sinh bấm nộp. Ghi log để phân biệt với trường hợp
                // vad_speech_end KHÔNG hề tới (xem log cùng tên ở RealtimeSessionClient).
                LocalFileLogger.Info(
                    "exam_flow",
                    "vad_end_ignored_window_closed",
                    new { isSpeaking = _isSpeaking });
                return;
            }

            _isSpeaking = false;
            budget = _budget;
            _speechEnded.TrySetResult(true);
            _speechStarted = NewSignal();
        }

        budget?.StopSpeaking();
        StudentSpeakingChanged?.Invoke(false);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CloseSpeechWindow();
        _sessionClient.OnVadSpeechStart -= HandleSpeechStart;
        _sessionClient.OnVadSpeechEnd -= HandleSpeechEnd;
    }
}
