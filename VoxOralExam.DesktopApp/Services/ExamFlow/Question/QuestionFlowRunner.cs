using VoxOralExam.Core.Models;
using VoxOralExam.Core.Models.Dtos;
using VoxOralExam.DesktopApp.Dtos;
using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.Services.ExamFlow.Turn;
using VoxOralExam.DesktopApp.State;
using ExamQuestion = VoxOralExam.Core.Models.Question;

namespace VoxOralExam.DesktopApp.Services.ExamFlow.Question;

internal sealed class QuestionFlowRunner
{
    private static readonly HashSet<string> ClarificationReasons =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "clarify_prompt",
            "decline_repair",
            "remind_respectfully"
        };

    private readonly ExamSessionState _sessionState;
    private readonly AppSettings _settings;
    private readonly RealtimeSessionClient _sessionClient;
    private readonly QuestionPresentationService _presentation;
    private readonly SpeechTurnCoordinator _speechTurns;
    private readonly TurnArchiveQueue _archiveQueue;
    private readonly Func<TimeSpan> _shutdownGrace;

    public QuestionFlowRunner(
        ExamSessionState sessionState,
        AppSettings settings,
        RealtimeSessionClient sessionClient,
        QuestionPresentationService presentation,
        SpeechTurnCoordinator speechTurns,
        TurnArchiveQueue archiveQueue,
        Func<TimeSpan> shutdownGrace)
    {
        _sessionState = sessionState;
        _settings = settings;
        _sessionClient = sessionClient;
        _presentation = presentation;
        _speechTurns = speechTurns;
        _archiveQueue = archiveQueue;
        _shutdownGrace = shutdownGrace;
    }

    public event Action<string>? StatusChanged;
    public event Action<string>? TranscriptAppended;
    public event Action<TimeSpan, TimeSpan>? SpeakingTimeChanged;

    /// <summary>
    /// Raised the moment an aborted answer is rescued, so the UI can show its "saving" state
    /// without waiting for this method to unwind into ExamAttemptRunner's catch.
    /// </summary>
    public event Action? FinalAnswerSaveStarted;

    public async Task<QuestionFlowResult> RunAsync(
        ExamQuestionPrompt prompt,
        CancellationToken cancellationToken)
    {
        var question = _sessionState.CurrentQuestion
            ?? throw new InvalidOperationException(
                "Exam session does not contain an active question.");
        var answerId = _sessionState.AttemptAnswerIdsByQuestionId[question.Id];
        var paperItemId = _sessionState.PaperItemIdsByQuestionId[question.Id];
        var maxAssessmentTurns = Math.Max(1, _settings.MaxTurnsPerQuestion);
        var resumeTurnOrder = _sessionState.ResumeTurnOrder;
        var resumePrompt = _sessionState.ResumeActivePromptText;
        var resumeSpokenSeconds = Math.Max(
            0,
            _sessionState.ResumeSpokenSeconds);
        var isResumingFollowUp = resumeTurnOrder is > 1
            && !string.IsNullOrWhiteSpace(resumePrompt);

        if (resumeTurnOrder is not null)
        {
            _sessionState.ResumeTurnOrder = null;
            _sessionState.ResumeActivePromptText = null;
        }
        _sessionState.ResumeSpokenSeconds = 0;

        using var budget = new QuestionSpeechBudget(
            question.MaxResponseSeconds,
            resumeSpokenSeconds);
        var lastCheckpointedSecond = (int)Math.Floor(resumeSpokenSeconds);
        void HandleBudgetProgress(TimeSpan elapsed, TimeSpan limit)
        {
            HandleSpeakingTimeChanged(elapsed, limit);
            var elapsedWholeSeconds = (int)Math.Floor(elapsed.TotalSeconds);
            if (elapsedWholeSeconds <= lastCheckpointedSecond)
            {
                return;
            }

            lastCheckpointedSecond = elapsedWholeSeconds;
            _ = _sessionClient.SendSpeechBudgetProgressAsync(
                answerId,
                elapsed.TotalSeconds);
        }
        budget.ProgressChanged += HandleBudgetProgress;
        _speechTurns.CloseSpeechWindow();
        _presentation.Clear();
        var questionContext = BuildQuestionContext(question);

        try
        {
            int turnOrder;
            int assessmentTurnCount;
            string currentPrompt;
            bool avatarSpoke;

            if (isResumingFollowUp)
            {
                turnOrder = resumeTurnOrder!.Value;
                assessmentTurnCount = Math.Max(0, turnOrder - 1);
                currentPrompt = resumePrompt!.Trim();
                _sessionClient.SetResumeCheckpoint(answerId, turnOrder - 1);
                avatarSpoke = await _presentation.PresentResumeAsync(
                    answerId,
                    paperItemId,
                    questionContext,
                    currentPrompt,
                    cancellationToken);
                if (avatarSpoke)
                {
                    _speechTurns.OpenSpeechWindow(budget);
                }
            }
            else
            {
                turnOrder = 1;
                assessmentTurnCount = 0;
                currentPrompt = prompt.QuestionText;
                var initialPresentation = await _presentation.PresentInitialAsync(
                    question,
                    answerId,
                    paperItemId,
                    questionContext,
                    currentPrompt,
                    () => _speechTurns.OpenSpeechWindow(budget),
                    () => _speechTurns.SpeechStartedTask,
                    cancellationToken);
                avatarSpoke = initialPresentation.AvatarSpoke;
                if (avatarSpoke && !initialPresentation.Interrupted)
                {
                    await _presentation.RunPreparationAsync(
                        question,
                        _speechTurns.SpeechStartedTask,
                        cancellationToken);
                }
            }

            if (!avatarSpoke)
            {
                _speechTurns.CloseSpeechWindow();
                StatusChanged?.Invoke(
                    "AI chưa xác nhận đọc xong câu hỏi. Tạm thời chưa mở lượt trả lời.");
            }

            var questionCompleted = assessmentTurnCount >= maxAssessmentTurns;
            var lastTurnOrder = Math.Max(0, turnOrder - 1);
            while (!questionCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_speechTurns.IsSpeechWindowOpen)
                {
                    break;
                }
                if (budget.IsExceeded)
                {
                    StatusChanged?.Invoke(
                        $"Đã hết thời gian nói của câu {prompt.QuestionNumber}. Chuyển sang câu tiếp theo.");
                    break;
                }

                StatusChanged?.Invoke(
                    $"Đang chờ học sinh trả lời câu {prompt.QuestionNumber} (turn {turnOrder})...");
                var initialTimeout = TimeSpan.FromSeconds(Math.Max(
                    3,
                    assessmentTurnCount == 0
                        ? _settings.InitialSilenceTimeoutSeconds
                        : _settings.SilenceTimeoutAfterRepeatSeconds));
                var overallTimeout = TimeSpan.FromSeconds(
                    Math.Max(15, _settings.QuestionTurnTimeoutSeconds));
                var gracePeriod = TimeSpan.FromSeconds(
                    Math.Max(1, _settings.PostSpeechSilenceGracePeriodSeconds));

                CapturedTurn captured;
                var salvaged = false;
                try
                {
                    captured = await _speechTurns.CaptureAsync(
                        turnOrder,
                        initialTimeout,
                        overallTimeout,
                        gracePeriod,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // The exam clock hit zero, the student pressed "Nop bai", or proctoring
                    // force-ended the attempt -- all while the student was mid-answer. CaptureAsync
                    // unwinds before CompleteCapture, so the recorder still holds the audio and
                    // nobody has drained it; ExamAttemptRunner's finally is about to call
                    // recorder.StopAsync(), which clears the buffer. Rescue it here or the answer
                    // is gone from the results entirely -- not scored zero, simply absent.
                    var rescued = _speechTurns.TrySalvageInFlightCapture(turnOrder);
                    if (rescued is null || rescued.Pcm.Length < MinimumSalvageBytes())
                    {
                        if (rescued is not null)
                        {
                            LocalFileLogger.Info("exam_flow", "turn_salvage_skipped", new
                            {
                                turnOrder,
                                reason = "below_minimum_audio",
                                pcmBytes = rescued.Pcm.Length
                            });
                        }
                        throw;
                    }

                    captured = rescued;
                    salvaged = true;
                    // Raised here rather than from ExamAttemptRunner's catch so the "dang luu"
                    // overlay appears within milliseconds instead of after this method unwinds.
                    FinalAnswerSaveStarted?.Invoke();
                    LocalFileLogger.Info("exam_flow", "turn_salvage_begin", new
                    {
                        answerId,
                        paperItemId,
                        turnOrder,
                        pcmBytes = captured.Pcm.Length,
                        captured.DurationSeconds
                    });
                }
                lastTurnOrder = turnOrder;

                StatusChanged?.Invoke(captured.EndReason switch
                {
                    SpeechCaptureEndReason.SpeechBudgetExceeded =>
                        "Đã hết thời gian nói, đang lưu câu trả lời...",
                    SpeechCaptureEndReason.InitialSilenceTimeout =>
                        "Không phát hiện câu trả lời, đang xử lý lượt hiện tại...",
                    _ => "Học sinh đã dừng nói, đang xử lý..."
                });

                // KHÔNG upload audio và KHÔNG gọi POST /turns/archive từ đây nữa -- Python tự lo
                // (AttemptConnection._archive_turn), từ chính luồng PCM đã đẩy sang nó qua
                // WebSocket của bài thi.
                //
                // Vì sao bỏ: đường cũ phụ thuộc mạng của học sinh -- đúng lý do mô hình này đã bị
                // bác khi làm luyện tập (xem docstring đầu agents/src/infra/practice_session_client.py)
                // -- và nó không hề có retry. Đo 2026-08-13: một lượt chạy quá 100 giây rồi bị cắt
                // giữa chừng (SocketException 995), lượt khác bị huỷ đúng lúc nộp bài; cả hai chỉ
                // thoát nạn nhờ may. Upload từ pod lên S3 nằm gọn trong AWS, không đi qua wifi
                // phòng thi.
                //
                // Đã bỏ enqueue thì hàng đợi luôn rỗng, nên DrainAsync ở cuối bài thành no-op --
                // giữ nguyên phần plumbing đó để lần chạy đầu còn revert nhanh được nếu cần.

                // A salvaged turn is the student's last answer of the exam by definition, so tell
                // Python not to hand back a follow-up: TurnProcessor clamps should_continue=false
                // and speaks the closing line instead. Java never reads the decision reason, so
                // this costs nothing at grading time.
                var speechBudgetExceeded =
                    salvaged || captured.SpeechBudgetExceeded || budget.IsExceeded;
                RealtimeDecision decision;
                bool avatarSpokeAfterDecision;
                try
                {
                    if (salvaged)
                    {
                        // No WaitForAvatarAfterAsync: the student is watching the "dang luu"
                        // overlay and the farewell is about to speak anyway, so waiting out the
                        // closing line only adds seconds. What matters is that the turn_end frame
                        // lands -- that is the only thing that makes Python publish the turn.
                        decision = await SendTurnEndBoundedAsync(
                            turnOrder,
                            speechBudgetExceeded,
                            captured.DurationSeconds,
                            assessmentTurnCount,
                            maxAssessmentTurns,
                            cancellationToken);
                        avatarSpokeAfterDecision = false;
                    }
                    else
                    {
                        (decision, avatarSpokeAfterDecision) =
                            await _presentation.WaitForAvatarAfterAsync(
                                _ => SendTurnEndBoundedAsync(
                                    turnOrder,
                                    speechBudgetExceeded,
                                    captured.DurationSeconds,
                                    assessmentTurnCount,
                                    maxAssessmentTurns,
                                    cancellationToken),
                                cancellationToken);
                    }
                }
                // Wider than the TimeoutException this used to catch: after a proctoring force_end
                // Python closes the socket, so the send throws InvalidOperationException /
                // WebSocketException instead. Letting those escape would turn a clean cancellation
                // into ExamAttemptRunner's run_failed path and mark the attempt INTERRUPTED.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LocalFileLogger.Error(
                        "exam_flow",
                        "turn_end_decision_timeout",
                        ex,
                        new { turnOrder, salvaged });
                    decision = new RealtimeDecision
                    {
                        ShouldContinue = false,
                        NextPromptText = null,
                        Reason = "connection_lost_timeout"
                    };
                    avatarSpokeAfterDecision = false;
                }

                _sessionClient.SetResumeCheckpoint(answerId, turnOrder);

                if (salvaged)
                {
                    LocalFileLogger.Info("exam_flow", "turn_salvage_complete", new
                    {
                        answerId,
                        turnOrder,
                        decisionReason = decision.Reason
                    });
                    // Hand control back to ExamAttemptRunner's OperationCanceledException handler
                    // so its _submitRequested branch (farewell -> drain -> SUBMITTED) runs.
                    // Falling through would only present the next question before the loop's own
                    // ThrowIfCancellationRequested threw anyway.
                    throw new OperationCanceledException(cancellationToken);
                }
                if (!string.IsNullOrWhiteSpace(decision.NextPromptText))
                {
                    TranscriptAppended?.Invoke($"AI: {decision.NextPromptText}");
                    currentPrompt = decision.NextPromptText;
                }

                if (!IsClarificationReason(decision.Reason))
                {
                    assessmentTurnCount++;
                }

                questionCompleted = speechBudgetExceeded
                    || !decision.ShouldContinue
                    || assessmentTurnCount >= maxAssessmentTurns;
                if (speechBudgetExceeded)
                {
                    StatusChanged?.Invoke(
                        $"Câu {prompt.QuestionNumber} đã đạt giới hạn {question.MaxResponseSeconds} giây nói. Tự động chuyển câu tiếp theo.");
                }
                else if (!questionCompleted && avatarSpokeAfterDecision)
                {
                    _speechTurns.OpenSpeechWindow(budget);
                }
                else if (!questionCompleted)
                {
                    StatusChanged?.Invoke(
                        "AI chưa xác nhận đọc xong follow-up. Tạm thời chưa mở lượt trả lời.");
                }

                turnOrder++;
            }

            return new QuestionFlowResult(
                questionCompleted,
                assessmentTurnCount,
                lastTurnOrder);
        }
        finally
        {
            _speechTurns.CloseSpeechWindow();
            await _sessionClient.SendSpeechBudgetProgressAsync(
                answerId,
                budget.ElapsedSeconds);
            _presentation.Clear();
            budget.ProgressChanged -= HandleBudgetProgress;
        }
    }

    /// <summary>
    /// Sends turn_end for a turn whose audio is already in the archive queue, on a deadline the
    /// run token can only SHORTEN -- never cancel.
    ///
    /// The queue uploads and archives that audio regardless (it runs on its own lifetime token),
    /// but Python publishes AnswerTurnsRecorded -- the only thing that makes Java create the
    /// answer row -- exclusively from its turn_end handler. So letting the run token abort this
    /// handshake produces archived audio that Java never sees: the same lost answer this whole
    /// change exists to prevent, one step further along the pipeline.
    ///
    /// Normal operation keeps the existing generous ceiling; a shutdown grants a few more seconds
    /// of grace and then gives up, because the student is watching a "saving" overlay and cannot
    /// be made to wait out the 180s reconnect-recovery ceiling.
    ///
    /// Deliberately never retried: SendTurnEndAndWaitAsync awaits the send before registering its
    /// cancellation callback, so a caller catching a cancellation genuinely cannot tell whether
    /// the frame reached the wire. Re-sending blind would complete the turn twice server-side and
    /// spawn a second publish for a turn that will never be archived.
    /// </summary>
    private async Task<RealtimeDecision> SendTurnEndBoundedAsync(
        int turnOrder,
        bool speechBudgetExceeded,
        double durationSeconds,
        int assessmentTurnCount,
        int maxAssessmentTurns,
        CancellationToken runToken)
    {
        using var handshake = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(15, _settings.QuestionTurnTimeoutSeconds)));
        // Fires synchronously when runToken is already cancelled (the salvage case), so the grace
        // window starts immediately there. CancelAfter reschedules the existing timer.
        // Registered after the CTS and disposed before it, so the callback can never touch a
        // disposed source.
        using var shorten = runToken.Register(
            () => handshake.CancelAfter(_shutdownGrace()));

        try
        {
            return await _sessionClient.SendTurnEndAndWaitAsync(
                turnOrder,
                speechBudgetExceeded,
                durationSeconds,
                assessmentTurnCount,
                maxAssessmentTurns,
                handshake.Token);
        }
        catch (OperationCanceledException) when (handshake.IsCancellationRequested)
        {
            // Normalise to the exception the caller already handles. This must never look like a
            // run-token cancellation, or the caller would mistake it for "the exam was cancelled".
            throw new TimeoutException(
                $"turn_end handshake for turn {turnOrder} gave up after its shutdown grace.");
        }
    }

    // 16 kHz, 16-bit, mono.
    private int MinimumSalvageBytes() =>
        16_000 * 2 * Math.Max(0, _settings.MinimumSalvageAudioMilliseconds) / 1000;

    private void HandleSpeakingTimeChanged(TimeSpan elapsed, TimeSpan limit) =>
        SpeakingTimeChanged?.Invoke(elapsed, limit);

    private QuestionContextDto BuildQuestionContext(ExamQuestion question)
    {
        _sessionState.EvaluationGuidesByQuestionId.TryGetValue(
            question.Id,
            out var evaluationGuide);
        return new QuestionContextDto
        {
            InstructionText = question.InstructionText,
            QuestionText = question.QuestionText,
            QuestionType = ToPythonQuestionType(question.Type),
            DifficultyLevel = NormalizeDifficultyLevel(question.DifficultyLevel),
            DurationSeconds = question.MaxResponseSeconds,
            MinResponseSeconds = question.MinResponseSeconds,
            MaxResponseSeconds = question.MaxResponseSeconds,
            EvaluationGuide = BuildEvaluationGuide(evaluationGuide),
            Asset = BuildAsset(question.Asset)
        };
    }

    private static EvaluationGuideDto? BuildEvaluationGuide(
        QuestionEvaluationGuide? guide) =>
        guide is null
            ? null
            : new EvaluationGuideDto
            {
                ExpectedContent = guide.ExpectedContent,
                KeyPoints = guide.KeyPoints,
                AcceptableResponses = guide.AcceptableResponses,
                OffTopicExamples = guide.OffTopicExamples,
                ScoringHints = guide.ScoringHints,
                CommonMistakes = guide.CommonMistakes
            };

    private static QuestionAssetContextDto? BuildAsset(QuestionAsset? asset) =>
        asset is null
            ? null
            : new QuestionAssetContextDto
            {
                Type = asset.Type switch
                {
                    QuestionAssetType.Audio => "audio",
                    QuestionAssetType.Image => "image",
                    QuestionAssetType.Video => "video",
                    QuestionAssetType.TextPassage => "text_passage",
                    _ => throw new ArgumentOutOfRangeException(nameof(asset))
                },
                Transcript = asset.Transcript,
                Description = asset.Description,
                AltText = asset.AltText
            };

    private static string ToPythonQuestionType(QuestionType type) => type switch
    {
        QuestionType.ReadAloud => "read_aloud",
        QuestionType.ShortAnswer => "short_answer",
        QuestionType.LongAnswer => "long_answer",
        QuestionType.Opinion => "opinion",
        QuestionType.Description => "description",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private static string NormalizeDifficultyLevel(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "medium"
            : value.Trim().ToLowerInvariant();

    private static bool IsClarificationReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason)
        && (
            reason.StartsWith(
                "clarification_",
                StringComparison.OrdinalIgnoreCase)
            || ClarificationReasons.Contains(reason)
        );
}
