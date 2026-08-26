using VoxOralExam.Core.Interfaces;
using VoxOralExam.Core.Models.Dtos;
using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.Services.DomainService;
using VoxOralExam.DesktopApp.Services.ExamFlow.Question;
using VoxOralExam.DesktopApp.Services.ExamFlow.Turn;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services.ExamFlow.Attempt;

internal sealed class ExamAttemptRunner
{
    private readonly TurnAudioUploader _audioUploader;
    private readonly TurnArchiveClient _archiveClient;
    private readonly ExamSessionState _sessionState;
    private readonly AppSettings _settings;
    private readonly RealtimeSessionClient _sessionClient;
    private readonly AvatarWebRtcClient _avatarClient;
    private readonly LocalAvatarSpeaker _avatarSpeaker;
    private readonly IExamApiService _examApi;
    private readonly QuestionAssetPresentationCoordinator _assets;
    private readonly IProctoringService _proctoring;
    private readonly RealtimeAttemptProgressClient _attemptProgress;
    private CancellationTokenSource? _runCancellation;
    private TurnAudioRecorder? _recorder;
    private SpeechTurnCoordinator? _speechTurns;
    private bool _isMicMuted;
    private bool _stopRequested;
    private bool _forceEndRequested;
    private bool _submitRequested;
    private bool _proctoringStarted;

    public ExamAttemptRunner(
        TurnAudioUploader audioUploader,
        TurnArchiveClient archiveClient,
        ExamSessionState sessionState,
        AppSettings settings,
        RealtimeSessionClient sessionClient,
        AvatarWebRtcClient avatarClient,
        LocalAvatarSpeaker avatarSpeaker,
        IExamApiService examApi,
        QuestionAssetPresentationCoordinator assets,
        IProctoringService proctoring,
        RealtimeAttemptProgressClient attemptProgress,
        bool isMicMuted)
    {
        _audioUploader = audioUploader;
        _archiveClient = archiveClient;
        _sessionState = sessionState;
        _settings = settings;
        _sessionClient = sessionClient;
        _avatarClient = avatarClient;
        _avatarSpeaker = avatarSpeaker;
        _examApi = examApi;
        _assets = assets;
        _proctoring = proctoring;
        _attemptProgress = attemptProgress;
        _isMicMuted = isMicMuted;
    }

    public event Action<ExamQuestionPrompt>? QuestionPresented;
    public event Action<string>? TranscriptAppended;
    public event Action<string>? StatusChanged;
    public event Action? SessionReady;
    public event Action<bool>? ExamEnded;
    public event Action<bool>? StudentSpeakingChanged;
    public event Action<bool>? AvatarSpeakingChanged;
    /// <summary>Lời avatar vừa bắt đầu đọc -- xem QuestionPresentationService.AvatarUtteranceStarted.</summary>
    public event Action<string>? AvatarUtteranceChanged;
    public event Action<TimeSpan, TimeSpan>? QuestionSpeakingTimeChanged;

    /// <summary>
    /// True while the attempt is saving the student's final answer and closing out the session,
    /// false once the status has been submitted. Drives the "dang luu" overlay.
    /// </summary>
    public event Action<bool>? FinalSaveStateChanged;

    public bool IsMicMuted => _recorder?.IsMuted ?? _isMicMuted;

    public bool IsRealtimeAlive => _sessionClient.IsServerAlive;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        EnsureSessionInitialized();
        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var runToken = _runCancellation.Token;

        using var recorder = new TurnAudioRecorder(
            _settings.TurnAudioPreRollMilliseconds,
            _sessionState.SelectedAudioInputDeviceIndex);
        using var micStreamer = new MicAudioStreamer();
        using var archiveQueue = new TurnArchiveQueue(
            _audioUploader,
            _archiveClient);
        using var speechTurns = new SpeechTurnCoordinator(
            _sessionClient,
            recorder);
        using var presentation = new QuestionPresentationService(
            _sessionClient,
            _avatarSpeaker,
            _assets);
        var questionRunner = new QuestionFlowRunner(
            _sessionState,
            _settings,
            _sessionClient,
            presentation,
            speechTurns,
            archiveQueue,
            // Submit: the student is watching a "saving" overlay, so pay for the answer to make
            // it all the way to Java. Stop / force-end: the window is closing or Python has
            // already hung up, so fail fast.
            () => TimeSpan.FromSeconds(_submitRequested
                ? _settings.SubmitTurnEndGraceSeconds
                : _settings.InterruptTurnEndGraceSeconds));

