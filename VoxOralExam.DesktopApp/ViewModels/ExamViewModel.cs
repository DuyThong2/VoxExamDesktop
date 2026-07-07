using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VoxOralExam.Core.Dtos;
using VoxOralExam.Core.Interfaces;
using VoxOralExam.DesktopApp.Infrastructure;
using VoxOralExam.DesktopApp.Models;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.ViewModels;

public class ExamViewModel : BaseViewModel
{
    private readonly CameraService _camera;
    private readonly ScreenProctoringService _proctoring;
    private readonly ExamSessionState _sessionState;
    private readonly IExamFlowService _examFlow;
    private readonly AvatarWebRtcClient _avatarClient;
    private readonly IExamApiService _examApi;

    private string _studentName = string.Empty;
    private string _studentId = string.Empty;
    private string _examTitle = string.Empty;
    private string _currentQuestion = string.Empty;
    private string _timeRemaining = "00:00:00";
    private string _aiStatus = "Dang cho...";
    private string _cameraStatus = "Dang khoi dong camera...";
    private int _questionNumber;
    private int _totalQuestions;
    private bool _initialized;

    private DispatcherTimer? _countdownTimer;
    private TimeSpan _remainingTime;
    private BitmapImage? _cameraPreview;
    private BitmapImage? _avatarVideoFrame;
    private bool _isAvatarSpeaking;
    private bool _isStudentSpeaking;
    private bool _isCameraOn;
    private bool _isCleaningUp;
    private Task? _cleanupTask;

    public ExamViewModel(
        CameraService camera,
        ScreenProctoringService proctoring,
        ExamSessionState sessionState,
        IExamFlowService examFlow,
        AvatarWebRtcClient avatarClient,
        IExamApiService examApi)
    {
        _camera = camera;
        _proctoring = proctoring;
        _sessionState = sessionState;
        _examFlow = examFlow;
        _avatarClient = avatarClient;
        _examApi = examApi;

        LoadSessionData();

        _examFlow.OnQuestionPresented += HandleQuestionPresented;
        _examFlow.OnTranscriptAppended += HandleTranscriptAppended;
        _examFlow.OnStatusChanged += HandleExamStatusChanged;
        _examFlow.OnExamCompleted += HandleExamCompleted;
        _examFlow.OnStudentSpeakingChanged += HandleStudentSpeakingChanged;
        _avatarClient.OnVideoFrame += HandleAvatarVideoFrame;
        _avatarClient.OnSpeakingChanged += HandleAvatarSpeakingChanged;

        StartCountdown();
    }

    public string StudentName
    {
        get => _studentName;
        set => SetProperty(ref _studentName, value);
    }

    public string StudentId
    {
        get => _studentId;
        set => SetProperty(ref _studentId, value);
    }

    public string ExamTitle
    {
        get => _examTitle;
        set => SetProperty(ref _examTitle, value);
    }

    public string CurrentQuestion
    {
        get => _currentQuestion;
        set => SetProperty(ref _currentQuestion, value);
    }

    public string TimeRemaining
    {
        get => _timeRemaining;
        set => SetProperty(ref _timeRemaining, value);
    }

    public string AiStatus
    {
        get => _aiStatus;
        set => SetProperty(ref _aiStatus, value);
    }

    public string CameraStatus
    {
        get => _cameraStatus;
        set => SetProperty(ref _cameraStatus, value);
    }

    public int QuestionNumber
    {
        get => _questionNumber;
        set => SetProperty(ref _questionNumber, value);
    }

    public int TotalQuestions
    {
        get => _totalQuestions;
        set => SetProperty(ref _totalQuestions, value);
    }

    public BitmapImage? CameraPreview
    {
        get => _cameraPreview;
        set => SetProperty(ref _cameraPreview, value);
    }

    public bool IsCameraOn
    {
        get => _isCameraOn;
        set => SetProperty(ref _isCameraOn, value);
    }

    public BitmapImage? AvatarVideoFrame
    {
        get => _avatarVideoFrame;
        set => SetProperty(ref _avatarVideoFrame, value);
    }

    public bool IsAvatarSpeaking
    {
        get => _isAvatarSpeaking;
        set => SetProperty(ref _isAvatarSpeaking, value);
    }

