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
    /// <summary>
    /// How long to wait for the realtime session to become usable before giving the student their
    /// window back. Generous on purpose: a slow-but-working connection must never trip this.
    /// </summary>
    private const int SessionReadyWatchdogSeconds = 120;

    /// <summary>
    /// Hard ceiling on the "attempt is finishing" phase, sized well above the worst realistic sum of
    /// the farewell + archive-drain + settle budgets in <see cref="AppSettings"/>, so it only ever
    /// fires when the flow has genuinely died without raising OnExamEnded.
    /// </summary>
    private const int ExamEndWatchdogSeconds = 180;

    private readonly CameraService _camera;
    private readonly CameraSignalGuard _cameraSignalGuard;
    private readonly IProctoringService _proctoring;
    private readonly ExamSessionState _sessionState;
    private readonly IExamFlowService _examFlow;
    private readonly AvatarWebRtcClient _avatarClient;
    private readonly IExamApiService _examApi;
    private readonly QuestionAssetPresentationCoordinator _assetPresentationCoordinator;
    private readonly IExamRecordingService _recording;
    private readonly AppSettings _settings;
    private readonly IQuestionAssetCache _assetCache;
    private readonly QuestionAssetAudioPlayer _assetAudioPlayer = new();

    private string _studentName = string.Empty;
    private string _studentId = string.Empty;
    private string _examTitle = string.Empty;
    private string _currentQuestion = string.Empty;
    private string _lastAiUtterance = string.Empty;
    private string _timeRemaining = "00:00:00";
    private string _examDurationText = "Thời lượng bài thi: 30 phút";
    private string _questionSpeakingTime = "00:00 / --:--";
    private string _responseWindowText = "Thời gian trả lời: chưa cấu hình";
    private string _aiStatus = "Đang chờ...";
    private string _cameraStatus = "Đang khởi động camera...";
    private int _questionNumber;
    private int _totalQuestions;
    private bool _initialized;

    private DispatcherTimer? _countdownTimer;
    private int _remainingSecondsLocal;
    private DateTime _lastCheckpointAt;
    private bool _checkpointInFlight;
    private DateTime _lastForceEndPollAt;
    private bool _forceEndPollInFlight;
    private bool _forceEndHandled;
    private BitmapImage? _cameraPreview;
    private BitmapImage? _avatarVideoFrame;
    private bool _isAvatarSpeaking;
    private bool _isStudentSpeaking;
    private bool _isCameraOn;
    private string _cameraSignalMessage = string.Empty;
    private bool _isCameraSignalLost;
    // Đã gửi cảnh báo mất tín hiệu cho lần mất đang diễn ra. Quyết định có gửi sự kiện phục hồi hay
    // không: một gián đoạn ngắn chưa từng báo cho ai thì cũng không có khoảng nào để đóng lại.
    private bool _cameraOutageReported;
    private bool _isCleaningUp;
    private bool _examCompleted;
    private Task? _cleanupTask;
    private bool _isRealtimeDisconnected;
    private bool _isAssetMediaPlaying;
    private bool _hasAssetMediaFinished;
    private bool _assetMediaRetryUsed;
    private QuestionAsset? _currentQuestionAsset;
    private BitmapImage? _currentQuestionAssetImage;
    private Uri? _currentQuestionMediaSource;
    private bool _isMicMuted;
    private bool _isSubmitting;
    private bool _isSavingFinalAnswer;
    private bool _showSubmittedOverlay;
    private bool _showErrorOverlay;
    private string _endScreenMessage = string.Empty;

    // Same reasoning as _isExamLocked below: the attempt is always connecting when this view model
    // is constructed, so the overlay is up before the window's first paint rather than appearing a
    // frame later.
    private bool _showConnectingOverlay = true;

    // Defaults to true: an ExamViewModel exists only to run an attempt, so the window it backs is
    // locked from construction and is unlocked exactly once, when that attempt is over.
    private bool _isExamLocked = true;
    private bool _sessionReady;
    private DispatcherTimer? _lockWatchdogTimer;

    public ExamViewModel(
        CameraService camera,
        CameraSignalGuard cameraSignalGuard,
        IProctoringService proctoring,
        ExamSessionState sessionState,
        IExamFlowService examFlow,
        AvatarWebRtcClient avatarClient,
        IExamApiService examApi,
        QuestionAssetPresentationCoordinator assetPresentationCoordinator,
        IExamRecordingService recording,
        AppSettings settings,
        IQuestionAssetCache assetCache)
    {
        _camera = camera;
        _cameraSignalGuard = cameraSignalGuard;
        _proctoring = proctoring;
        _sessionState = sessionState;
        _examFlow = examFlow;
        _avatarClient = avatarClient;
        _examApi = examApi;
        _assetPresentationCoordinator = assetPresentationCoordinator;
        _recording = recording;
        _settings = settings;
        _assetCache = assetCache;

        LoadSessionData();

        _examFlow.OnQuestionPresented += HandleQuestionPresented;
        _examFlow.OnTranscriptAppended += HandleTranscriptAppended;
        _examFlow.OnStatusChanged += HandleExamStatusChanged;
        _examFlow.OnSessionReady += HandleSessionReady;
        _examFlow.OnExamEnded += HandleExamEnded;
        _examFlow.OnStudentSpeakingChanged += HandleStudentSpeakingChanged;
        _examFlow.OnAvatarSpeakingChanged += HandleAvatarSpeakingChanged;
        _examFlow.OnAvatarUtteranceChanged += HandleAvatarUtteranceChanged;
        _examFlow.OnQuestionSpeakingTimeChanged += HandleQuestionSpeakingTimeChanged;
        _examFlow.OnFinalSaveStateChanged += HandleFinalSaveStateChanged;
        _assetPresentationCoordinator.OnAssetDisplayRequested += HandleAssetDisplayRequested;
        _assetPresentationCoordinator.MediaPlaybackStateChanged += HandleAssetMediaPlaybackChanged;
        _assetPresentationCoordinator.MediaStopRequested += HandleAssetMediaStopRequested;
        _assetAudioPlayer.PlaybackEnded += HandleAssetAudioEnded;
        _assetAudioPlayer.PlaybackFailed += HandleAssetAudioFailed;
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

    /// <summary>
    /// Bảo View dừng hẳn <c>MediaElement</c> khi lượt phát chạm trần an toàn. Đi qua đây vì
    /// MediaElement là đối tượng của View, ViewModel không cầm được nó.
    /// </summary>
    public event Action? MediaStopRequested;

    /// <summary>Phát lại media sau một lần <c>MediaFailed</c> -- xem NotifyQuestionAssetMediaFailed.</summary>
    public event Action? MediaRetryRequested;

    public string CurrentQuestion
    {
        get => _currentQuestion;
        set => SetProperty(ref _currentQuestion, value);
    }

    /// <summary>
    /// Câu AI vừa nói. Tách khỏi <see cref="CurrentQuestion"/> có chủ đích: câu hỏi gốc là mỏ neo
    /// đứng yên suốt câu, còn khối này chạy theo từng lượt (follow-up, nhắc lại, thông báo chuẩn
    /// bị). Gộp hai thứ vào một chỗ thì thí sinh mất ngữ cảnh đề bài ngay lượt follow-up đầu tiên
    /// -- rõ nhất lúc vào lại sau khi bị cấm, vì câu đầu tiên nghe được đã là follow-up.
    /// </summary>
    public string LastAiUtterance
    {
        get => _lastAiUtterance;
        set
        {
            if (SetProperty(ref _lastAiUtterance, value))
            {
                OnPropertyChanged(nameof(HasLastAiUtterance));
            }
        }
    }

    public bool HasLastAiUtterance => !string.IsNullOrWhiteSpace(LastAiUtterance);

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

    /// <summary>
    /// Nội dung banner mất tín hiệu camera; rỗng nghĩa là không hiện gì.
    ///
    /// <para>Tách khỏi <see cref="CameraStatus"/> vì hai thứ khác hẳn nhau: CameraStatus là trạng
    /// thái thường trực nằm cạnh khung preview, còn đây là thứ phải cắt ngang tầm mắt học viên --
    /// họ là người DUY NHẤT cắm lại được sợi dây, nên nếu họ không thấy thì cả chuỗi cảnh báo phía
    /// sau chỉ ghi lại một sự cố mà lẽ ra đã được sửa trong mười giây.</para>
    /// </summary>
    public string CameraSignalMessage
    {
        get => _cameraSignalMessage;
        private set => SetProperty(ref _cameraSignalMessage, value);
    }

    /// <summary>Đã vượt ngưỡng cảnh báo (đã báo giám thị) -- banner chuyển từ vàng sang đỏ.</summary>
    public bool IsCameraSignalLost
    {
        get => _isCameraSignalLost;
        private set => SetProperty(ref _isCameraSignalLost, value);
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

    /// <summary>
    /// True while the attempt is uploading the student's final answer and closing the session out.
    /// The student must not shut the machine down during this window, so it gets its own overlay.
    /// </summary>
    public bool IsSavingFinalAnswer
    {
        get => _isSavingFinalAnswer;
        private set
        {
            if (SetProperty(ref _isSavingFinalAnswer, value))
            {
                OnPropertyChanged(nameof(CanInteract));
            }
        }
    }

    /// <summary>
    /// Notifying so the controls grey out the instant the countdown auto-submits, not only when
    /// the student clicked the button themselves.
    /// </summary>
    public bool IsSubmitting
    {
        get => _isSubmitting;
        private set
        {
            if (SetProperty(ref _isSubmitting, value))
            {
                OnPropertyChanged(nameof(CanInteract));
            }
        }
    }

    public bool CanInteract => !IsSubmitting && !IsSavingFinalAnswer;

    /// <summary>
    /// True while the student must stay in the exam window: the window greys out its close button
    /// and refuses every user-initiated close while this is set. Cleared only by
    /// <see cref="HandleExamEnded"/> or <see cref="UnlockWindowForFailure"/> -- deliberately NOT by
    /// IsSubmitting/IsSavingFinalAnswer, because the final answer is still being persisted then and
    /// a turn that lands after the attempt is closed out never gets graded.
    /// </summary>
    public bool IsExamLocked
    {
        get => _isExamLocked;
        private set => SetProperty(ref _isExamLocked, value);
    }

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

    /// <summary>
    /// True from construction until the attempt is ready to be interacted with, covering the exam UI
    /// while proctoring, recording and the realtime session come up (see ExamAttemptRunner.RunAsync).
    /// Without it the student is shown a complete-looking exam surface -- question area, mic button --
    /// several seconds before any of it does anything.
    ///
    /// Cleared on all three ways out of that wait, not just the happy one: <see cref="HandleSessionReady"/>
    /// when the session comes up, <see cref="UnlockWindowForFailure"/> when the watchdog gives up, and
    /// defensively in <see cref="HandleExamEnded"/>. Leaving it set would bury the very message that
    /// tells the student what went wrong, since the error overlay draws above this one but the dimmed
    /// backdrop here would still be covering the window.
    ///
    /// Note this deliberately does NOT gate the exam clock -- HandleSessionReady already owns that.
    /// This is presentation only.
    /// </summary>
    public bool ShowConnectingOverlay
    {
        get => _showConnectingOverlay;
        private set => SetProperty(ref _showConnectingOverlay, value);
    }

    public string EndScreenMessage
    {
        get => _endScreenMessage;
        set => SetProperty(ref _endScreenMessage, value);
    }

    /// <summary>
    /// Whether ExamWindow shows the behaviour-log panel. Read once from settings and never raised:
    /// it is a build/deployment switch, not runtime state.
    ///
    /// <see cref="AddLog"/> keeps running either way. The entries are cheap, some are read back by
    /// the end-screen, and everything worth diagnosing is written to LocalFileLogger regardless --
    /// this gates the student-visible surface only.
    /// </summary>
    public bool ShowDebugLogPanel => _settings.ShowDebugLogPanel;

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
                OnPropertyChanged(nameof(AssetMediaCaption));
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

    /// <summary>
    /// Dòng mô tả cho khung audio/video. Cần thiết vì <c>MediaElement</c> với nguồn AUDIO
    /// <b>không vẽ gì cả</b> -- không có dòng này thì thí sinh chỉ thấy một hộp xám rỗng nằm suốt
    /// câu hỏi. Và vì audio chỉ được phát MỘT lần, họ cần biết clip dài bao nhiêu TRƯỚC khi nó chạy.
    /// </summary>
    public string AssetMediaCaption
    {
        get
        {
            var asset = CurrentQuestionAsset;
            if (asset is null)
            {
                return string.Empty;
            }

            var label = asset.Type switch
            {
                QuestionAssetType.Audio => "Đoạn nghe",
                QuestionAssetType.Video => "Đoạn phim",
                _ => string.Empty
            };
            if (label.Length == 0)
            {
                return string.Empty;
            }

            var title = asset.Title?.Trim();
            if (!string.IsNullOrEmpty(title))
            {
                label = $"{label} · {title}";
            }
            if (asset.DurationSeconds is > 0 and var seconds)
            {
                label = $"{label} · {TimeSpan.FromSeconds(seconds):m\\:ss}";
            }
            return label;
        }
    }

    /// <summary>Trạng thái phát. Luật là phát đúng một lần nên không có nút bấm nào ở đây.</summary>
    public string AssetMediaStatusText => IsAssetMediaPlaying
        ? "Đang phát — chỉ phát một lần"
        : HasAssetMediaFinished ? "Đã phát xong" : "Sắp phát";

    public bool IsAssetMediaPlaying
    {
        get => _isAssetMediaPlaying;
        private set
        {
            if (SetProperty(ref _isAssetMediaPlaying, value))
            {
                OnPropertyChanged(nameof(AssetMediaStatusText));
            }
        }
    }

    public bool HasAssetMediaFinished
    {
        get => _hasAssetMediaFinished;
        private set
        {
            if (SetProperty(ref _hasAssetMediaFinished, value))
            {
                OnPropertyChanged(nameof(AssetMediaStatusText));
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
            StartSessionReadyWatchdog();
            return;
        }

        try
        {
            var ticket = _sessionState.EntryTicket
                ?? throw new InvalidOperationException("Exam entry ticket is missing.");
            // Same call DevicePreflightViewModel gates on, on purpose: the set this exam records
            // has to be exactly the set the preflight proved working, or clearing the preflight
            // means nothing.
            RecordingStreamType[] streamTypes = [.. ticket.ResolveRecordingStreamTypes()];

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
        StartSessionReadyWatchdog();
    }

    /// <summary>
    /// Covers the gap between "the attempt was told to start" and <see cref="HandleSessionReady"/>.
    /// If the realtime session never becomes ready the countdown never runs, so nothing ever
    /// auto-submits and nothing ever raises OnExamEnded -- the window would stay locked forever.
    /// </summary>
    private void StartSessionReadyWatchdog()
    {
        // Guards against arming after the session already came up. Today StartAsync completes
        // synchronously so this cannot happen, but that is RealtimeExamFlowService's implementation
        // detail; if it ever becomes genuinely async, arming late would unlock a perfectly healthy
        // exam two minutes in -- the worst failure this feature can have.
        if (_sessionReady)
        {
            return;
        }

        StartLockWatchdog(
            SessionReadyWatchdogSeconds,
            "Không kết nối được phiên thi. Bạn có thể đóng cửa sổ và liên hệ giám thị.");
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

    /// <summary>
    /// Releases the window lock on a path where the exam can never signal its own end -- a startup
    /// failure, or one of the watchdogs firing. Never called on a healthy attempt.
    /// </summary>
    public void UnlockWindowForFailure(string reason)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            StopLockWatchdog();
            IsSavingFinalAnswer = false;
            IsExamLocked = false;
            // Must come down here too: the failure this reports is usually "never connected", which
            // is exactly the state the connecting overlay is up for.
            ShowConnectingOverlay = false;
            ShowErrorOverlay = true;
            EndScreenMessage = reason;
            AddLog(reason, LogType.Error);
        });
    }

    /// <summary>
    /// Arms a one-shot failsafe that unlocks the window if the exam has not moved on by the deadline.
    /// Not a feature: without it, a flow that dies without raising OnExamEnded would seal the student
    /// into a window whose close button does nothing at all.
    /// </summary>
    private void StartLockWatchdog(int seconds, string reason)
    {
        StopLockWatchdog();

        if (!IsExamLocked)
        {
            return;
        }

        _lockWatchdogTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(seconds)
        };
        _lockWatchdogTimer.Tick += (_, _) =>
        {
            StopLockWatchdog();
            if (!IsExamLocked)
            {
                return;
            }

            LocalFileLogger.Error(
                "exam_window",
                "lock_watchdog_unlocked_window",
                new InvalidOperationException(reason),
                new { seconds });
            UnlockWindowForFailure(reason);
        };
        _lockWatchdogTimer.Start();
    }

    private void StopLockWatchdog()
    {
        _lockWatchdogTimer?.Stop();
        _lockWatchdogTimer = null;
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
            StopLockWatchdog();
            // Trước khi dừng luồng thi và ghi hình: quá trình tắt tự nó làm khung hình ngừng lại,
            // và một cảnh báo "mất camera" sinh ra lúc bài thi đang kết thúc bình thường là cảnh
            // báo sai -- đúng loại nhiễu khiến người ta ngừng tin vào cả những cảnh báo thật.
            StopCameraSignalGuard();
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
            _examFlow.OnAvatarUtteranceChanged -= HandleAvatarUtteranceChanged;
            _examFlow.OnQuestionSpeakingTimeChanged -= HandleQuestionSpeakingTimeChanged;
            _examFlow.OnFinalSaveStateChanged -= HandleFinalSaveStateChanged;
            _assetPresentationCoordinator.OnAssetDisplayRequested -= HandleAssetDisplayRequested;
            _assetPresentationCoordinator.MediaPlaybackStateChanged -= HandleAssetMediaPlaybackChanged;
            _assetPresentationCoordinator.MediaStopRequested -= HandleAssetMediaStopRequested;
            _assetAudioPlayer.PlaybackEnded -= HandleAssetAudioEnded;
            _assetAudioPlayer.PlaybackFailed -= HandleAssetAudioFailed;
            _assetAudioPlayer.Dispose();
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

    private void StopCameraSignalGuard()
    {
        _cameraSignalGuard.Stop();
        _cameraSignalGuard.Interrupted -= HandleCameraSignalInterrupted;
        _cameraSignalGuard.Lost -= HandleCameraSignalLost;
        _cameraSignalGuard.Restored -= HandleCameraSignalRestored;
        CameraSignalMessage = string.Empty;
        IsCameraSignalLost = false;
    }

    private void LoadSessionData()
    {
        StudentName = _sessionState.CurrentUser?.DisplayName ?? _sessionState.CurrentUser?.Login ?? "Unknown user";
        StudentId = _sessionState.CurrentUser?.UserId ?? "N/A";
        ExamTitle = string.IsNullOrWhiteSpace(_sessionState.ExamTitle)
            ? "Kỳ thi vấn đáp"
            : _sessionState.ExamTitle;
        ExamDurationText = $"Thời lượng mã đề: {FormatDuration(_sessionState.DurationSeconds)}";
        CurrentQuestion = _sessionState.CurrentQuestion?.QuestionText ?? string.Empty;
        QuestionNumber = _sessionState.QuestionIndex + 1;
        TotalQuestions = _sessionState.Questions.Count;
        // Dựng màn hình lần đầu, luồng thi chưa chạy: chỉ hiện, KHÔNG phát. Lượt phát thật do
        // QuestionAssetPresentationCoordinator.PresentAsync kích hoạt khi câu hỏi bắt đầu.
        ApplyCurrentQuestionAsset(_sessionState.CurrentQuestion?.Asset, autoPlay: false);

        LogEntries.Add(new LogEntry { Time = DateTime.Now.AddMinutes(-2), Message = "Người dùng đã đăng nhập thành công", Type = LogType.Success });
        LogEntries.Add(new LogEntry { Time = DateTime.Now.AddMinutes(-1), Message = $"Thiết bị: {_sessionState.CurrentUser?.Device.DeviceName ?? "unknown"}", Type = LogType.Info });
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
        StartCameraSignalGuardIfRequired();
    }

    /// <summary>
    /// Canh tín hiệu camera, nhưng CHỈ khi kỳ thi thật sự yêu cầu camera.
    ///
    /// <para>Bài chỉ ghi màn hình vẫn mở camera cho AI giám sát, nhưng ở đó camera không phải bằng
    /// chứng bắt buộc -- báo động giám thị vì một thứ kỳ thi không đòi hỏi là đúng nghĩa nhiễu.</para>
    ///
    /// <para>Bật ngay tại đây dù thiết bị còn chưa mở (ExamAttemptRunner mở nó muộn hơn): guard tự
    /// nằm im khi camera chưa chạy, và đo từ mốc mở thiết bị chứ không phải mốc này -- nhờ vậy nó
    /// bắt được cả ca camera mở lên rồi KHÔNG BAO GIỜ gửi nổi khung nào, thứ mà cổng preflight
    /// không thể thấy vì lúc đó thiết bị còn tốt.</para>
    /// </summary>
    private void StartCameraSignalGuardIfRequired()
    {
        IReadOnlyList<RecordingStreamType> required;
        try
        {
            required = _sessionState.EntryTicket?.ResolveRecordingStreamTypes() ?? [];
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("camera_signal", "required_streams_unreadable", ex);
            return;
        }

        if (!required.Contains(RecordingStreamType.Camera))
        {
            return;
        }

        _cameraSignalGuard.Interrupted += HandleCameraSignalInterrupted;
        _cameraSignalGuard.Lost += HandleCameraSignalLost;
        _cameraSignalGuard.Restored += HandleCameraSignalRestored;
        _cameraSignalGuard.Start();
    }

    private void HandleCameraSignalInterrupted(CameraSignalOutage outage) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsCameraSignalLost = false;
            CameraSignalMessage = "Mất tín hiệu camera — hãy kiểm tra dây cắm hoặc nắp che ống kính.";
        });

    private void HandleCameraSignalLost(CameraSignalOutage outage)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsCameraSignalLost = true;
            CameraSignalMessage = outage.NeverDelivered
                ? "Camera chưa gửi được hình ảnh nào từ đầu buổi thi. Giám thị đã được thông báo."
                : "Camera đã mất tín hiệu. Giám thị đã được thông báo. Hãy cắm lại camera ngay.";
        });

        _cameraOutageReported = true;
        // Không await: bài thi không được dừng lại chờ một cảnh báo, và runner đã tự nuốt lỗi.
        _ = _examFlow.ReportCameraSignalLostAsync(outage.StoppedAt, outage.NeverDelivered);
    }

    private void HandleCameraSignalRestored(CameraSignalOutage outage)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsCameraSignalLost = false;
            CameraSignalMessage = string.Empty;
        });

        if (!_cameraOutageReported)
        {
            return;
        }

        _cameraOutageReported = false;
        _ = _examFlow.ReportCameraSignalRestoredAsync(outage.StoppedAt, outage.Duration);
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

    // Bắn từ luồng nhận WebSocket (RealtimeSessionClient đọc frame `speak` ở đó), nên bắt buộc
    // qua Dispatcher -- gán thẳng property từ luồng đó sẽ ném ngay ở binding.
    private void HandleAvatarUtteranceChanged(string utterance)
    {
        Application.Current.Dispatcher.Invoke(() => LastAiUtterance = utterance?.Trim() ?? string.Empty);
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
        Application.Current.Dispatcher.Invoke(() =>
        {
            // The session came up, so the "could not connect" failsafe has done its job. The exam is
            // now expected to end through OnExamEnded, which arms its own watchdog on submit.
            _sessionReady = true;
            StopLockWatchdog();
            ShowConnectingOverlay = false;
            StartCountdown();
        });
    }

    private void StartCountdown()
    {
        _countdownTimer?.Stop();
        _remainingSecondsLocal = Math.Max(
            0,
            _sessionState.RemainingSeconds
                ?? (_sessionState.DurationSeconds > 0 ? _sessionState.DurationSeconds : 30 * 60));
        _lastCheckpointAt = DateTime.UtcNow;
        _lastForceEndPollAt = DateTime.UtcNow;
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
            // CỐ Ý vẫn trừ trong lúc phát audio/video: đồng hồ khởi tạo bằng
            // paper.timeDurationSeconds, mà cột đó ĐÃ cộng thời lượng media. Dừng ở đây là cấp thừa
            // đúng phần media và làm buổi thi tràn quá độ dài ca đã đặt theo cùng con số đó.
            //
            // Trường hợp bị ngắt giữa lúc đang phát được xử lý bằng cách HOÀN LẠI số giây đã xem
            // lúc vào lại, không phải bằng cách dừng đồng hồ -- xem RestoreQuestionStartRemaining.
            // Điều kiện thứ ba: MẤT KẾT NỐI thì ngừng trừ giây. Mất mạng nghĩa là AI không nghe
            // được cũng không hỏi được gì -- trừ giờ của thí sinh trong khoảng đó là trừ oan, mà
            // trước đây đồng hồ vẫn chạy đều vì nó chỉ nhìn IsAvatarSpeaking.
            //
            // NGỪNG TRỪ chứ không hoàn lại sau: checkpoint phía server chỉ cho phép giảm
            // (UpdateExamSessionRemainingTimeUseCase), nên cộng ngược lên sẽ bị từ chối. Không tiêu
            // thì không phải hoàn.
            //
            // Đây KHÔNG phải đường để câu giờ: ca thi vẫn đóng đúng hạn và
            // ExamScheduleTimeoutGradingJob ép nộp ở biên ca, nên thời gian đóng băng nhiều nhất
            // cũng chỉ tới đó.
            // Đọc MỘT LẦN rồi dùng cho cả hai việc: quyết định có trừ giây không, và hiện chỉ báo.
            // Hỏi hai lần thì có lúc đồng hồ đứng mà chỉ báo chưa hiện, người xem không hiểu vì sao.
            var realtimeAlive = _examFlow.IsRealtimeAlive;
            IsRealtimeDisconnected = !realtimeAlive;

            if (_remainingSecondsLocal > 0 && !IsAvatarSpeaking && realtimeAlive)
            {
                _remainingSecondsLocal--;
            }
            // Giữ state dùng chung khớp đồng hồ đang chạy: ExamAttemptRunner đọc đúng chỗ này để
            // gửi mốc kèm question_start (CurrentRemainingSecondsProvider). Chỉ gán trong bộ nhớ,
            // không phải lời gọi mạng -- checkpoint lên server vẫn là nhánh 10 giây bên dưới.
            _sessionState.RemainingSeconds = _remainingSecondsLocal;
            RefreshRemainingTime();

            if (DateTime.UtcNow - _lastCheckpointAt >= TimeSpan.FromSeconds(10))
            {
                _lastCheckpointAt = DateTime.UtcNow;
                _ = CheckpointRemainingTimeBestEffortAsync();
            }

            // Lưới an toàn cho lệnh buộc kết thúc: xem PollForceEndAsync.
            if (DateTime.UtcNow - _lastForceEndPollAt
                >= TimeSpan.FromSeconds(Math.Max(1, _settings.ForceEndPollIntervalSeconds)))
            {
                _lastForceEndPollAt = DateTime.UtcNow;
                _ = PollForceEndAsync();
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
            ? "Hết giờ!"
            : remaining.ToString(@"hh\:mm\:ss");
    }

    /// <summary>
    /// Hỏi server xem giám thị đã buộc kết thúc chưa, vì tin <c>force_end</c> qua WebSocket không
    /// đáng tin.
    ///
    /// <para>Lệnh cấm đi Kafka rồi mới tới pod Python đang giữ WebSocket của thí sinh. Consumer
    /// group giao partition cho MỘT pod, còn WebSocket nằm ở pod nào thì không ai biết trước — hai
    /// phép gán độc lập nhau. Một pod thì luôn trùng nên chạy đúng suốt; đo được 2026-08-17 khi hệ
    /// tự scale lên 2 pod: lệnh cấm rơi vào pod không giữ kết nối, chỉ ghi log "no local realtime
    /// connection" rồi bỏ qua, và thí sinh thi tiếp tới hết bài.
    ///
    /// <para>Vì sao không suy từ mốc thời gian còn lại: buộc kết thúc đặt phiên sang INTERRUPTED,
    /// mà trạng thái đó vẫn thuộc RESUMABLE nên endpoint checkpoint nhận bình thường, không hề báo
    /// lỗi gì để mà bắt.</para>
    ///
    /// <para>Chỉ dừng khi server nói RÕ <c>candidateBlocked</c>. Không hỏi được thì thi tiếp: dừng
    /// bài của thí sinh vì một cú nghẽn mạng còn tệ hơn hẳn lỗi đang vá.</para>
    /// </summary>
    private async Task PollForceEndAsync()
    {
        if (_forceEndPollInFlight || _forceEndHandled || _isCleaningUp
            || _sessionState.ExamAttemptId == Guid.Empty)
        {
            return;
        }

        _forceEndPollInFlight = true;
        try
        {
            var guard = await _examApi.GetSessionGuardAsync(
                _sessionState.ExamAttemptId,
                CancellationToken.None);
            if (guard is null || !guard.CandidateBlocked)
            {
                return;
            }

            // Đặt cờ TRƯỚC khi gọi xuống để nhịp sau không bắn lại, kể cả khi runner mất thời
            // gian dừng.
            _forceEndHandled = true;
            LocalFileLogger.Info("exam_timer", "force_end_detected_by_poll", new
            {
                sessionId = _sessionState.ExamAttemptId,
                status = guard.Status
            });
            AddLog("Bài thi đã bị giám thị buộc kết thúc.", LogType.Warning);
            _examFlow.ForceEndFromServer("Giám thị đã buộc kết thúc bài thi.");
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error(
                "exam_timer",
                "force_end_poll_failed",
                ex,
                new { sessionId = _sessionState.ExamAttemptId });
        }
        finally
        {
            _forceEndPollInFlight = false;
        }
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

        IsSubmitting = true;
        _countdownTimer?.Stop();
        // Re-arms the failsafe for the finishing phase: if CompleteAttemptAsync itself throws, the
        // run task faults unobserved and OnExamEnded never fires.
        StartLockWatchdog(
            ExamEndWatchdogSeconds,
            "Bài thi không kết thúc đúng cách. Bạn có thể đóng cửa sổ và liên hệ giám thị.");
        _ = _examFlow.SubmitNowAsync();
    }

    private void HandleFinalSaveStateChanged(bool saving) =>
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsSavingFinalAnswer = saving;
            if (saving)
            {
                AiStatus = "Đang lưu câu trả lời cuối...";
                AddLog(
                    "Đang lưu câu trả lời cuối cùng, vui lòng không tắt máy",
                    LogType.Warning);
            }
        });


    /// <summary>
    /// Cửa sổ thi vừa mất focus (WindowFocusGuard). Fire-and-forget có chủ ý: đây là đường báo
    /// cáo phụ, tuyệt đối không được chặn luồng UI hay làm gián đoạn bài thi. Mọi lỗi đã được
    /// nuốt ở ExamAttemptRunner và ghi lại trong log máy trạm.
    /// </summary>
    public void ReportFocusLost(DateTimeOffset capturedAt)
    {
        _ = _examFlow.ReportFocusLostAsync(capturedAt);
    }

    private void HandleQuestionPresented(ExamQuestionPrompt prompt)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            CurrentQuestion = prompt.QuestionText;
            // Câu mới thì lời AI của câu trước không còn nghĩa gì. Để nguyên là thí sinh đọc
            // follow-up của câu cũ ngay dưới đề bài câu mới.
            LastAiUtterance = string.Empty;
            QuestionNumber = prompt.QuestionNumber;
            TotalQuestions = prompt.TotalQuestions;
            ResponseWindowText = FormatResponseWindow(prompt.MinResponseSeconds, prompt.MaxResponseSeconds);
            QuestionSpeakingTime = FormatQuestionSpeakingTime(TimeSpan.Zero, TimeSpan.FromSeconds(Math.Max(0, prompt.MaxResponseSeconds)));
            AddLog($"Đang hiện câu {prompt.QuestionNumber}: {prompt.QuestionText}", LogType.Info);
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

    /// <summary>
    /// Tài nguyên AUDIO do <see cref="QuestionAssetAudioPlayer"/> phát, không phải MediaElement.
    /// <c>ExamWindow</c> đọc cờ này để KHÔNG gọi <c>Play()</c> cho loại đó -- gọi cả hai là nghe
    /// chồng hai lần, mà một trong hai lại ra sai thiết bị.
    /// </summary>
    public bool PlaysAssetAudioInternally =>
        CurrentQuestionAsset?.Type == QuestionAssetType.Audio;

    private void HandleAssetAudioEnded() => NotifyQuestionAssetMediaEnded();

    private void HandleAssetAudioFailed(string reason) => NotifyQuestionAssetMediaFailed(reason);

    /// <summary>
    /// Media không phát được. Cho phát lại ĐÚNG MỘT lần rồi mới bỏ qua.
    ///
    /// <para>Luật của bài thi là audio/video chỉ phát một lần, nhưng luật đó nói về việc thí sinh
    /// KHÔNG được nghe lại thứ họ ĐÃ nghe. File hỏng thì họ chưa nghe gì cả -- bỏ qua luôn nghĩa là
    /// bắt họ trả lời về đoạn ghi âm chưa từng được phát. Đây là đường lỗi, không phải nới luật.</para>
    ///
    /// <para>Trước bản này hàm chỉ ghi một dòng log rồi gọi thẳng CompleteMediaPlayback, tức luồng
    /// thi đi tiếp y như đã phát thành công, và người chấm không có cách nào biết.</para>
    /// </summary>
    public void NotifyQuestionAssetMediaFailed(string? reason = null)
    {
        var retrying = false;
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!_assetMediaRetryUsed && CurrentQuestionMediaSource is not null)
            {
                _assetMediaRetryUsed = true;
                retrying = true;
                AddLog($"Không phát được tài nguyên câu hỏi, đang thử lại: {reason}", LogType.Warning);
                return;
            }

            AddLog(
                $"Không phát được tài nguyên câu hỏi sau khi thử lại: {reason}. Hãy báo giám thị.",
                LogType.Warning);
        });

        LocalFileLogger.Error(
            "exam_flow",
            retrying ? "asset_media_failed_retrying" : "asset_media_failed_giving_up",
            new Exception(reason ?? "unknown"),
            new { questionNumber = QuestionNumber, assetType = CurrentQuestionAsset?.Type.ToString() });

        if (retrying)
        {
            // AUDIO không đi qua MediaElement nên MediaRetryRequested (Close/Play trên element đó)
            // sẽ không phát gì cả -- phát lại bằng chính bộ phát đã dùng lần đầu.
            if (PlaysAssetAudioInternally && CurrentQuestionMediaSource is not null)
            {
                _assetAudioPlayer.Play(
                    CurrentQuestionMediaSource,
                    _sessionState.SelectedAudioOutputDeviceIndex);
                return;
            }

            MediaRetryRequested?.Invoke();
            return;
        }

        // Bằng chứng cho người chấm: đi nhờ WebSocket sẵn có để Python ghi vào log pod, đúng mẫu
        // ReportFocusLostAsync. CỐ Ý không đẩy vào kênh cảnh báo giám thị -- kênh đó dành cho hành
        // vi của thí sinh, nhét lỗi kỹ thuật vào là làm loãng đúng thứ cần được tin.
        _ = _examFlow.ReportAssetPlaybackFailedAsync(reason ?? "unknown", QuestionNumber);
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
            // Defensive: the end-screen overlay must always win over the saving one, even if the
            // attempt runner somehow ended without clearing the saving state itself.
            IsSavingFinalAnswer = false;
            // Same defensiveness: an attempt that ended without ever raising SessionReady (it failed
            // during startup) would otherwise leave this covering the end screen.
            ShowConnectingOverlay = false;
            // Unlocked here, before the auto-close delay below starts, so CloseWindowAfterDelayAsync's
            // Window.Close() passes ExamWindow's Closing guard instead of being cancelled by it.
            StopLockWatchdog();
            IsExamLocked = false;
            ShowSubmittedOverlay = succeeded;
            ShowErrorOverlay = !succeeded;
            if (succeeded)
            {
                AiStatus = "Đã hoàn thành bài thi";
                EndScreenMessage = "Bài thi đã được nộp thành công. Hệ thống sẽ đóng sau ít giây nữa.";
                AddLog("Bài thi vấn đáp đã hoàn thành", LogType.Success);
                // Chỉ dọn đệm tài nguyên khi bài thi ĐÃ NỘP THÀNH CÔNG. Nhánh thất bại giữ nguyên
                // đệm là có chủ đích: thi hỏng thường kéo theo một lần vào lại, mà vào lại thì đệm
                // chính là thứ giúp không phải tải lại từ đầu -- đúng lúc mạng đang tệ nhất.
                _assetCache.Clear();
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

    private void HandleAssetDisplayRequested(QuestionAsset? asset, bool autoPlay)
    {
        Application.Current.Dispatcher.Invoke(() => ApplyCurrentQuestionAsset(asset, autoPlay));
    }

    private void HandleAssetMediaPlaybackChanged(bool isPlaying)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsAssetMediaPlaying = isPlaying;
            if (isPlaying)
            {
                HasAssetMediaFinished = false;
            }
            else if (CurrentQuestionMediaSource is not null)
            {
                HasAssetMediaFinished = true;
            }
        });
    }

    private void HandleAssetMediaStopRequested()
    {
        _assetAudioPlayer.Stop();
        Application.Current.Dispatcher.Invoke(() => MediaStopRequested?.Invoke());
    }

    /// <summary>
    /// Cờ đọc bởi <c>ExamWindow.QuestionAssetMedia_TargetUpdated</c>: đổi nguồn media KHÔNG đồng
    /// nghĩa với được phát. Vào lại giữa câu sau khi đã trả lời thì chỉ hiện lại khung của thứ đã
    /// nghe xong, xem <c>QuestionAssetPresentationCoordinator.ShowWithoutWaiting</c>.
    /// </summary>
    /// <summary>
    /// Đang mất kết nối tới server hay không, lấy đúng tín hiệu đã dùng để đóng băng đồng hồ.
    ///
    /// <para>Cần một chỉ báo RIÊNG chứ không dùng lại <c>AiStatus</c> vì hai lý do. Một:
    /// <c>OnReconnecting</c> chỉ nổ SAU khi loạt thử nhanh (~30 giây) đã cạn, nên một lần đứt 10
    /// giây không hiện gì cả -- thí sinh chỉ thấy đồng hồ tự dưng đứng, không hiểu vì sao. Hai:
    /// <c>AiStatus</c> là dòng trạng thái dùng chung, luồng thi vẫn ghi đè lên nó ("Đang chờ học
    /// sinh trả lời...") ngay cả trong lúc mất mạng.</para>
    /// </summary>
    public bool IsRealtimeDisconnected
    {
        get => _isRealtimeDisconnected;
        private set => SetProperty(ref _isRealtimeDisconnected, value);
    }

    public bool AutoPlayAssetMedia { get; private set; } = true;

    private void ApplyCurrentQuestionAsset(QuestionAsset? asset, bool autoPlay)
    {
        // Đặt TRƯỚC khi gán nguồn: gán nguồn là thứ kích hoạt TargetUpdated, mà handler đó đọc cờ này.
        AutoPlayAssetMedia = autoPlay;

        // Dừng bản ghi của câu trước trước khi dựng câu mới: nếu không, sang câu khác mà tiếng cũ
        // vẫn kêu chồng lên lời AI đọc đề.
        _assetAudioPlayer.Stop();

        CurrentQuestionAsset = asset;
        CurrentQuestionAssetImage = null;
        CurrentQuestionMediaSource = null;
        // Không được phát nghĩa là thứ này đã nghe xong từ lần vào trước -- nhãn phải nói đúng vậy,
        // chứ để "Sắp phát" thì thí sinh ngồi chờ một đoạn băng không bao giờ chạy.
        HasAssetMediaFinished = !autoPlay;
        _assetMediaRetryUsed = false;

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
            var source = ResolveAssetUri(asset.Url);

            if (asset.Type == QuestionAssetType.Image)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = source;
                image.EndInit();
                image.Freeze();
                CurrentQuestionAssetImage = image;
                return;
            }

            if (asset.Type == QuestionAssetType.Video || asset.Type == QuestionAssetType.Audio)
            {
                // Vẫn gán nguồn cho cả hai loại: HasMediaAsset và nhánh thử lại đọc thuộc tính này,
                // và MediaElement để LoadedBehavior="Manual" nên gán nguồn KHÔNG tự phát.
                CurrentQuestionMediaSource = source;

                // AUDIO đi qua NAudio để ra ĐÚNG thiết bị học sinh đã chọn -- MediaElement không
                // chọn được thiết bị, xem QuestionAssetAudioPlayer. VIDEO vẫn để MediaElement lo cả
                // hình lẫn tiếng, tránh phải tự đồng bộ hình-tiếng.
                if (asset.Type == QuestionAssetType.Audio && autoPlay)
                {
                    _assetAudioPlayer.Play(source, _sessionState.SelectedAudioOutputDeviceIndex);
                }
            }
        }
        catch (Exception ex)
        {
            AddLog($"Không thể tải asset câu hỏi: {ex.Message}", LogType.Warning);
        }
    }

    /// <summary>
    /// Ưu tiên tệp đã tải sẵn trên đĩa, không có thì mới lấy thẳng từ S3.
    ///
    /// <para>Tệp cục bộ tốt hơn ở hai điểm, không chỉ ở tốc độ: mất mạng giữa bài không còn làm
    /// tài nguyên biến mất, và <c>BitmapImage</c> nạp từ tệp thì giải mã xong ngay trong
    /// <c>EndInit()</c> -- <c>Freeze()</c> ngay sau đó luôn hợp lệ. Với URI từ xa, ảnh tải bất
    /// đồng bộ nên <c>Freeze()</c> có thể ném đúng vào khối catch bên trên và ảnh không bao giờ
    /// hiện ra.</para>
    ///
    /// <para>Vẫn giữ đường lấy thẳng từ S3 làm lưới đỡ: học sinh có thể đã bấm bỏ qua ở màn kiểm
    /// tra thiết bị khi tải trước thất bại.</para>
    /// </summary>
    private Uri ResolveAssetUri(string url)
    {
        var localPath = _assetCache.TryGetLocalPath(url);
        return localPath is not null
            ? new Uri(localPath, UriKind.Absolute)
            : new Uri(url, UriKind.Absolute);
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


