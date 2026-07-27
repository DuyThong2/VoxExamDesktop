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
            archiveQueue);

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
            await CompleteAttemptAsync(
                archiveQueue,
                "SUBMITTED",
                notifyEnded: true,
                completed: true);
        }
        catch (OperationCanceledException)
        {
            if (_submitRequested)
            {
                await SendFarewellBestEffortAsync(presentation);
                await CompleteAttemptAsync(
                    archiveQueue,
                    "SUBMITTED",
                    notifyEnded: true,
                    completed: true);
            }
            else
            {
                await CompleteAttemptAsync(
                    archiveQueue,
                    "INTERRUPTED",
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
        bool notifyEnded,
        bool completed)
    {
        await archiveQueue.DrainAsync(TimeSpan.FromSeconds(30));
        await SubmitSessionStatusAsync(status);
        await StopProctoringAsync();
        if (notifyEnded)
        {
            ExamEnded?.Invoke(completed);
        }
    }

    private async Task SendFarewellBestEffortAsync(
        QuestionPresentationService presentation)
    {
        try
        {
            await presentation.WaitForAvatarAfterAsync(
                token => _sessionClient.SendExamEndAndWaitForAckAsync(token),
                CancellationToken.None);
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