    public bool IsStudentSpeaking
    {
        get => _isStudentSpeaking;
        set => SetProperty(ref _isStudentSpeaking, value);
    }

    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await EnsureExamLoadedAsync();
        await StartCameraAsync();
        await _examFlow.StartAsync(CancellationToken.None);
    }

    public void AddLog(string message, LogType type = LogType.Info)
    {
        LogEntries.Add(new LogEntry
        {
            Time = DateTime.Now,
            Message = message,
            Type = type
        });
    }

    public Task CleanupAsync()
    {
        if (_cleanupTask is not null)
        {
            return _cleanupTask;
        }

        _cleanupTask = CleanupCoreAsync();
        return _cleanupTask;
    }

    private async Task CleanupCoreAsync()
    {
        if (_isCleaningUp)
        {
            return;
        }

        _isCleaningUp = true;

        try
        {
            _countdownTimer?.Stop();
            await _examFlow.StopAsync();
            await _proctoring.StopAsync();
            _examFlow.OnQuestionPresented -= HandleQuestionPresented;
            _examFlow.OnTranscriptAppended -= HandleTranscriptAppended;
            _examFlow.OnStatusChanged -= HandleExamStatusChanged;
            _examFlow.OnExamCompleted -= HandleExamCompleted;
            _examFlow.OnStudentSpeakingChanged -= HandleStudentSpeakingChanged;
            _avatarClient.OnVideoFrame -= HandleAvatarVideoFrame;
            _avatarClient.OnSpeakingChanged -= HandleAvatarSpeakingChanged;
            _camera.OnPreviewFrame -= HandlePreviewFrame;
            _proctoring.OnStatusChanged -= HandleProctoringStatusChanged;
            _proctoring.OnProctoringEvent -= HandleProctoringEvent;
            _proctoring.Dispose();
        }
        finally
        {
            _isCleaningUp = false;
        }
    }

    private void LoadSessionData()
    {
        StudentName = _sessionState.CurrentUser?.DisplayName ?? _sessionState.CurrentUser?.Login ?? "Unknown user";
        StudentId = _sessionState.CurrentUser?.UserId ?? "N/A";
        ExamTitle = string.IsNullOrWhiteSpace(_sessionState.ExamTitle)
            ? "Ky thi van dap"
            : _sessionState.ExamTitle;
        CurrentQuestion = _sessionState.CurrentQuestion?.QuestionText ?? string.Empty;
        QuestionNumber = _sessionState.QuestionIndex + 1;
        TotalQuestions = _sessionState.Questions.Count;

        LogEntries.Add(new LogEntry { Time = DateTime.Now.AddMinutes(-2), Message = "Nguoi dung da dang nhap thanh cong", Type = LogType.Success });
        LogEntries.Add(new LogEntry { Time = DateTime.Now.AddMinutes(-1), Message = $"Thiet bi: {_sessionState.CurrentUser?.Device.DeviceName ?? "unknown"}", Type = LogType.Info });
    }

    private async Task EnsureExamLoadedAsync()
    {
        if (_sessionState.Questions.Count > 0)
        {
            return;
        }

        // Safety net only: the normal path loads the paper in MainViewModel before this window
        // opens. If nothing is loaded, fall back to whatever exam id the session already knows
        // (the mock service returns its first paper for a null id).
        var examId = _sessionState.ExamId == Guid.Empty ? null : _sessionState.ExamId.ToString();
        var paper = await _examApi.GetExamPaperAsync(examId);
        _sessionState.LoadExamPaper(paper);
    }

    private async Task StartCameraAsync()
    {
        try
        {
            _camera.OnPreviewFrame += HandlePreviewFrame;

            if (!IsCameraOn)
            {
                await _camera.StartAsync();
            }

            IsCameraOn = true;
            CameraStatus = "Camera da bat";
            AddLog("Camera da bat", LogType.Success);

            _proctoring.OnStatusChanged += HandleProctoringStatusChanged;
            _proctoring.OnProctoringEvent += HandleProctoringEvent;
            await _proctoring.StartAsync();
        }
        catch (Exception ex)
        {
            IsCameraOn = false;
            CameraStatus = $"Loi camera: {ex.Message}";
            AddLog(CameraStatus, LogType.Error);
        }
    }

    private void HandlePreviewFrame(BitmapImage bitmapImage)
    {
        CameraPreview = bitmapImage;
    }

    private void HandleAvatarVideoFrame(BitmapImage bitmapImage)
    {
        Application.Current.Dispatcher.Invoke(() => AvatarVideoFrame = bitmapImage);
    }

    private void HandleAvatarSpeakingChanged(bool isSpeaking)
    {
        Application.Current.Dispatcher.Invoke(() => IsAvatarSpeaking = isSpeaking);
    }

    private void HandleStudentSpeakingChanged(bool isSpeaking)
    {
        Application.Current.Dispatcher.Invoke(() => IsStudentSpeaking = isSpeaking);
    }

    private void HandleProctoringStatusChanged(string status)
    {
        AddLog(status, LogType.Info);
    }

    private void HandleProctoringEvent(Models.ProctoringEvent evt)
    {
        AddLog($"[{evt.Type}] {evt.Message}", LogType.Warning);
    }

    private void StartCountdown()
    {
        _remainingTime = TimeSpan.FromMinutes(30);
        if (_sessionState.DurationMinutes > 0)
        {
            _remainingTime = TimeSpan.FromMinutes(_sessionState.DurationMinutes);
        }
        TimeRemaining = _remainingTime.ToString(@"hh\:mm\:ss");

        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += (_, _) =>
        {
            _remainingTime = _remainingTime.Subtract(TimeSpan.FromSeconds(1));
            TimeRemaining = _remainingTime.TotalSeconds <= 0
                ? "Het gio!"
                : _remainingTime.ToString(@"hh\:mm\:ss");

            if (_remainingTime.TotalSeconds <= 0)
            {
                _countdownTimer.Stop();
                AddLog("Het thoi gian lam bai", LogType.Warning);
            }
        };
        _countdownTimer.Start();
    }

    private void HandleQuestionPresented(ExamQuestionPrompt prompt)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            CurrentQuestion = prompt.QuestionText;
            QuestionNumber = prompt.QuestionNumber;
            TotalQuestions = prompt.TotalQuestions;
            AddLog($"Dang hien cau {prompt.QuestionNumber}: {prompt.QuestionText}", LogType.Info);
        });
    }

    private void HandleTranscriptAppended(string transcript)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            AddLog($"Transcript: {transcript}", LogType.Info);
        });
    }

    private void HandleExamStatusChanged(string status)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            AiStatus = status;
            AddLog(status, LogType.Info);
        });
    }

    private void HandleExamCompleted()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            AiStatus = "Da hoan thanh bai thi";
            AddLog("Bai thi van dap da hoan thanh", LogType.Success);
        });
    }
}

public class LogEntry
{
    public DateTime Time { get; set; }
    public string Message { get; set; } = string.Empty;
    public LogType Type { get; set; }
}

public enum LogType
{
    Info,
    Success,
    Warning,
    Error
}
