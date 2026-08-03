using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VoxOralExam.Core.Models;
using VoxOralExam.Core.Models.Dtos;
using VoxOralExam.Core.Interfaces;
using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.Services.DomainService;
using VoxOralExam.DesktopApp.Services.ExamFlow;
using VoxOralExam.DesktopApp.Services.ExamFlow.Question;
using VoxOralExam.DesktopApp.Services.Proctoring;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.ViewModels;

public class ExamViewModel : BaseViewModel
{
    private readonly CameraService _camera;
    private readonly IProctoringService _proctoring;
    private readonly ExamSessionState _sessionState;
    private readonly IExamFlowService _examFlow;
    private readonly AvatarWebRtcClient _avatarClient;
    private readonly IExamApiService _examApi;
    private readonly QuestionAssetPresentationCoordinator _assetPresentationCoordinator;
    private readonly IExamRecordingService _recording;
    private readonly AppSettings _settings;

    private string _studentName = string.Empty;
    private string _studentId = string.Empty;
    private string _examTitle = string.Empty;
    private string _currentQuestion = string.Empty;
    private string _timeRemaining = "00:00:00";
    private string _examDurationText = "Thời lượng bài thi: 30 phút";
    private string _questionSpeakingTime = "00:00 / --:--";
    private string _responseWindowText = "Thời gian trả lời: chưa cấu hình";
    private string _aiStatus = "Dang cho...";
    private string _cameraStatus = "Dang khoi dong camera...";
    private int _questionNumber;
    private int _totalQuestions;
    private bool _initialized;

    private DispatcherTimer? _countdownTimer;
    private int _remainingSecondsLocal;
    private DateTime _lastCheckpointAt;
    private bool _checkpointInFlight;
    private BitmapImage? _cameraPreview;
    private BitmapImage? _avatarVideoFrame;
    private bool _isAvatarSpeaking;
    private bool _isStudentSpeaking;
    private bool _isCameraOn;
    private bool _isCleaningUp;
    private bool _examCompleted;
    private Task? _cleanupTask;
    private QuestionAsset? _currentQuestionAsset;
    private BitmapImage? _currentQuestionAssetImage;
    private Uri? _currentQuestionMediaSource;
    private bool _isMicMuted;
    private bool _isSubmitting;
    private bool _showSubmittedOverlay;
    private bool _showErrorOverlay;
    private string _endScreenMessage = string.Empty;

