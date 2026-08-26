using VoxOralExam.Core.Interfaces;
using VoxOralExam.Core.Models.Dtos;
using VoxOralExam.DesktopApp.Services.ExamFlow.Attempt;

namespace VoxOralExam.DesktopApp.Services.ExamFlow;

/// <summary>
/// ViewModel-facing façade. Attempt, question and turn state live in their
/// dedicated runtime components and are recreated for every StartAsync call.
/// </summary>
public sealed class RealtimeExamFlowService : IExamFlowService
{
    private readonly ExamAttemptRunnerFactory _runnerFactory;
    private ExamAttemptRunner? _runner;
    private Task? _runTask;
    private bool _isMicMuted;

    public RealtimeExamFlowService(ExamAttemptRunnerFactory runnerFactory)
    {
        _runnerFactory = runnerFactory;
    }

    public event Action<ExamQuestionPrompt>? OnQuestionPresented;
    public event Action<string>? OnTranscriptAppended;
    public event Action<string>? OnStatusChanged;
    public event Action? OnSessionReady;
    public event Action<bool>? OnExamEnded;
    public event Action<bool>? OnStudentSpeakingChanged;
    public event Action<bool>? OnAvatarSpeakingChanged;
    public event Action<string>? OnAvatarUtteranceChanged;
    public event Action<TimeSpan, TimeSpan>? OnQuestionSpeakingTimeChanged;
    public event Action<bool>? OnFinalSaveStateChanged;

    public bool IsMicMuted => _runner?.IsMicMuted ?? _isMicMuted;

    // Chưa có runner = chưa vào lượt thi nào; coi là còn sống để đồng hồ không bị chặn oan.
    public bool IsRealtimeAlive => _runner?.IsRealtimeAlive ?? true;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_runTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        var runner = _runnerFactory.Create(_isMicMuted);
        _runner = runner;
        WireRunner(runner);
        _runTask = RunAndUnwireAsync(runner, cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var runner = _runner;
        if (runner is null)
        {
            return;
        }
        runner.RequestStop();
        await AwaitRunTaskAsync();
    }

    public async Task SubmitNowAsync()
    {
        var runner = _runner;
        if (runner is null)
        {
            return;
        }
        runner.RequestSubmit();
        await AwaitRunTaskAsync();
    }

    public void SetMicMuted(bool muted)
    {
        _isMicMuted = muted;
        _runner?.SetMicMuted(muted);
    }

    /// <summary>
    /// Chưa vào phiên (runner null) thì không có gì để dừng -- người gọi vẫn tự đóng màn thi.
    /// </summary>
    public void ForceEndFromServer(string reason) => _runner?.ForceEndFromServer(reason);

    public Task ReportFocusLostAsync(DateTimeOffset capturedAt)
    {
        // Chưa vào phiên (runner null) thì không có WS để gửi -- bỏ qua, log máy trạm vẫn giữ
        // bằng chứng.
        return _runner?.ReportFocusLostAsync(capturedAt) ?? Task.CompletedTask;
    }

    public Task ReportCameraSignalLostAsync(DateTimeOffset capturedAt, bool neverDelivered)
        => _runner?.ReportCameraSignalLostAsync(capturedAt, neverDelivered) ?? Task.CompletedTask;

    public Task ReportCameraSignalRestoredAsync(DateTimeOffset capturedAt, TimeSpan outage)
        => _runner?.ReportCameraSignalRestoredAsync(capturedAt, outage) ?? Task.CompletedTask;

    public Task ReportAssetPlaybackFailedAsync(string reason, int questionNumber)
        => _runner?.ReportAssetPlaybackFailedAsync(reason, questionNumber) ?? Task.CompletedTask;

    private async Task RunAndUnwireAsync(
        ExamAttemptRunner runner,
        CancellationToken cancellationToken)
    {
        try
        {
            await runner.RunAsync(cancellationToken);
        }
        finally
        {
            UnwireRunner(runner);
        }
    }

    private async Task AwaitRunTaskAsync()
    {
        var task = _runTask;
        if (task is null)
        {
            return;
        }
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // The runner translates cancellation into SUBMITTED or INTERRUPTED.
        }
    }

    private void WireRunner(ExamAttemptRunner runner)
    {
        runner.QuestionPresented += HandleQuestionPresented;
        runner.TranscriptAppended += HandleTranscriptAppended;
        runner.StatusChanged += HandleStatusChanged;
        runner.SessionReady += HandleSessionReady;
        runner.ExamEnded += HandleExamEnded;
        runner.StudentSpeakingChanged += HandleStudentSpeakingChanged;
        runner.AvatarSpeakingChanged += HandleAvatarSpeakingChanged;
        runner.AvatarUtteranceChanged += HandleAvatarUtteranceChanged;
        runner.QuestionSpeakingTimeChanged += HandleQuestionSpeakingTimeChanged;
        runner.FinalSaveStateChanged += HandleFinalSaveStateChanged;
    }

    private void UnwireRunner(ExamAttemptRunner runner)
    {
        runner.QuestionPresented -= HandleQuestionPresented;
        runner.TranscriptAppended -= HandleTranscriptAppended;
        runner.StatusChanged -= HandleStatusChanged;
        runner.SessionReady -= HandleSessionReady;
        runner.ExamEnded -= HandleExamEnded;
        runner.StudentSpeakingChanged -= HandleStudentSpeakingChanged;
        runner.AvatarSpeakingChanged -= HandleAvatarSpeakingChanged;
        runner.AvatarUtteranceChanged -= HandleAvatarUtteranceChanged;
        runner.QuestionSpeakingTimeChanged -= HandleQuestionSpeakingTimeChanged;
        runner.FinalSaveStateChanged -= HandleFinalSaveStateChanged;
    }

    private void HandleQuestionPresented(ExamQuestionPrompt value) =>
        OnQuestionPresented?.Invoke(value);

    private void HandleTranscriptAppended(string value) =>
        OnTranscriptAppended?.Invoke(value);

    private void HandleStatusChanged(string value) =>
        OnStatusChanged?.Invoke(value);

    private void HandleSessionReady() => OnSessionReady?.Invoke();

    private void HandleExamEnded(bool value) => OnExamEnded?.Invoke(value);

    private void HandleStudentSpeakingChanged(bool value) =>
        OnStudentSpeakingChanged?.Invoke(value);

    private void HandleAvatarSpeakingChanged(bool value) =>
        OnAvatarSpeakingChanged?.Invoke(value);

    private void HandleAvatarUtteranceChanged(string value) =>
        OnAvatarUtteranceChanged?.Invoke(value);

    private void HandleQuestionSpeakingTimeChanged(
        TimeSpan elapsed,
        TimeSpan limit) =>
        OnQuestionSpeakingTimeChanged?.Invoke(elapsed, limit);

    private void HandleFinalSaveStateChanged(bool saving) =>
        OnFinalSaveStateChanged?.Invoke(saving);
}
