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
    public event Action<TimeSpan, TimeSpan>? QuestionSpeakingTimeChanged;

    /// <summary>
    /// True while the attempt is saving the student's final answer and closing out the session,
    /// false once the status has been submitted. Drives the "dang luu" overlay.
    /// </summary>
    public event Action<bool>? FinalSaveStateChanged;

    public bool IsMicMuted => _recorder?.IsMuted ?? _isMicMuted;

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
    private async Task<int> WaitForRemoteArchivesAsync(TimeSpan timeout)
    {
        if (_sessionState.ExamAttemptId == Guid.Empty)
        {
            return 0;
        }

        var deadline = DateTime.UtcNow + timeout;
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

    private void HandleForceEnded(string reason)
    {
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

    private void HandleStatusChanged(string value) =>
        StatusChanged?.Invoke(value);

    private void HandleTranscriptAppended(string value) =>
        TranscriptAppended?.Invoke(value);

    private void HandleSpeakingTimeChanged(TimeSpan elapsed, TimeSpan limit) =>
        QuestionSpeakingTimeChanged?.Invoke(elapsed, limit);
}
