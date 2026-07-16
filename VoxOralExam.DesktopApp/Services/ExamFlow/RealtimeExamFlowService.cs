using VoxOralExam.Core.Models;
using VoxOralExam.Core.Models.Dtos;
using VoxOralExam.Core.Interfaces;
using VoxOralExam.DesktopApp.Dtos;
using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.Services.DomainService;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services.ExamFlow;

/// <summary>
/// Sole IExamFlowService implementation (Phase 5 of docs/realtime-self-hosted-avatar-plan.md),
/// replacing the Phase 1 stub. Opens RealtimeSessionClient (WebSocket) and AvatarWebRtcClient
/// (recvonly avatar video+audio) exactly once at exam start and holds both open for every
/// question in the attempt -- switching questions is an in-band question_start message, never a
/// reconnect, the direct fix for Tavus's old per-question reconnect gap.
///
/// Turn-end detection is VAD-driven from the server (Voice Live's vad_speech_start/vad_speech_end,
/// forwarded over the WebSocket), not a client-side silence timer racing against a conversational
/// AI's own turn-taking the way the old Tavus flow worked. A short grace period after
/// vad_speech_end (PostSpeechSilenceGracePeriodSeconds) tolerates the student pausing
/// mid-answer rather than treating every brief silence as the end of the turn -- this is the WPF
/// side of the same multi-utterance-per-turn support realtime/session.py has on the Python side.
/// </summary>
public partial class RealtimeExamFlowService : IExamFlowService
{
    private readonly TurnAudioUploader _turnAudioUploader;
    private readonly TurnArchiveClient _turnArchiveClient;
    private readonly ExamSessionState _sessionState;
    private readonly AppSettings _settings;
    private readonly RealtimeSessionClient _sessionClient;
    private readonly AvatarWebRtcClient _avatarClient;
    private readonly LocalAvatarSpeaker _avatarSpeaker;
    private readonly MicAudioStreamer _micStreamer;
    private readonly IExamApiService _examApi;
    private readonly QuestionAssetPresentationCoordinator _assetPresentationCoordinator;

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private TurnAudioRecorder? _recorder;
    private TaskCompletionSource<bool>? _vadSpeechStartTcs;
    private TaskCompletionSource<bool>? _vadSpeechEndTcs;
    private TaskCompletionSource<bool>? _avatarUtteranceCompleteTcs;
    private bool _studentSpeechWindowOpen;
    private readonly List<Task> _pendingArchiveTasks = [];
    private Guid? _lastAnnouncedSectionId;

    public RealtimeExamFlowService(
        TurnAudioUploader turnAudioUploader,
        TurnArchiveClient turnArchiveClient,
        ExamSessionState sessionState,
        AppSettings settings,
        RealtimeSessionClient sessionClient,
        AvatarWebRtcClient avatarClient,
        LocalAvatarSpeaker avatarSpeaker,
        MicAudioStreamer micStreamer,
        IExamApiService examApi,
        QuestionAssetPresentationCoordinator assetPresentationCoordinator)
    {
        _turnAudioUploader = turnAudioUploader;
        _turnArchiveClient = turnArchiveClient;
        _sessionState = sessionState;
        _settings = settings;
        _sessionClient = sessionClient;
        _avatarClient = avatarClient;
        _avatarSpeaker = avatarSpeaker;
        _micStreamer = micStreamer;
        _examApi = examApi;
        _assetPresentationCoordinator = assetPresentationCoordinator;
    }

    public event Action<ExamQuestionPrompt>? OnQuestionPresented;
    public event Action<string>? OnTranscriptAppended;
    public event Action<string>? OnStatusChanged;
    public event Action? OnExamCompleted;
    public event Action<bool>? OnStudentSpeakingChanged;
    public event Action<bool>? OnAvatarSpeakingChanged;