    public ExamViewModel(
        CameraService camera,
        IProctoringService proctoring,
        ExamSessionState sessionState,
        IExamFlowService examFlow,
        AvatarWebRtcClient avatarClient,
        IExamApiService examApi,
        QuestionAssetPresentationCoordinator assetPresentationCoordinator,
        IExamRecordingService recording,
        AppSettings settings)
    {
        _camera = camera;
        _proctoring = proctoring;
        _sessionState = sessionState;
        _examFlow = examFlow;
        _avatarClient = avatarClient;
        _examApi = examApi;
        _assetPresentationCoordinator = assetPresentationCoordinator;
        _recording = recording;
        _settings = settings;

        LoadSessionData();

        _examFlow.OnQuestionPresented += HandleQuestionPresented;
        _examFlow.OnTranscriptAppended += HandleTranscriptAppended;
        _examFlow.OnStatusChanged += HandleExamStatusChanged;
        _examFlow.OnSessionReady += HandleSessionReady;
        _examFlow.OnExamEnded += HandleExamEnded;
        _examFlow.OnStudentSpeakingChanged += HandleStudentSpeakingChanged;
        _examFlow.OnAvatarSpeakingChanged += HandleAvatarSpeakingChanged;
        _examFlow.OnQuestionSpeakingTimeChanged += HandleQuestionSpeakingTimeChanged;
        _assetPresentationCoordinator.OnAssetDisplayRequested += HandleAssetDisplayRequested;
        _avatarClient.OnVideoFrame += HandleAvatarVideoFrame;
        _recording.StatusChanged += HandleRecordingStatusChanged;
        _isMicMuted = _examFlow.IsMicMuted;
        ToggleMuteCommand = new RelayCommand(ToggleMute);
        SubmitNowCommand = new RelayCommand(SubmitNowClicked);
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

    public string ExamDurationText
    {
        get => _examDurationText;
        set => SetProperty(ref _examDurationText, value);
    }

    public string QuestionSpeakingTime
    {
        get => _questionSpeakingTime;
        set => SetProperty(ref _questionSpeakingTime, value);
    }

    public string ResponseWindowText
    {
        get => _responseWindowText;
        set => SetProperty(ref _responseWindowText, value);
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

    public bool IsMicMuted
    {
        get => _isMicMuted;
        set
        {
            if (SetProperty(ref _isMicMuted, value))
            {
                OnPropertyChanged(nameof(MuteButtonText));
                OnPropertyChanged(nameof(MicStatusText));
            }
        }
    }

    public string MuteButtonText => IsMicMuted ? "Bật mic" : "Tắt mic";
    public string MicStatusText => IsMicMuted ? "Mic đang tắt" : "Mic đang bật";

    public bool ShowSubmittedOverlay
    {
        get => _showSubmittedOverlay;
        set => SetProperty(ref _showSubmittedOverlay, value);
    }

    public bool ShowErrorOverlay
    {
        get => _showErrorOverlay;
        set => SetProperty(ref _showErrorOverlay, value);
    }

    public string EndScreenMessage
    {
        get => _endScreenMessage;
        set => SetProperty(ref _endScreenMessage, value);
    }

    public ObservableCollection<LogEntry> LogEntries { get; } = new();
    public ICommand ToggleMuteCommand { get; }
    public ICommand SubmitNowCommand { get; }

    public QuestionAsset? CurrentQuestionAsset
    {
        get => _currentQuestionAsset;
        set
        {
            if (SetProperty(ref _currentQuestionAsset, value))
            {
                OnPropertyChanged(nameof(HasImageAsset));
                OnPropertyChanged(nameof(HasMediaAsset));
                OnPropertyChanged(nameof(HasTextPassageAsset));
            }
        }
    }

    public BitmapImage? CurrentQuestionAssetImage
    {
        get => _currentQuestionAssetImage;
        set
        {
            if (SetProperty(ref _currentQuestionAssetImage, value))
            {
                OnPropertyChanged(nameof(HasImageAsset));
            }
        }
    }

    public Uri? CurrentQuestionMediaSource
    {
        get => _currentQuestionMediaSource;
        set
        {
            if (SetProperty(ref _currentQuestionMediaSource, value))
            {
                OnPropertyChanged(nameof(HasMediaAsset));
            }
        }
    }

    public bool HasImageAsset =>
        CurrentQuestionAsset?.Type == QuestionAssetType.Image && CurrentQuestionAssetImage is not null;

    public bool HasMediaAsset =>
        CurrentQuestionAsset is not null
        && (CurrentQuestionAsset.Type == QuestionAssetType.Video || CurrentQuestionAsset.Type == QuestionAssetType.Audio)
        && CurrentQuestionMediaSource is not null;

    public bool HasTextPassageAsset =>
        CurrentQuestionAsset?.Type == QuestionAssetType.TextPassage
        && !string.IsNullOrWhiteSpace(CurrentQuestionAsset?.Transcript);

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await EnsureExamLoadedAsync();

        // An unmonitored exam has no stream token, so recording has nothing to authenticate with.
        // Handled before the try/catch below rather than inside it: that block turns every failure
        // into "recording could not start", which is the wrong story to tell about an exam that was
        // never meant to record -- and under RequireRecording it would rethrow and block entry.
        if (_sessionState.EntryTicket is { IsMonitored: false })
        {
            LocalFileLogger.Info("recording", "recording_skipped_exam_not_monitored", new
            {
                _sessionState.ExamAttemptId
            });
            AddLog("Bài thi này không yêu cầu giám sát, bỏ qua ghi hình.");
            PrepareProctoringUi();
            await _examFlow.StartAsync(CancellationToken.None);
            return;
        }

        try
        {
            var ticket = _sessionState.EntryTicket
                ?? throw new InvalidOperationException("Exam entry ticket is missing.");
            RecordingStreamType[] streamTypes = ticket.StreamTypes.Count == 0
                ? [RecordingStreamType.Camera, RecordingStreamType.Screen]
                : ticket.StreamTypes
                    .Select(value => value.Trim().ToLowerInvariant())
                    .Select(value => value switch
                    {
                        "camera" => RecordingStreamType.Camera,
                        "screen" => RecordingStreamType.Screen,
                        _ => throw new InvalidOperationException($"Unsupported stream type: {value}")
                    })
                    .Distinct()
                    .ToArray();

            await _recording.StartAsync(
                new RecordingSessionContext(
                    ticket.AttemptId,
                    string.IsNullOrWhiteSpace(ticket.ScheduleId)
                        ? "local"
                        : ticket.ScheduleId,
                    string.IsNullOrWhiteSpace(ticket.SessionId)
                        ? ticket.AttemptId.ToString("D")
                        : ticket.SessionId,
                    ticket.StreamJwt,
                    streamTypes),
                CancellationToken.None);
            if (_settings.RequireRecording && !_recording.IsRecording)
            {
                throw new InvalidOperationException("Recording is required before the exam can start.");
            }
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("recording", "exam_recording_start_failed", ex);
            AddLog($"Local recording could not start: {ex.Message}", LogType.Warning);
            if (_settings.RequireRecording)
            {
                throw;
            }
        }

        PrepareProctoringUi();
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
            await _recording.StopAsync(
                _examCompleted ? RecordingStopReason.Submitted : RecordingStopReason.UserClosed,
                CancellationToken.None);
            // ExamWindow is always the last window in the real exam flow -- safe to tear down the
            // shared upload worker here rather than leaving it for App.xaml.cs's OnExit fallback.
            await _recording.ShutdownAsync();
            _examFlow.OnQuestionPresented -= HandleQuestionPresented;
            _examFlow.OnTranscriptAppended -= HandleTranscriptAppended;
            _examFlow.OnStatusChanged -= HandleExamStatusChanged;
            _examFlow.OnSessionReady -= HandleSessionReady;
            _examFlow.OnExamEnded -= HandleExamEnded;
            _examFlow.OnStudentSpeakingChanged -= HandleStudentSpeakingChanged;
            _examFlow.OnAvatarSpeakingChanged -= HandleAvatarSpeakingChanged;
            _examFlow.OnQuestionSpeakingTimeChanged -= HandleQuestionSpeakingTimeChanged;
            _assetPresentationCoordinator.OnAssetDisplayRequested -= HandleAssetDisplayRequested;
            _avatarClient.OnVideoFrame -= HandleAvatarVideoFrame;
            _camera.OnPreviewFrame -= HandlePreviewFrame;
            _proctoring.OnStatusChanged -= HandleProctoringStatusChanged;
            _proctoring.OnProctoringEvent -= HandleProctoringEvent;
            _recording.StatusChanged -= HandleRecordingStatusChanged;
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
        ExamDurationText = $"Thời lượng mã đề: {FormatDuration(_sessionState.DurationSeconds)}";
        CurrentQuestion = _sessionState.CurrentQuestion?.QuestionText ?? string.Empty;
        QuestionNumber = _sessionState.QuestionIndex + 1;
        TotalQuestions = _sessionState.Questions.Count;
        ApplyCurrentQuestionAsset(_sessionState.CurrentQuestion?.Asset);

        LogEntries.Add(new LogEntry { Time = DateTime.Now.AddMinutes(-2), Message = "Nguoi dung da dang nhap thanh cong", Type = LogType.Success });
        LogEntries.Add(new LogEntry { Time = DateTime.Now.AddMinutes(-1), Message = $"Thiet bi: {_sessionState.CurrentUser?.Device.DeviceName ?? "unknown"}", Type = LogType.Info });
    }

    private async Task EnsureExamLoadedAsync()
    {
        if (_sessionState.Questions.Count > 0)
        {
            return;
        }

        var sessionId = _sessionState.EntryTicket?.AttemptId != Guid.Empty
            ? _sessionState.EntryTicket?.AttemptId.ToString()
            : (_sessionState.ExamAttemptId == Guid.Empty ? null : _sessionState.ExamAttemptId.ToString());
        var paper = await _examApi.GetExamPaperAsync(sessionId);
        _sessionState.LoadExamPaper(paper, _sessionState.EntryTicket?.AttemptId);
    }

    private void PrepareProctoringUi()
    {
        _camera.OnPreviewFrame += HandlePreviewFrame;
        _proctoring.OnStatusChanged += HandleProctoringStatusChanged;
        _proctoring.OnProctoringEvent += HandleProctoringEvent;
        CameraStatus = "Đang khởi động camera...";
    }

    private void HandlePreviewFrame(BitmapImage bitmapImage)
    {
        IsCameraOn = true;
        CameraStatus = "Camera đã bật";
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
        CameraStatus = status;
        AddLog(status, LogType.Info);
    }

    private void HandleProctoringEvent(ProctoringEvent evt)
    {
        AddLog($"[{evt.Type}] {evt.Message}", LogType.Warning);
    }

    // Ca thi + avatar (neu bat) da ket noi xong -- day la lan dau tien, va la lan duy nhat,
    // dong ho dem nguoc duoc phep bat dau chay. Truoc do (constructor chay ngay sau khi
    // vao man hinh thi/login) hoc sinh chua the tuong tac voi AI, khong duoc tinh vao gio lam bai.
    private void HandleSessionReady()
    {
        Application.Current.Dispatcher.Invoke(StartCountdown);
    }

    private void StartCountdown()
    {
        _countdownTimer?.Stop();
        _remainingSecondsLocal = Math.Max(
            0,
            _sessionState.RemainingSeconds
                ?? (_sessionState.DurationSeconds > 0 ? _sessionState.DurationSeconds : 30 * 60));
        _lastCheckpointAt = DateTime.UtcNow;
        RefreshRemainingTime();

        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += (_, _) =>
        {
            // Khong tru giay khi avatar dang noi (cau hoi, huong dan, thong bao chuan bi,
            // follow-up...) -- IsAvatarSpeaking duoc cap nhat qua HandleAvatarSpeakingChanged,
            // bao trum dung moi lan _avatarSpeaker.SpeakAsync chay (xem
            // RealtimeExamFlowService.EventHandlers.cs). Van tru binh thuong trong luc hoc sinh
            // chuan bi (im lang) va luc hoc sinh dang tra loi.
            if (_remainingSecondsLocal > 0 && !IsAvatarSpeaking)
            {
                _remainingSecondsLocal--;
            }
            RefreshRemainingTime();

            if (DateTime.UtcNow - _lastCheckpointAt >= TimeSpan.FromSeconds(10))
            {
                _lastCheckpointAt = DateTime.UtcNow;
                _ = CheckpointRemainingTimeBestEffortAsync();
            }

            if (_remainingSecondsLocal <= 0)
            {
                _countdownTimer.Stop();
                AddLog("Hết giờ làm bài, tự động nộp bài", LogType.Warning);
                // Hết giờ đếm ngược -- tự động nộp bài luôn, không chờ học sinh tự bấm (dù có
                // trả lời hết câu hỏi hay chưa). Không cần confirm() vì đây là hệ thống tự làm,
                // không phải hành động của học sinh.
                TriggerSubmitNow();
            }
        };
        _countdownTimer.Start();
    }

    private void RefreshRemainingTime()
    {
        var remaining = TimeSpan.FromSeconds(Math.Max(0, _remainingSecondsLocal));
        TimeRemaining = _remainingSecondsLocal <= 0
            ? "Het gio!"
            : remaining.ToString(@"hh\:mm\:ss");
    }

    private async Task CheckpointRemainingTimeBestEffortAsync()
    {
        if (_checkpointInFlight || _sessionState.ExamAttemptId == Guid.Empty)
        {
            return;
        }

        _checkpointInFlight = true;
        try
        {
            await _examApi.UpdateRemainingTimeAsync(
                _sessionState.ExamAttemptId,
                Math.Max(0, _remainingSecondsLocal),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error(
                "exam_timer",
                "remaining_time_checkpoint_failed",
                ex,
                new
                {
                    sessionId = _sessionState.ExamAttemptId,
                    remainingSeconds = _remainingSecondsLocal
                });
        }
        finally
        {
            _checkpointInFlight = false;
        }
    }

    private void SubmitNowClicked()
    {
        if (_isSubmitting)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            "Bạn chắc chắn muốn nộp bài ngay bây giờ? Các câu hỏi chưa trả lời sẽ được tính là chưa trả lời và không thể làm lại.",
            "Xác nhận nộp bài",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!confirmed)
        {
            return;
        }

        AddLog("Học sinh chủ động nộp bài trước khi hết giờ", LogType.Info);
        TriggerSubmitNow();
    }

    private void TriggerSubmitNow()
    {
        if (_isSubmitting)
        {
            return;
        }

        _isSubmitting = true;
        _countdownTimer?.Stop();
        _ = _examFlow.SubmitNowAsync();
    }

    private void HandleQuestionPresented(ExamQuestionPrompt prompt)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            CurrentQuestion = prompt.QuestionText;
            QuestionNumber = prompt.QuestionNumber;
            TotalQuestions = prompt.TotalQuestions;
            ResponseWindowText = FormatResponseWindow(prompt.MinResponseSeconds, prompt.MaxResponseSeconds);
            QuestionSpeakingTime = FormatQuestionSpeakingTime(TimeSpan.Zero, TimeSpan.FromSeconds(Math.Max(0, prompt.MaxResponseSeconds)));
            AddLog($"Dang hien cau {prompt.QuestionNumber}: {prompt.QuestionText}", LogType.Info);
        });
    }

    private void HandleQuestionSpeakingTimeChanged(TimeSpan elapsed, TimeSpan limit)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            QuestionSpeakingTime = FormatQuestionSpeakingTime(elapsed, limit);
        });
    }

    public void NotifyQuestionAssetMediaEnded()
    {
        _assetPresentationCoordinator.CompleteMediaPlayback();
    }

    public void NotifyQuestionAssetMediaFailed(string? reason = null)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrWhiteSpace(reason))
            {
                AddLog($"không thể phát media asset: {reason}", LogType.Warning);
            }
        });
        _assetPresentationCoordinator.CompleteMediaPlayback();
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

    private void HandleExamEnded(bool succeeded)
    {
        _examCompleted = succeeded;
        Application.Current.Dispatcher.Invoke(() =>
        {
            ShowSubmittedOverlay = succeeded;
            ShowErrorOverlay = !succeeded;
            if (succeeded)
            {
                AiStatus = "Đã hoàn thành bài thi";
                EndScreenMessage = "Bài thi đã được nộp thành công. Hệ thống sẽ đóng sau ít giây nữa.";
                AddLog("Bài thi vấn đáp đã hoàn thành", LogType.Success);
            }
            else
            {
                AiStatus = "Bài thi tạm dừng để xem xét";
                EndScreenMessage = "Bài thi đã kết thúc. Nếu cần hỗ trợ, vui lòng liên hệ giám thị/nhà trường.";
                AddLog("Bài thi đã kết thúc không bình thường", LogType.Warning);
            }
        });

        _ = CloseWindowAfterDelayAsync(succeeded ? 4 : 6);
    }

    private void HandleRecordingStatusChanged(RecordingStatus status)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            AddLog(status.Message, status.IsDegraded ? LogType.Warning : LogType.Info);
        });
    }

    private async Task CloseWindowAfterDelayAsync(int seconds)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var window = Application.Current.Windows
                    .OfType<Window>()
                    .FirstOrDefault(current => ReferenceEquals(current.DataContext, this));
                window?.Close();
            });
        }
        catch
        {
            // Best-effort auto close only. Cleanup still runs if the user closes manually.
        }
    }

    private void HandleAssetDisplayRequested(QuestionAsset? asset)
    {
        Application.Current.Dispatcher.Invoke(() => ApplyCurrentQuestionAsset(asset));
    }

    private void ApplyCurrentQuestionAsset(QuestionAsset? asset)
    {
        CurrentQuestionAsset = asset;
        CurrentQuestionAssetImage = null;
        CurrentQuestionMediaSource = null;

        if (asset is null)
        {
            return;
        }

        if (asset.Type == QuestionAssetType.TextPassage)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(asset.Url))
        {
            return;
        }

        try
        {
            if (asset.Type == QuestionAssetType.Image)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(asset.Url, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                CurrentQuestionAssetImage = image;
                return;
            }

            if (asset.Type == QuestionAssetType.Video || asset.Type == QuestionAssetType.Audio)
            {
                CurrentQuestionMediaSource = new Uri(asset.Url, UriKind.Absolute);
            }
        }
        catch (Exception ex)
        {
            AddLog($"Khong the tai asset cau hoi: {ex.Message}", LogType.Warning);
        }
    }

    private void ToggleMute()
    {
        var nextState = !IsMicMuted;
        _examFlow.SetMicMuted(nextState);
        IsMicMuted = _examFlow.IsMicMuted;
        AddLog(IsMicMuted ? "Đã tắt mic của học sinh" : "Đã bật lại mic của học sinh", LogType.Info);
    }

    private static string FormatResponseWindow(int minResponseSeconds, int maxResponseSeconds)
    {
        var hasMin = minResponseSeconds > 0;
        var hasMax = maxResponseSeconds > 0;
        if (hasMin && hasMax)
        {
            return $"Thời gian trả lời: tối thiểu {minResponseSeconds}s, tối đa {maxResponseSeconds}s";
        }
        if (hasMin)
        {
            return $"Thời gian trả lời: tối thiểu {minResponseSeconds}s";
        }
        if (hasMax)
        {
            return $"Thời gian trả lời: tối đa {maxResponseSeconds}s";
        }
        return "Thời gian trả lời: chưa cấu hình";
    }

    private static string FormatDuration(int seconds)
    {
        if (seconds <= 0)
        {
            return "30 phút";
        }

        var minutes = seconds / 60;
        var remainingSeconds = seconds % 60;
        if (minutes <= 0)
        {
            return $"{remainingSeconds}s";
        }

        return remainingSeconds == 0
            ? $"{minutes} phút"
            : $"{minutes} phút {remainingSeconds}s";
    }

    private static string FormatQuestionSpeakingTime(TimeSpan elapsed, TimeSpan limit)
    {
        static string Format(TimeSpan value) => value.ToString(value.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");

        if (limit <= TimeSpan.Zero)
        {
            return $"{Format(elapsed)} / --:--";
        }

        var clippedElapsed = elapsed > limit ? limit : elapsed;
        return $"{Format(clippedElapsed)} / {Format(limit)}";
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


