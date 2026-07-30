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

    public QuestionFlowRunner(
        ExamSessionState sessionState,
        AppSettings settings,
        RealtimeSessionClient sessionClient,
        QuestionPresentationService presentation,
        SpeechTurnCoordinator speechTurns,
        TurnArchiveQueue archiveQueue)
    {
        _sessionState = sessionState;
        _settings = settings;
        _sessionClient = sessionClient;
        _presentation = presentation;
        _speechTurns = speechTurns;
        _archiveQueue = archiveQueue;
    }

    public event Action<string>? StatusChanged;
    public event Action<string>? TranscriptAppended;
    public event Action<TimeSpan, TimeSpan>? SpeakingTimeChanged;

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

                var captured = await _speechTurns.CaptureAsync(
                    turnOrder,
                    initialTimeout,
                    overallTimeout,
                    gracePeriod,
                    cancellationToken);
                lastTurnOrder = turnOrder;

                StatusChanged?.Invoke(captured.EndReason switch
                {
                    SpeechCaptureEndReason.SpeechBudgetExceeded =>
                        "Đã hết thời gian nói, đang lưu câu trả lời...",
                    SpeechCaptureEndReason.InitialSilenceTimeout =>
                        "Không phát hiện câu trả lời, đang xử lý lượt hiện tại...",
                    _ => "Học sinh đã dừng nói, đang xử lý..."
                });

                if (captured.Pcm.Length > 0)
                {
                    _archiveQueue.Enqueue(
                        new TurnArchiveWorkItem(
                            answerId,
                            paperItemId,
                            turnOrder,
                            currentPrompt,
                            captured.DurationSeconds,
                            captured.Pcm,
                            questionContext));
                }

                var speechBudgetExceeded =
                    captured.SpeechBudgetExceeded || budget.IsExceeded;
                RealtimeDecision decision;
                bool avatarSpokeAfterDecision;
                try
                {
                    (decision, avatarSpokeAfterDecision) =
                        await _presentation.WaitForAvatarAfterAsync(
                            token => _sessionClient.SendTurnEndAndWaitAsync(
                                turnOrder,
                                speechBudgetExceeded,
                                captured.DurationSeconds,
                                assessmentTurnCount,
                                maxAssessmentTurns,
                                token),
                            cancellationToken);
                }
                catch (TimeoutException ex)
                {
                    LocalFileLogger.Error(
                        "exam_flow",
                        "turn_end_decision_timeout",
                        ex,
                        new { turnOrder });
                    decision = new RealtimeDecision
                    {
                        ShouldContinue = false,
                        NextPromptText = null,
                        Reason = "connection_lost_timeout"
                    };
                    avatarSpokeAfterDecision = false;
                }

                _sessionClient.SetResumeCheckpoint(answerId, turnOrder);
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