    public Task StartAsync(CancellationToken ct)
    {
        if (_runTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        LocalFileLogger.Info("exam_flow", "start_requested");
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runTask = RunAsync(_runCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        LocalFileLogger.Info("exam_flow", "stop_requested");
        _runCts?.Cancel();

        if (_runTask is not null)
        {
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
                LocalFileLogger.Info("exam_flow", "stop_cancelled");
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        LocalFileLogger.Info("exam_flow", "run_begin");
        EnsureSessionInitialized();

        using var recorder = new TurnAudioRecorder(
            _settings.TurnAudioPreRollMilliseconds,
            _sessionState.SelectedAudioInputDeviceIndex);
        _recorder = recorder;

        _sessionClient.OnVadSpeechStart += HandleVadSpeechStart;
        _sessionClient.OnVadSpeechEnd += HandleVadSpeechEnd;
        _sessionClient.OnAvatarUtteranceComplete += HandleAvatarUtteranceComplete;
        _sessionClient.OnSpeakRequested += HandleSpeakRequested;
        _sessionClient.OnError += HandleSessionError;
        _sessionClient.OnReconnecting += HandleSessionReconnecting;
        _sessionClient.OnReconnected += HandleSessionReconnected;
        _avatarClient.OnReconnecting += HandleAvatarReconnecting;
        _avatarClient.OnReconnected += HandleAvatarReconnected;

        try
        {
            await recorder.StartAsync(ct);
            OnStatusChanged?.Invoke(TurnAudioRecorder.DescribeInputDevice(_sessionState.SelectedAudioInputDeviceIndex));

            OnStatusChanged?.Invoke("Dang ket noi realtime session...");
            await _sessionClient.ConnectAsync(_sessionState.ExamAttemptId, ct);

            if (_settings.EnableAvatarWebRtc)
            {
                OnStatusChanged?.Invoke("Dang ket noi avatar...");
                await _avatarClient.ConnectAsync(_sessionState.ExamAttemptId, ct);
            }

            _micStreamer.Start(recorder, _sessionClient);
            _lastAnnouncedSectionId = null;

            for (_sessionState.QuestionIndex = 0; _sessionState.QuestionIndex < _sessionState.Questions.Count; _sessionState.QuestionIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var prompt = PresentCurrentQuestion();
                await RunQuestionAsync(prompt, ct);
            }

            OnStatusChanged?.Invoke("Da hoan thanh bai van dap.");
            await WaitForAvatarUtteranceCompletionAfterAsync(
                triggerAsync: token => _sessionClient.SendExamEndAndWaitForAckAsync(token),
                ct);
            OnExamCompleted?.Invoke();
            LocalFileLogger.Info("exam_flow", "run_completed");

            await WaitForPendingArchivesAsync();
            await SubmitSessionStatusAsync("SUBMITTED");
        }
        catch (OperationCanceledException)
        {
            LocalFileLogger.Info("exam_flow", "run_cancelled");
            // Student exited / app was closed before finishing every question -- the exam is being
            // force-cut-off, not naturally submitted. Still send whatever was answered to grading
            // rather than leaving the session stuck at IN_PROGRESS forever (matches vox's
            // IN_PROGRESS -> EXPIRED -> GRADING transition, which grades partial answers too).
            await WaitForPendingArchivesAsync();
            await SubmitSessionStatusAsync("EXPIRED");
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "run_failed", ex);
            await WaitForPendingArchivesAsync();
            await SubmitSessionStatusAsync("EXPIRED");
            throw;
        }
        finally
        {
            _sessionClient.OnVadSpeechStart -= HandleVadSpeechStart;
            _sessionClient.OnVadSpeechEnd -= HandleVadSpeechEnd;
            _sessionClient.OnAvatarUtteranceComplete -= HandleAvatarUtteranceComplete;
            _sessionClient.OnSpeakRequested -= HandleSpeakRequested;
            _sessionClient.OnError -= HandleSessionError;
            _sessionClient.OnReconnecting -= HandleSessionReconnecting;
            _sessionClient.OnReconnected -= HandleSessionReconnected;
            _avatarClient.OnReconnecting -= HandleAvatarReconnecting;
            _avatarClient.OnReconnected -= HandleAvatarReconnected;
            OnStudentSpeakingChanged?.Invoke(false);
            _micStreamer.Stop();
            await recorder.StopAsync();
            _recorder = null;
            if (_settings.EnableAvatarWebRtc)
            {
                await _avatarClient.DisconnectAsync();
            }
            await _sessionClient.CloseAsync();
            LocalFileLogger.Info("exam_flow", "run_finally_complete");
        }
    }

    private async Task RunQuestionAsync(ExamQuestionPrompt prompt, CancellationToken ct)
    {
        var question = _sessionState.CurrentQuestion ?? throw new InvalidOperationException("Exam session does not contain an active question.");
        var attemptAnswerId = _sessionState.AttemptAnswerIdsByQuestionId[question.Id];
        var paperItemId = _sessionState.PaperItemIdsByQuestionId[question.Id];
        var maxTurnsPerQuestion = Math.Max(1, _settings.MaxTurnsPerQuestion);
        var sectionInstruction = GetSectionInstructionToAnnounce(question);

        CloseStudentSpeechWindow();
        _assetPresentationCoordinator.Clear();
        var questionContext = BuildQuestionContext(question);

        // 1) Section lead-in alone (only speaks when this question starts a new section -- see
        // GetSectionInstructionToAnnounce), then a deliberate pause before moving on, so it reads
        // as its own beat rather than running straight into the question's own instruction.
        await WaitForAvatarUtteranceCompletionAfterAsync(
            triggerAsync: token => _sessionClient.SendQuestionStartAsync(
                attemptAnswerId,
                paperItemId,
                questionContext,
                language: "en-US",
                promptText: null,
                sectionInstruction: sectionInstruction,
                ct: token),
            ct);
        if (!string.IsNullOrWhiteSpace(sectionInstruction))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        // 2) Read the question's own instruction (if any) while showing its asset at the same
        // time (if any) -- concurrent, not sequential, so the student sees the passage/image while
        // being told what to do with it. The asset stays up for at least PreparationTimeSeconds
        // regardless of how long the instruction takes to read.
        bool avatarSpokeForQuestion;
        var hasInstruction = !string.IsNullOrWhiteSpace(question.InstructionText);
        if (hasInstruction || question.Asset is not null)
        {
            var instructionSpokenTask = hasInstruction
                ? WaitForAvatarUtteranceCompletionAfterAsync(
                    triggerAsync: token => _sessionClient.SendPresentQuestionAsync(question.InstructionText, token),
                    ct)
                : Task.FromResult(true);
            if (question.Asset is not null)
            {
                OnStatusChanged?.Invoke("Dang hien tai nguyen cau hoi...");
                var presentAssetTask = _assetPresentationCoordinator.PresentAsync(question.Asset, question.PreparationTimeSeconds, ct);
                await Task.WhenAll(instructionSpokenTask, presentAssetTask);
            }
            avatarSpokeForQuestion = await instructionSpokenTask;
        }

        // 3) Ask the actual question -- the student is expected to answer right away afterwards.
        avatarSpokeForQuestion = await WaitForAvatarUtteranceCompletionAfterAsync(
            triggerAsync: token => _sessionClient.SendPresentQuestionAsync(prompt.QuestionText, token),
            ct);

        if (avatarSpokeForQuestion)
        {
            // Arm the mic/recorder BEFORE the preparation announcement below, not after -- if the
            // student starts answering early (while that announcement is still playing),
            // HandleVadSpeechStart gates only on _studentSpeechWindowOpen and will call
            // _recorder.BeginTurnCapture() regardless of whether WaitForSpeechStartAsync has been
            // called yet, so their audio is captured instead of lost. See the turn-1 check right
            // before WaitForSpeechStartAsync below, and AnnouncePreparationAndRecordingAsync's own
            // doc comment, for the other half of this.
            OpenStudentSpeechWindow();
            await AnnouncePreparationAndRecordingAsync(question, ct);
        }
        else
        {
            OnStatusChanged?.Invoke("AI chua xac nhan doc xong cau hoi. Tam thoi chua mo luot tra loi cua hoc sinh.");
        }

        var turnOrder = 1;
        var assessmentTurnCount = 0;
        var questionDone = false;
        var currentPromptText = prompt.QuestionText;

        while (!questionDone)
        {
            ct.ThrowIfCancellationRequested();
            if (_studentSpeechWindowOpen)
            {
                OnStatusChanged?.Invoke($"Dang cho hoc sinh tra loi cau {prompt.QuestionNumber} (turn {turnOrder})...");
            }

            var initialTimeout = TimeSpan.FromSeconds(Math.Max(3,
                assessmentTurnCount == 0 ? _settings.InitialSilenceTimeoutSeconds : _settings.SilenceTimeoutAfterRepeatSeconds));
            var overallTimeout = TimeSpan.FromSeconds(Math.Max(15, _settings.QuestionTurnTimeoutSeconds));
            var gracePeriod = TimeSpan.FromSeconds(Math.Max(1, _settings.PostSpeechSilenceGracePeriodSeconds));

            if (!_studentSpeechWindowOpen)
            {
                LocalFileLogger.Info("exam_flow", "student_speech_window_closed_before_wait", new
                {
                    prompt.QuestionNumber,
                    turnOrder
                });
                break;
            }

            // If the student already started answering while the preparation/recording
            // announcement was still playing (see AnnouncePreparationAndRecordingAsync),
            // HandleVadSpeechStart already called _recorder.BeginTurnCapture() for them -- VAD's
            // speech_start already fired once and won't fire again, so WaitForSpeechStartAsync
            // would otherwise just wait out the full initialTimeout for a signal that will never
            // come, producing a false "no response" timeout despite the student having answered.
            // Only relevant for turn 1 -- later follow-up turns never have that announcement.
            var spoke = turnOrder == 1 && _recorder is not null && _recorder.IsTurnActive
                ? true
                : await WaitForSpeechStartAsync(initialTimeout, ct);
            if (spoke)
            {
                OnStatusChanged?.Invoke("Hoc sinh dang noi...");
                await WaitForSpeechEndWithGraceAsync(overallTimeout, gracePeriod, ct);
                OnStatusChanged?.Invoke("Hoc sinh da dung noi, dang xu ly...");
            }

            CloseStudentSpeechWindow();
            var pcmBytes = _recorder!.GetTurnBufferAndReset();
            if (pcmBytes.Length > 0)
            {
                DispatchArchiveTurn(question, attemptAnswerId, paperItemId, turnOrder, currentPromptText, pcmBytes, ct);
            }

            // Assumes worst case (this turn counts toward the budget, i.e. isn't a
            // clarification) since we don't know Python's decision.Reason yet -- conservative,
            // but the alternative (finding out too late) is exactly the bug this prevents.
            var isLastAllowedTurn = assessmentTurnCount + 1 >= maxTurnsPerQuestion;
            RealtimeDecision decision;
            bool avatarSpokeAfterDecision;
            try
            {
                (decision, avatarSpokeAfterDecision) = await WaitForAvatarUtteranceCompletionAfterAsync(
                    triggerAsync: token => _sessionClient.SendTurnEndAndWaitAsync(turnOrder, isLastAllowedTurn, token),
                    ct);
            }
            catch (TimeoutException ex)
            {
                // RealtimeSessionClient never heard back (direct or resume-recovered) within its
                // timeout -- that's just a network fact; deciding what it means for the exam (stop
                // this question rather than block forever) is this class's call, not the network
                // client's.
                LocalFileLogger.Error("exam_flow", "turn_end_decision_timeout", ex, new { turnOrder });
                decision = new RealtimeDecision
                {
                    ShouldContinue = false,
                    NextPromptText = null,
                    Reason = "connection_lost_timeout",
                };
                avatarSpokeAfterDecision = false;
            }
            _sessionClient.SetResumeCheckpoint(attemptAnswerId, turnOrder);
            LocalFileLogger.Info("exam_flow", "decision_received", new
            {
                prompt.QuestionNumber,
                turnOrder,
                assessmentTurnCount,
                decision.ShouldContinue,
                decision.Reason
            });

            if (!string.IsNullOrWhiteSpace(decision.NextPromptText))
            {
                OnTranscriptAppended?.Invoke($"AI: {decision.NextPromptText}");
                currentPromptText = decision.NextPromptText;
            }

            var isClarificationTurn = IsClarificationReason(decision.Reason);
            if (!isClarificationTurn)
            {
                assessmentTurnCount++;
            }

            questionDone = !decision.ShouldContinue || assessmentTurnCount >= maxTurnsPerQuestion;
            if (!questionDone && avatarSpokeAfterDecision)
            {
                OpenStudentSpeechWindow();
            }
            else if (!questionDone)
            {
                OnStatusChanged?.Invoke("AI chua xac nhan doc xong follow-up. Tam thoi chua mo luot tra loi cua hoc sinh.");
            }
            turnOrder++;
        }

        if (!questionDone)
        {
            OnStatusChanged?.Invoke($"Da dat gioi han {maxTurnsPerQuestion} luot danh gia cho cau {prompt.QuestionNumber}. Chuyen sang cau tiep theo.");
        }

        CloseStudentSpeechWindow();
        _assetPresentationCoordinator.Clear();
    }

    private string? GetSectionInstructionToAnnounce(Question question)
    {
        if (question.SectionId is null || question.SectionId == Guid.Empty)
        {
            return null;
        }

        if (_lastAnnouncedSectionId == question.SectionId)
        {
            return null;
        }

        _lastAnnouncedSectionId = question.SectionId;
        if (string.IsNullOrWhiteSpace(question.SectionInstruction) && string.IsNullOrWhiteSpace(question.SectionTitle))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(question.SectionTitle)
            ? question.SectionInstruction
            : $"{question.SectionTitle}. {question.SectionInstruction}".Trim().Trim('.');
    }

    private void DispatchArchiveTurn(
        Question question,
        Guid attemptAnswerId,
        Guid paperItemId,
        int turnOrder,
        string promptText,
        byte[] pcmBytes,
        CancellationToken ct)
    {
        var task = ArchiveTurnAsync(question, attemptAnswerId, paperItemId, turnOrder, promptText, pcmBytes, ct);
        _pendingArchiveTasks.Add(task);
        // Path A (the live decision via SendTurnEndAndWaitAsync) never awaits this -- mirrors
        // the Python session's own decoupled Path A/Path B design. Failures are logged inside
        // ArchiveTurnAsync itself; WaitForPendingArchivesAsync only makes sure they get a chance
        // to finish before the exam flow fully tears down.
        _pendingArchiveTasks.RemoveAll(t => t.IsCompleted);
    }

    private async Task ArchiveTurnAsync(
        Question question,
        Guid attemptAnswerId,
        Guid paperItemId,
        int turnOrder,
        string promptText,
        byte[] pcmBytes,
        CancellationToken ct)
    {
        try
        {
            var wavBytes = _turnAudioUploader.EncodeWav(pcmBytes);
            var audioUrl = await _turnAudioUploader.UploadTurnAudioAsync(wavBytes, attemptAnswerId, turnOrder, ct);
            var request = BuildEvaluateTurnRequest(question, attemptAnswerId, paperItemId, turnOrder, promptText, audioUrl);
            await _turnArchiveClient.ArchiveTurnAsync(request, ct);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "archive_turn_failed", ex, new { attemptAnswerId, turnOrder });
        }
    }

    private async Task SubmitSessionStatusAsync(string status)
    {
        if (_sessionState.ExamAttemptId == Guid.Empty)
        {
            return;
        }

        try
        {
            // Use CancellationToken.None: the exam's own ct is already cancelled/faulted by the time
            // this runs (cancellation or error exit path), and this call must still go through.
            await _examApi.UpdateSessionStatusAsync(_sessionState.ExamAttemptId, status, CancellationToken.None);
            LocalFileLogger.Info("exam_flow", "session_status_submitted", new { sessionId = _sessionState.ExamAttemptId, status });
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "session_status_submit_failed", ex, new { sessionId = _sessionState.ExamAttemptId, status });
        }
    }