        _recorder = recorder;
        _speechTurns = speechTurns;
        recorder.IsMuted = _isMicMuted;
        // Đứt mạng giữa lượt là bộ đệm audio phía Python mất sạch (nó nằm trong RAM của một
        // AttemptConnection, mỗi lần nối lại dựng đối tượng mới rỗng). Máy trạm là nơi duy nhất còn
        // giữ đủ audio, nên cho client nối lại tự chép ngược lên -- xem
        // RealtimeSessionClient.ResyncTurnAudioAsync.
        _sessionClient.CurrentTurnAudioProvider = recorder.PeekTurnBufferFrom;
        // ExamViewModel giữ ExamSessionState.RemainingSeconds luôn khớp đồng hồ đang chạy, nên đọc
        // thẳng ở đây là ra số hiện tại -- dùng làm mốc hoàn giờ khi bị ngắt giữa câu.
        _sessionClient.CurrentRemainingSecondsProvider = () => _sessionState.RemainingSeconds;
        WireRuntimeEvents(speechTurns, presentation, questionRunner);
        WireTransportEvents();
        presentation.Start();

        try
        {
            await StartProctoringAsync(runToken);
            await recorder.StartAsync(runToken);
            StatusChanged?.Invoke(
                TurnAudioRecorder.DescribeInputDevice(
                    _sessionState.SelectedAudioInputDeviceIndex));

            StatusChanged?.Invoke("Đang kết nối phiên realtime...");
            await _sessionClient.ConnectAsync(
                _sessionState.ExamAttemptId,
                runToken);

            if (_settings.EnableAvatarWebRtc)
            {
                StatusChanged?.Invoke("Đang kết nối avatar...");
                await _avatarClient.ConnectAsync(
                    _sessionState.ExamAttemptId,
                    runToken);
            }

            SessionReady?.Invoke();
            micStreamer.Start(recorder, _sessionClient);

            for (;
                 _sessionState.QuestionIndex < _sessionState.Questions.Count;
                 _sessionState.QuestionIndex++)
            {
                runToken.ThrowIfCancellationRequested();
                var prompt = PresentCurrentQuestion();
                await questionRunner.RunAsync(prompt, runToken);
            }

            StatusChanged?.Invoke("Đã hoàn thành bài vấn đáp.");
            await presentation.WaitForAvatarAfterAsync(
                token => _sessionClient.SendExamEndAndWaitForAckAsync(token),
                runToken);
            // Even the clean finish drains and settles before the status goes up, so tell the
            // student rather than leaving them on a frozen-looking screen.
            FinalSaveStateChanged?.Invoke(true);
            await CompleteAttemptAsync(
                archiveQueue,
                "SUBMITTED",
                drainTimeout: TimeSpan.FromSeconds(
                    _settings.FinalArchiveDrainTimeoutSeconds),
                settle: TimeSpan.FromSeconds(_settings.PostArchiveSettleSeconds),
                notifyEnded: true,
                completed: true);
        }
        catch (OperationCanceledException)
        {
            if (_submitRequested)
            {
                // Idempotent with the signal QuestionFlowRunner may already have raised from a
                // salvage -- the ViewModel just sets a bool. Raised here too so the overlay also
                // appears when there was nothing to salvage, since exam_end and the drain still
                // take real time.
                FinalSaveStateChanged?.Invoke(true);
                try
                {
                    await SendFarewellBestEffortAsync(presentation);
                }
                finally
                {
                    await CompleteAttemptAsync(
                        archiveQueue,
                        "SUBMITTED",
                        drainTimeout: TimeSpan.FromSeconds(
                            _settings.FinalArchiveDrainTimeoutSeconds),
                        settle: TimeSpan.FromSeconds(_settings.PostArchiveSettleSeconds),
                        notifyEnded: true,
                        completed: true);
                }
            }
            else
            {
                await CompleteAttemptAsync(
                    archiveQueue,
                    "INTERRUPTED",
                    // Shorter on the stop path: this runs inside ExamWindow_Closing's awaited
                    // cleanup, so it is literally how long the window takes to disappear. No
                    // settle either -- nothing is being graded on an interrupted attempt.
                    drainTimeout: TimeSpan.FromSeconds(
                        _settings.FinalArchiveDrainTimeoutSeconds),
                    settle: TimeSpan.Zero,
                    notifyEnded: _forceEndRequested || !_stopRequested,
                    completed: false);
            }
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "run_failed", ex);
            await CompleteAttemptAsync(
                archiveQueue,
                "INTERRUPTED",
                drainTimeout: TimeSpan.FromSeconds(
                    _settings.FinalArchiveDrainTimeoutSeconds),
                settle: TimeSpan.Zero,
                notifyEnded: !_stopRequested,
                completed: false);
            throw;
        }
        finally
        {
            micStreamer.Stop();
            presentation.Dispose();
            speechTurns.CloseSpeechWindow();
            _avatarSpeaker.Stop();
            await recorder.StopAsync();
            if (_settings.EnableAvatarWebRtc)
            {
                await _avatarClient.DisconnectAsync();
            }
            await _sessionClient.CloseAsync();
            await StopProctoringAsync();
            UnwireTransportEvents();
            UnwireRuntimeEvents(speechTurns, presentation, questionRunner);
            StudentSpeakingChanged?.Invoke(false);
            AvatarSpeakingChanged?.Invoke(false);
            _speechTurns = null;
            _recorder = null;
            // Recorder sắp bị Dispose; giữ delegate trỏ vào nó là chép ngược từ một đối tượng đã chết.
            _sessionClient.CurrentTurnAudioProvider = null;
            _sessionClient.CurrentRemainingSecondsProvider = null;
            _runCancellation.Dispose();
            _runCancellation = null;
        }
    }

    public void RequestStop()
    {
        _stopRequested = true;
        _runCancellation?.Cancel();
    }

    public void RequestSubmit()
    {
        _submitRequested = true;
        _runCancellation?.Cancel();
    }

    public async Task ReportFocusLostAsync(DateTimeOffset capturedAt)
    {
        try
        {
            await _sessionClient.SendFocusLostAsync(capturedAt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // WS có thể đang reconnect đúng lúc thí sinh chuyển cửa sổ. Mất một cảnh báo chấp
            // nhận được; bằng chứng vẫn nằm trong log máy trạm do WindowFocusGuard ghi.
            LocalFileLogger.Error("exam_flow", "focus_lost_send_failed", ex);
        }
    }

    public async Task ReportCameraSignalLostAsync(DateTimeOffset capturedAt, bool neverDelivered)
    {
        try
        {
            await _sessionClient
                .SendCameraSignalLostAsync(capturedAt, neverDelivered, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Cùng chính sách với focus lost: mất một cảnh báo còn hơn làm gián đoạn bài thi.
            // CameraSignalGuard đã ghi sự việc vào log máy trạm trước khi gọi tới đây.
            LocalFileLogger.Error("exam_flow", "camera_signal_lost_send_failed", ex);
        }
    }

    public async Task ReportCameraSignalRestoredAsync(DateTimeOffset capturedAt, TimeSpan outage)
    {
        try
        {
            await _sessionClient
                .SendCameraSignalRestoredAsync(capturedAt, outage, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "camera_signal_restored_send_failed", ex);
        }
    }

    public async Task ReportAssetPlaybackFailedAsync(string reason, int questionNumber)
    {
        try
        {
            await _sessionClient
                .SendAssetPlaybackFailedAsync(reason, questionNumber, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "asset_playback_failed_send_failed", ex);
        }
    }

    public void SetMicMuted(bool muted)
    {
        _isMicMuted = muted;
        if (_recorder is not null)
        {
            _recorder.IsMuted = muted;
        }
    }

    private async Task CompleteAttemptAsync(
        TurnArchiveQueue archiveQueue,
        string status,
        TimeSpan drainTimeout,
        TimeSpan settle,
        bool notifyEnded,
        bool completed)
    {
        LocalFileLogger.Info("exam_flow", "final_drain_begin", new
        {
            status,
            drainSeconds = drainTimeout.TotalSeconds
        });
        var stillPending = await archiveQueue.DrainAsync(drainTimeout);

        // Chờ THẬT nằm ở đây: từ khi Python lưu audio, hàng đợi ngay trên luôn rỗng nên
        // DrainAsync là no-op. Việc còn dang dở nằm bên Python, hỏi thẳng nó.
        stillPending += await WaitForRemoteArchivesAsync(drainTimeout);

        LocalFileLogger.Info("exam_flow", "final_drain_complete", new { status, stillPending });

        if (stillPending > 0)
        {
            // Nộp bài với lượt chưa lưu xong KHÔNG được phép im lặng.
            //
            // Bài thi phải liên tục nên ở đây không chặn -- nhưng phải để lại dấu vết dứt khoát
            // để đối chiếu sau. Kể từ khi turn_publisher publish hai pha, lượt treo ở đây gần
            // như chắc chắn đã sang tới Java bằng pha sơ bộ; cái còn thiếu là bản ghi âm.
            LocalFileLogger.Error(
                "exam_flow",
                "submitted_with_pending_archives",
                new InvalidOperationException(
                    $"{stillPending} lượt chưa lưu xong khi nộp bài (drain {drainTimeout.TotalSeconds}s)"),
                new { status, stillPending, drainSeconds = drainTimeout.TotalSeconds });
        }

        if (settle > TimeSpan.Zero)
        {
            // Java runs the whole grading submission synchronously inside the PATCH that marks
            // this session SUBMITTED, snapshotting the answer's turn rows right then. A drained
            // archive only means Python HAS the turn -- it still has to notice it (it polls),
            // publish AnswerTurnsRecorded, and let Java's consumer commit the row. A turn that
            // lands after the PATCH exists in the database but is never graded.
            LocalFileLogger.Info("exam_flow", "submit_settle_delay", new
            {
                seconds = settle.TotalSeconds
            });
            await Task.Delay(settle);
        }

        await SubmitSessionStatusAsync(status);
        await StopProctoringAsync();
        // Before ExamEnded so the saving overlay is already down even if a subscriber throws.
        FinalSaveStateChanged?.Invoke(false);
        if (notifyEnded)
        {
            ExamEnded?.Invoke(completed);
        }
    }

    private async Task SendFarewellBestEffortAsync(
        QuestionPresentationService presentation)
    {
        // Was CancellationToken.None, which meant SendExamEndAndWaitForAckAsync's 180s ceiling
        // plus WaitForAvatarCompletionAsync's own 60s -- up to four minutes of a student watching
        // nothing happen after their exam ended.
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(1, _settings.SubmitFarewellTimeoutSeconds)));
        try
        {
            await presentation.WaitForAvatarAfterAsync(
                token => _sessionClient.SendExamEndAndWaitForAckAsync(token),
                deadline.Token);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error(
                "exam_flow",
                "submit_now_farewell_failed",
                ex);
        }
    }

    private async Task StartProctoringAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _proctoring.StartAsync(cancellationToken);
            _proctoringStarted = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "proctoring_start_failed", ex);
            StatusChanged?.Invoke(
                $"Không thể khởi động giám sát: {ex.Message}");
        }
    }

    private async Task StopProctoringAsync()
    {
        if (!_proctoringStarted)
        {
            return;
        }
        try
        {
            await _proctoring.StopAsync();
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "proctoring_stop_failed", ex);
        }
        finally
        {
            _proctoringStarted = false;
        }
    }

    /// <summary>
    /// Hỏi Python còn bao nhiêu lượt đang lưu dở, chờ tới khi hết hoặc hết giờ. Trả về số còn
    /// lại (0 = sạch).
    ///
    /// <para>Đây là bản thay thế cho cửa chờ cũ của TurnArchiveQueue. Từ 2026-08-13 audio lượt
    /// thi do Python lưu (upload S3 + phiên âm Azure), nên hàng đợi bên này không còn gì để
    /// chờ -- chờ ở đó là chờ hư không trong khi việc thật vẫn đang chạy ở nơi khác.</para>
    ///
    /// <para>KHÔNG chặn nộp bài: hết giờ thì đi tiếp và ghi lại. Bài thi phải liên tục, và bản
    /// thân lượt nói đã sang Java từ pha sơ bộ ngay lúc học sinh dứt lời -- thứ còn chờ ở đây
    /// chỉ là bản ghi âm và bản phiên âm của Azure.</para>
    /// </summary>
    /// <summary>
    /// Trần chờ số đếm nhích lên ở giai đoạn 1. Ngắn có chủ đích -- xem chú thích trong hàm.
    /// </summary>
    private static readonly TimeSpan AppearanceWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Trần riêng cho giai đoạn 0, TÁCH khỏi ngân sách chờ chính.
    ///
    /// <para>Vì có những lượt vĩnh viễn không bao giờ báo "đã lưu": bộ đệm audio rỗng lúc turn_end
    /// thì Python thoát sớm trước cả chỗ ghi vào trạng thái bền, nên hỏi bao lâu cũng vô ích. Chuyện
    /// này không hiếm -- thí sinh không trả lời câu cuối là dính, và mất bộ đệm do đổi kết nối giữa
    /// câu cũng dính.</para>
    ///
    /// <para>Dùng chung `deadline` thì mỗi ca như vậy giam thí sinh ở màn "đang lưu" trọn ngân sách
    /// VÀ bỏ đói hai giai đoạn sau. Bản mới của Python có đánh dấu riêng cho ca này, nên trần ở đây
    /// chỉ còn để chặn thiệt hại khi chạy với Python bản cũ.</para>
    /// </summary>
    private static readonly TimeSpan ArchiveProbeWait = TimeSpan.FromSeconds(8);

    private async Task<int> WaitForRemoteArchivesAsync(TimeSpan timeout)
    {
        if (_sessionState.ExamAttemptId == Guid.Empty)
        {
            return 0;
        }

        var deadline = DateTime.UtcNow + timeout;

        // GIAI ĐOẠN 0 -- hỏi ĐÍCH DANH lượt cuối đã lưu trữ xong chưa.
        //
        // Điểm mù của cách chờ cũ: `pending = 0` vừa có nghĩa "đã lưu xong" vừa có nghĩa "Python
        // còn chưa kịp nhận turn_end nên chưa spawn task nào". Hai chuyện ngược nhau, cùng một con
        // số. Các câu giữa bài không lộ ra vì sau đó còn cả đoạn AI đọc câu kế tiếp -- thừa thời
        // gian cho task nền chạy xong. Câu CUỐI thì không có khoảng đệm đó: gửi turn_end xong là
        // hỏi ngay, thấy 0, tưởng sạch, nộp bài luôn -- mà Java chấm đồng bộ ngay tại lần PATCH ấy.
        // Audio về sau cũng vô nghĩa, nên câu cuối mất đúng phần chấm phát âm của Azure.
        //
        // Đây mới là câu hỏi đúng. Hai giai đoạn dưới suy từ một con số đếm gộp, nên chỉ đúng gần
        // đúng; còn ở đây client biết chính xác nó vừa gửi lượt nào và hỏi thẳng về lượt đó.
        //
        // Trần RIÊNG (ArchiveProbeWait) chứ không dùng `deadline`: có những lượt vĩnh viễn không
        // bao giờ báo "đã lưu" -- xem chú thích của hằng đó.
        var lastTurn = _sessionClient.LastCompletedTurn;
        if (lastTurn is (Guid lastAnswerId, int lastTurnOrder))
        {
            var probeDeadline = DateTime.UtcNow + ArchiveProbeWait;
            var probeResolved = false;
            while (DateTime.UtcNow < probeDeadline && DateTime.UtcNow < deadline)
            {
                var (_, archived) = await _attemptProgress.GetArchiveStatusAsync(
                    _sessionState.ExamAttemptId, lastAnswerId, lastTurnOrder, CancellationToken.None);

                // null = KHÔNG BIẾT (server bản cũ, hoặc đọc lỗi). Bỏ giai đoạn này, rơi xuống
                // cách đếm bên dưới -- tuyệt đối không coi "không biết" là "chưa lưu" rồi giữ thí
                // sinh ở màn đang lưu cho tới hết giờ.
                if (archived is null)
                {
                    LocalFileLogger.Info("exam_flow", "archive_probe_unsupported", new { lastTurnOrder });
                    probeResolved = true;
                    break;
                }
                if (archived.Value)
                {
                    LocalFileLogger.Info("exam_flow", "last_turn_archived", new { lastTurnOrder });
                    probeResolved = true;
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }

            // Hết trần mà vẫn chưa "đã lưu". Ghi lại, vì đây là dấu hiệu DUY NHẤT phân biệt được
            // "audio về chậm thật" với "lượt này không bao giờ có audio" -- hai ca cần hai cách sửa
            // khác hẳn nhau, và nhìn từ ngoài chúng giống hệt nhau.
            if (!probeResolved)
            {
                LocalFileLogger.Info("exam_flow", "archive_probe_timeout", new
                {
                    lastTurnOrder,
                    waitedSeconds = ArchiveProbeWait.TotalSeconds
                });
            }
        }

        // GIAI ĐOẠN 1 -- chờ số đếm NHÍCH LÊN trước khi chờ nó về 0.
        //
        // Giữ lại làm lối lui cho khi giai đoạn 0 không dùng được (Python chưa deploy bản mới):
        // khi đó đây vẫn tốt hơn nguyên trạng, vì nó phân biệt được "chưa kịp bắt đầu" với "đã xong".
        var appearDeadline = DateTime.UtcNow + AppearanceWait;
        var everSawPending = false;
        while (DateTime.UtcNow < appearDeadline && DateTime.UtcNow < deadline)
        {
            var probe = await _attemptProgress.GetPendingArchiveCountAsync(
                _sessionState.ExamAttemptId, CancellationToken.None);
            if (probe > 0)
            {
                everSawPending = true;
                break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        // GIAI ĐOẠN 2 -- như cũ: chờ về 0 hoặc hết giờ.
        int pending;
        while (true)
        {
            pending = await _attemptProgress.GetPendingArchiveCountAsync(
                _sessionState.ExamAttemptId, CancellationToken.None);
            if (pending <= 0 || DateTime.UtcNow >= deadline)
            {
                break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        LocalFileLogger.Info("exam_flow", "remote_archive_wait_complete", new
        {
            pending,
            // false = chưa bao giờ thấy task nào chạy. Hoặc lượt cuối không có gì để lưu, hoặc
            // Python không nhận được turn_end. Đối chiếu với turn_salvage_skipped trong cùng log
            // để biết là cái nào.
            everSawPending,
            timeoutSeconds = timeout.TotalSeconds
        });
        return Math.Max(0, pending);
    }

    private async Task SubmitSessionStatusAsync(string status)
    {
        if (_sessionState.ExamAttemptId == Guid.Empty)
        {
            return;
        }
        try
        {
            await _examApi.UpdateSessionStatusAsync(
                _sessionState.ExamAttemptId,
                status,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error(
                "exam_flow",
                "session_status_submit_failed",
                ex,
                new { _sessionState.ExamAttemptId, status });
        }
    }

    private ExamQuestionPrompt PresentCurrentQuestion()
    {
        var question = _sessionState.CurrentQuestion
            ?? throw new InvalidOperationException(
                "Exam session does not contain an active question.");
        var prompt = new ExamQuestionPrompt
        {
            QuestionId = question.Id,
            InstructionText = question.InstructionText,
            QuestionText = question.QuestionText,
            MinResponseSeconds = question.MinResponseSeconds,
            MaxResponseSeconds = question.MaxResponseSeconds,
            QuestionNumber = _sessionState.QuestionIndex + 1,
            TotalQuestions = _sessionState.Questions.Count
        };
        QuestionPresented?.Invoke(prompt);
        return prompt;
    }

    private void EnsureSessionInitialized()
    {
        if (_sessionState.Questions.Count == 0)
        {
            throw new InvalidOperationException(
                "Exam session does not contain any questions.");
        }
        if (_sessionState.ExamAttemptId == Guid.Empty)
        {
            _sessionState.ExamAttemptId = Guid.NewGuid();
        }
        if (!Guid.TryParse(_sessionState.SessionId, out _))
        {
            _sessionState.SessionId = _sessionState.ExamAttemptId.ToString();
        }
    }

    /// <summary>
    /// Cửa vào từ đường HỎI SERVER (ExamViewModel poll mỗi vài giây), song song với tin
    /// <c>force_end</c> của WebSocket. Cùng gọi một hàm nên hai đường không thể lệch nhau.
    ///
    /// <para>Gọi lại nhiều lần vô hại: <c>_forceEndRequested</c> đã bật, còn
    /// <c>_runCancellation.Cancel()</c> vốn idempotent. Poll bắn trùng với tin WebSocket cũng
    /// không sao.</para>
    /// </summary>
    public void ForceEndFromServer(string reason)
    {
        LocalFileLogger.Info("exam_flow", "force_end_detected_by_poll", new { reason });
        HandleForceEnded(reason);
    }

    /// <summary>
    /// Chạy đúng MỘT lần cho mỗi phiên, dù bị gọi từ cả hai đường: tin <c>force_end</c> của
    /// WebSocket và vòng hỏi server của ExamViewModel. Cả hai đều còn sống nên chuyện gọi trùng
    /// là bình thường, không phải ngoại lệ -- đường nào tới trước thì đường đó làm.
    /// </summary>
    private void HandleForceEnded(string reason)
    {
        if (_forceEndRequested)
        {
            return;
        }
        _forceEndRequested = true;
        _speechTurns?.CloseSpeechWindow();
        _avatarSpeaker.Stop();
        AvatarSpeakingChanged?.Invoke(false);
        StatusChanged?.Invoke(
            "Bài thi đã tạm dừng để xem xét. Vui lòng liên hệ giám thị hoặc nhà trường.");
        _runCancellation?.Cancel();
    }

    private void HandleSessionError(string message) =>
        StatusChanged?.Invoke($"Lỗi phiên realtime: {message}");

    private void HandleSessionReconnecting() =>
        StatusChanged?.Invoke(
            "Mất kết nối realtime. Hệ thống vẫn đang thử kết nối lại...");

    private void HandleSessionReconnected(int lastArchivedTurnOrder) =>
        StatusChanged?.Invoke("Đã kết nối lại phiên realtime.");

    private void HandleAvatarReconnecting() =>
        StatusChanged?.Invoke("Mất kết nối avatar. Đang thử kết nối lại...");

    private void HandleAvatarReconnected() =>
        StatusChanged?.Invoke("Đã kết nối lại avatar.");

    private void WireTransportEvents()
    {
        _sessionClient.OnForceEnded += HandleForceEnded;
        _sessionClient.OnError += HandleSessionError;
        _sessionClient.OnReconnecting += HandleSessionReconnecting;
        _sessionClient.OnReconnected += HandleSessionReconnected;
        _avatarClient.OnReconnecting += HandleAvatarReconnecting;
        _avatarClient.OnReconnected += HandleAvatarReconnected;
    }

    private void UnwireTransportEvents()
    {
        _sessionClient.OnForceEnded -= HandleForceEnded;
        _sessionClient.OnError -= HandleSessionError;
        _sessionClient.OnReconnecting -= HandleSessionReconnecting;
        _sessionClient.OnReconnected -= HandleSessionReconnected;
        _avatarClient.OnReconnecting -= HandleAvatarReconnecting;
        _avatarClient.OnReconnected -= HandleAvatarReconnected;
    }

    private void WireRuntimeEvents(
        SpeechTurnCoordinator speechTurns,
        QuestionPresentationService presentation,
        QuestionFlowRunner questionRunner)
    {
        speechTurns.StudentSpeakingChanged += HandleStudentSpeakingChanged;
        presentation.StatusChanged += HandleStatusChanged;
        presentation.AvatarSpeakingChanged += HandleAvatarSpeakingChanged;
        presentation.AvatarUtteranceStarted += HandleAvatarUtteranceStarted;
        _assets.MediaPlaybackStateChanged += HandleAssetMediaPlaybackChanged;
        questionRunner.StatusChanged += HandleStatusChanged;
        questionRunner.TranscriptAppended += HandleTranscriptAppended;
        questionRunner.SpeakingTimeChanged += HandleSpeakingTimeChanged;
        questionRunner.FinalAnswerSaveStarted += HandleFinalAnswerSaveStarted;
    }

    private void UnwireRuntimeEvents(
        SpeechTurnCoordinator speechTurns,
        QuestionPresentationService presentation,
        QuestionFlowRunner questionRunner)
    {
        speechTurns.StudentSpeakingChanged -= HandleStudentSpeakingChanged;
        presentation.StatusChanged -= HandleStatusChanged;
        presentation.AvatarSpeakingChanged -= HandleAvatarSpeakingChanged;
        presentation.AvatarUtteranceStarted -= HandleAvatarUtteranceStarted;
        _assets.MediaPlaybackStateChanged -= HandleAssetMediaPlaybackChanged;
        questionRunner.StatusChanged -= HandleStatusChanged;
        questionRunner.TranscriptAppended -= HandleTranscriptAppended;
        questionRunner.SpeakingTimeChanged -= HandleSpeakingTimeChanged;
        questionRunner.FinalAnswerSaveStarted -= HandleFinalAnswerSaveStarted;
    }

    // The salvage fires on every cancellation path, but only a submit should tell the student we
    // are saving their final answer. A stop is the window closing, and a force-end already shows
    // "bai thi da tam dung de xem xet" -- covering that with a saving overlay would misinform them.
    private void HandleFinalAnswerSaveStarted()
    {
        if (_submitRequested)
        {
            FinalSaveStateChanged?.Invoke(true);
        }
    }

    private void HandleStudentSpeakingChanged(bool value) =>
        StudentSpeakingChanged?.Invoke(value);

    private void HandleAvatarSpeakingChanged(bool value) =>
        AvatarSpeakingChanged?.Invoke(value);

    private void HandleAvatarUtteranceStarted(string value) =>
        AvatarUtteranceChanged?.Invoke(value);

    /// <summary>
    /// Tắt mic trong lúc asset audio/video đang phát, để tiếng loa không bị chép thành lời thí sinh.
    ///
    /// <para>Mic thu LIÊN TỤC bất kể lượt nói có mở hay không (xem
    /// <c>TurnAudioRecorder.StreamChunkAvailable</c>), Python nhồi thẳng vào bộ đệm lượt, và
    /// <c>WaveIn</c> không khử vọng -- nên mọi thứ loa phát ra giữa <c>question_start</c> và
    /// <c>turn_end</c> đều nằm trong transcript lượt đó và trong file WAV dùng chấm phát âm.</para>
    ///
    /// <para>Khi hết phát thì trả về đúng lựa chọn của thí sinh (<c>_isMicMuted</c>), KHÔNG phải
    /// <c>false</c> -- nếu không thì thí sinh đang tự tắt mic sẽ bị bật lại sau mỗi đoạn nghe.</para>
    /// </summary>
    private void HandleAssetMediaPlaybackChanged(bool isPlaying)
    {
        if (_recorder is null)
        {
            return;
        }
        _recorder.IsMuted = isPlaying || _isMicMuted;
    }

    private void HandleStatusChanged(string value) =>
        StatusChanged?.Invoke(value);

    private void HandleTranscriptAppended(string value) =>
        TranscriptAppended?.Invoke(value);

    private void HandleSpeakingTimeChanged(TimeSpan elapsed, TimeSpan limit) =>
        QuestionSpeakingTimeChanged?.Invoke(elapsed, limit);
}