    private async Task WaitForPendingArchivesAsync()
    {
        var pending = _pendingArchiveTasks.Where(t => !t.IsCompleted).ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "pending_archives_incomplete", ex);
        }
    }

    // Randomized so the avatar doesn't say the literal same sentence every single question --
    // mirrors the pattern agents/src/node/followUpDecisionGraph/FollowUpNode already uses
    // (_NO_SPEECH_PREFIXES etc., random.choice over several template phrasings).
    //
    // Deliberately split into separate template groups (prep-only vs. each duration-clause shape)
    // rather than one template with {0}/{1}/{2} baked into a single sentence -- PreparationTimeSeconds,
    // MinResponseSeconds and MaxResponseSeconds are configured independently per question and any
    // combination can be zero/unset. Baking all three into one sentence would read as nonsense
    // ("between 0 and 0 seconds") whenever min/max aren't both set. BuildDurationClause below picks
    // the right group (or omits the clause) for whichever subset is actually present.
    private static readonly string[] PreparationAnnouncementTemplates =
    [
        "You have {0} seconds to prepare. I will start recording in {0} seconds.",
        "Take {0} seconds to think about your answer. Recording starts in {0} seconds.",
        "You have {0} seconds to get ready. I'll begin recording in {0} seconds.",
    ];

    private static readonly string[] BothDurationTemplates =
    [
        "We expect an answer between {0} and {1} seconds.",
        "Try to answer in about {0} to {1} seconds.",
        "Aim for somewhere between {0} and {1} seconds when you respond.",
    ];

    private static readonly string[] MinOnlyDurationTemplates =
    [
        "We expect an answer of at least {0} seconds.",
        "Please speak for at least {0} seconds.",
    ];

    private static readonly string[] MaxOnlyDurationTemplates =
    [
        "Please keep your answer under {0} seconds.",
        "Try to answer within {0} seconds.",
    ];

    private static readonly string[] RecordingStartedAnnouncementTemplates =
    [
        "I am recording now.",
        "Recording has started -- go ahead.",
        "I'm listening now, please begin.",
    ];

    /// <summary>
    /// Speaks a preparation-time (+ expected-answer-length, if configured) announcement, waits out
    /// the actual preparation window, then announces recording has started. A no-op if the
    /// question doesn't declare a preparation time -- PreparationTimeSeconds is what drives the
    /// whole wait/announce mechanic here, so without it there's nothing to wait for regardless of
    /// whether Min/MaxResponseSeconds are set. Deliberately does NOT gate the mic on any of this --
    /// the caller (RunQuestionAsync) already calls OpenStudentSpeechWindow() before this runs, so a
    /// student who starts answering early (mid-announcement or mid-countdown) is still captured;
    /// see the turn-1 IsTurnActive check right before WaitForSpeechStartAsync in RunQuestionAsync.
    ///
    /// Caveat this doesn't attempt to solve: the mic is now armed while the avatar may still be
    /// speaking (something that never happened before this method existed -- previously
    /// OpenStudentSpeechWindow only ever ran after the avatar had fully finished). On a speaker
    /// setup without echo cancellation/headphones, the avatar's own voice could in principle leak
    /// into the mic and confuse VAD. Not verified against real hardware -- worth testing with an
    /// actual speaker+mic setup before relying on this in a real exam.
    /// </summary>
    private async Task AnnouncePreparationAndRecordingAsync(Question question, CancellationToken ct)
    {
        var prepSeconds = question.PreparationTimeSeconds;
        if (prepSeconds <= 0)
        {
            return;
        }

        var prepText = string.Format(
            PreparationAnnouncementTemplates[Random.Shared.Next(PreparationAnnouncementTemplates.Length)],
            prepSeconds);
        var durationClause = BuildDurationClause(question.MinResponseSeconds, question.MaxResponseSeconds);
        var announcement = string.IsNullOrEmpty(durationClause) ? prepText : $"{prepText} {durationClause}";

        await WaitForAvatarUtteranceCompletionAfterAsync(
            triggerAsync: token => _sessionClient.SendPresentQuestionAsync(announcement, token),
            ct);

        await Task.Delay(TimeSpan.FromSeconds(prepSeconds), ct);

        var recordingNowText = RecordingStartedAnnouncementTemplates[Random.Shared.Next(RecordingStartedAnnouncementTemplates.Length)];
        await WaitForAvatarUtteranceCompletionAfterAsync(
            triggerAsync: token => _sessionClient.SendPresentQuestionAsync(recordingNowText, token),
            ct);
    }

    /// <summary>Covers all four combinations of Min/MaxResponseSeconds being set or not (both,
    /// min-only, max-only, neither) -- returns "" for "neither" so the caller can skip appending
    /// anything rather than speaking an empty/nonsensical clause.</summary>
    private static string BuildDurationClause(int minResponseSeconds, int maxResponseSeconds)
    {
        var hasMin = minResponseSeconds > 0;
        var hasMax = maxResponseSeconds > 0;

        if (hasMin && hasMax)
        {
            return string.Format(
                BothDurationTemplates[Random.Shared.Next(BothDurationTemplates.Length)],
                minResponseSeconds, maxResponseSeconds);
        }
        if (hasMin)
        {
            return string.Format(
                MinOnlyDurationTemplates[Random.Shared.Next(MinOnlyDurationTemplates.Length)],
                minResponseSeconds);
        }
        if (hasMax)
        {
            return string.Format(
                MaxOnlyDurationTemplates[Random.Shared.Next(MaxOnlyDurationTemplates.Length)],
                maxResponseSeconds);
        }
        return "";
    }

    private async Task<bool> WaitForSpeechStartAsync(TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _vadSpeechStartTcs = tcs;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var reg = cts.Token.Register(() => tcs.TrySetResult(false));
            cts.CancelAfter(timeout);
            return await tcs.Task;
        }
        finally
        {
            _vadSpeechStartTcs = null;
        }
    }

    private async Task WaitForSpeechEndWithGraceAsync(TimeSpan overallTimeout, TimeSpan gracePeriod, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + overallTimeout;

        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            var endedNaturally = await WaitForSignalAsync(tcs => _vadSpeechEndTcs = tcs, remaining, ct);
            if (!endedNaturally)
            {
                return;
            }

            remaining = deadline - DateTime.UtcNow;
            var graceWindow = remaining < gracePeriod ? remaining : gracePeriod;
            if (graceWindow <= TimeSpan.Zero)
            {
                return;
            }

            var resumed = await WaitForSignalAsync(tcs => _vadSpeechStartTcs = tcs, graceWindow, ct);
            if (!resumed)
            {
                return;
            }
            // Resumed within the grace period -- loop back and wait for the next speech_end.
        }
    }

    /// <summary>
    /// Waits for Python's explicit avatar_utterance_complete signal instead of inferring end-of-
    /// utterance from decoded audio amplitude. The audio signal still drives UI-only avatar
    /// speaking effects, but turn gating should use the backend's authoritative completion event.
    /// </summary>
    private async Task<bool> WaitForAvatarUtteranceCompletionAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _avatarUtteranceCompleteTcs = tcs;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var reg = cts.Token.Register(() => tcs.TrySetResult(false));
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings.AvatarSpeechMaxWaitSeconds)));
            var completed = await tcs.Task;
            LocalFileLogger.Info("exam_flow", "avatar_utterance_wait_complete", new
            {
                completed
            });
            return completed;
        }
        finally
        {
            if (ReferenceEquals(_avatarUtteranceCompleteTcs, tcs))
            {
                _avatarUtteranceCompleteTcs = null;
            }
        }
    }

    private async Task<bool> WaitForAvatarUtteranceCompletionAfterAsync(
        Func<CancellationToken, Task> triggerAsync,
        CancellationToken ct)
    {
        var avatarSpeechTask = WaitForAvatarUtteranceCompletionAsync(ct);
        await triggerAsync(ct);
        return await avatarSpeechTask;
    }

    private async Task<(T Result, bool AvatarSpoke)> WaitForAvatarUtteranceCompletionAfterAsync<T>(
        Func<CancellationToken, Task<T>> triggerAsync,
        CancellationToken ct)
    {
        var avatarSpeechTask = WaitForAvatarUtteranceCompletionAsync(ct);
        var result = await triggerAsync(ct);
        var avatarSpoke = await avatarSpeechTask;
        return (result, avatarSpoke);
    }

    private void OpenStudentSpeechWindow()
    {
        _studentSpeechWindowOpen = true;
    }

    private void CloseStudentSpeechWindow()
    {
        _studentSpeechWindowOpen = false;
        OnStudentSpeakingChanged?.Invoke(false);
    }

    private static async Task<bool> WaitForSignalAsync(Action<TaskCompletionSource<bool>> register, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        register(tcs);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var reg = cts.Token.Register(() => tcs.TrySetResult(false));
        cts.CancelAfter(timeout);
        return await tcs.Task;
    }

    private static bool IsClarificationReason(string? reason) =>
        !string.IsNullOrWhiteSpace(reason) &&
        reason.StartsWith("clarification_", StringComparison.OrdinalIgnoreCase);

    private void EnsureSessionInitialized()
    {
        if (_sessionState.Questions.Count == 0)
        {
            throw new InvalidOperationException("Exam session does not contain any questions.");
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

    private ExamQuestionPrompt PresentCurrentQuestion()
    {
        if (_sessionState.CurrentQuestion is null)
        {
            throw new InvalidOperationException("Exam session does not contain an active question.");
        }

        var question = _sessionState.CurrentQuestion;
        var prompt = new ExamQuestionPrompt
        {
            QuestionId = question.Id,
            InstructionText = question.InstructionText,
            QuestionText = question.QuestionText,
            QuestionNumber = _sessionState.QuestionIndex + 1,
            TotalQuestions = _sessionState.Questions.Count
        };

        LocalFileLogger.Info("exam_flow", "question_presented", new
        {
            questionIndex = _sessionState.QuestionIndex,
            prompt.QuestionNumber,
            prompt.TotalQuestions,
            questionId = question.Id
        });
        OnQuestionPresented?.Invoke(prompt);
        return prompt;
    }

    private EvaluateTurnRequest BuildEvaluateTurnRequest(
        Question question,
        Guid attemptAnswerId,
        Guid paperItemId,
        int turnOrder,
        string promptText,
        string audioUrl) =>
        new()
        {
            AudioRef = audioUrl,
            AnswerId = attemptAnswerId,
            PaperItemId = paperItemId,
            TurnOrder = turnOrder,
            PromptText = promptText,
            Language = "en",
            Question = BuildQuestionContext(question)
        };

    private QuestionContextDto BuildQuestionContext(Question question)
    {
        _sessionState.EvaluationGuidesByQuestionId.TryGetValue(question.Id, out var evaluationGuide);

        return new QuestionContextDto
        {
            InstructionText = question.InstructionText,
            QuestionText = question.QuestionText,
            QuestionType = ToPythonQuestionType(question.Type),
            DifficultyLevel = NormalizeDifficultyLevel(question.DifficultyLevel),
            DurationSeconds = question.MaxResponseSeconds,
            MinResponseSeconds = question.MinResponseSeconds,
            MaxResponseSeconds = question.MaxResponseSeconds,
            EvaluationGuide = BuildEvaluationGuideDto(evaluationGuide),
            Asset = BuildQuestionAssetContextDto(question.Asset)
        };
    }

    private static EvaluationGuideDto? BuildEvaluationGuideDto(QuestionEvaluationGuide? guide)
    {
        if (guide is null)
        {
            return null;
        }

        return new EvaluationGuideDto
        {
            ExpectedContent = guide.ExpectedContent,
            KeyPoints = guide.KeyPoints,
            AcceptableResponses = guide.AcceptableResponses,
            OffTopicExamples = guide.OffTopicExamples,
            ScoringHints = guide.ScoringHints,
            CommonMistakes = guide.CommonMistakes
        };
    }

    private static string ToPythonQuestionType(QuestionType type) => type switch
    {
        QuestionType.ReadAloud => "read_aloud",
        QuestionType.ShortAnswer => "short_answer",
        QuestionType.LongAnswer => "long_answer",
        QuestionType.Opinion => "opinion",
        QuestionType.Description => "description",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private static QuestionAssetContextDto? BuildQuestionAssetContextDto(QuestionAsset? asset)
    {
        if (asset is null)
        {
            return null;
        }

        return new QuestionAssetContextDto
        {
            Type = ToPythonAssetType(asset.Type),
            Transcript = asset.Transcript,
            Description = asset.Description,
            AltText = asset.AltText
        };
    }

    private static string ToPythonAssetType(QuestionAssetType type) => type switch
    {
        QuestionAssetType.Audio => "audio",
        QuestionAssetType.Image => "image",
        QuestionAssetType.Video => "video",
        QuestionAssetType.TextPassage => "text_passage",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private static string NormalizeDifficultyLevel(string? difficultyLevel) =>
        string.IsNullOrWhiteSpace(difficultyLevel) ? "medium" : difficultyLevel.Trim().ToLowerInvariant();
}



