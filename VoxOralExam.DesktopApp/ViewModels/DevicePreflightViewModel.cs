using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoxOralExam.Core.Interfaces;
using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.Services.EntryFlow;
using VoxOralExam.DesktopApp.State;

using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.Infra.Recording;

namespace VoxOralExam.DesktopApp.ViewModels;

/// <summary>One row of the "luồng bắt buộc" list. Rebuilt wholesale on every re-check.</summary>
public sealed record StreamReadinessItem(string Title, string StatusText, bool IsReady);

/// <summary>
/// Một lựa chọn giám sát cho kỳ thi cho phép học viên tự chọn.
/// </summary>
/// <param name="Value">CAMERA / SCREEN / CAMERA_AND_SCREEN, gửi thẳng lên server làm streamType.</param>
/// <param name="Hint">
/// Nói theo HỆ QUẢ chứ không theo tên kỹ thuật -- giống bảng lựa chọn phía web dành cho giáo viên.
/// Học viên đang chọn mức bằng chứng sẽ tồn tại về buổi thi của chính mình, nên họ cần biết mình
/// đang từ bỏ điều gì.
/// </param>
public sealed record StreamChoiceOption(string Value, string Label, string Hint);


public class DevicePreflightViewModel : BaseViewModel
{
    private readonly IExamEntryNavigator _navigator;
    private readonly AppSettings _settings;
    private readonly ExamSessionState _sessionState;
    private readonly CaptureReadinessProbe _readinessProbe;
    private readonly IExamSessionBootstrapService _bootstrapService;
    private readonly IQuestionAssetCache _assetCache;

    private string _deviceTestStatus = "Chưa kiểm tra thiết bị";
    private bool _isMicTesting;
    private bool _isCameraTesting;
    private bool _isCheckingStreams;
    private double _microphoneLevel;
    private BitmapImage? _cameraPreview;
    private AudioInputOption? _selectedAudioInput;
    private AudioOutputOption? _selectedAudioOutput;
    private WaveIn? _micTestRecorder;
    private WaveOut? _outputTestPlayer;
    private CameraService? _cameraTestService;
    private CancellationTokenSource? _streamCheckCts;
    private StreamChoiceOption? _selectedStreamChoice;
    private bool _isEnteringExam;

    public DevicePreflightViewModel(
        IExamEntryNavigator navigator,
        AppSettings settings,
        ExamSessionState sessionState,
        CaptureReadinessProbe readinessProbe,
        IExamSessionBootstrapService bootstrapService,
        IQuestionAssetCache assetCache)
    {
        _navigator = navigator;
        _settings = settings;
        _sessionState = sessionState;
        _readinessProbe = readinessProbe;
        _bootstrapService = bootstrapService;
        _assetCache = assetCache;

        LoadStreamChoices();

        EnterExamCommand = new RelayCommand(() => _ = EnterExamAsync(), () => CanEnterExam);
        BackCommand = new RelayCommand(() => _navigator.Back());
        PlayTestSoundCommand = new RelayCommand(PlayTestSound);
        ToggleMicTestCommand = new RelayCommand(ToggleMicTest);
        // Blocked during a check: the probe holds the physical camera, and a second open of the
        // same device fails on Windows -- which would look to the student like their camera broke.
        ToggleCameraTestCommand = new RelayCommand(ToggleCameraTest, () => !IsCheckingStreams);
        RecheckStreamsCommand = new RelayCommand(() => _ = CheckRequiredStreamsAsync(), () => !IsCheckingStreams);

        LoadAudioInputDevices();
        LoadAudioOutputDevices();
        _ = CheckRequiredStreamsAsync();
    }

    public ObservableCollection<AudioInputOption> AudioInputDevices { get; } = [];
    public ObservableCollection<AudioOutputOption> AudioOutputDevices { get; } = [];

    /// <summary>
    /// One entry per stream type this exam requires, empty for an unmonitored exam.
    /// </summary>
    public ObservableCollection<StreamReadinessItem> StreamChecks { get; } = [];

    /// <summary>
    /// Lựa chọn giám sát, chỉ có nội dung khi kỳ thi cho học viên tự chọn VÀ phiên chưa chốt.
    /// </summary>
    public ObservableCollection<StreamChoiceOption> StreamChoices { get; } = [];

    public bool HasStreamChoices => StreamChoices.Count > 0;

    public StreamChoiceOption? SelectedStreamChoice
    {
        get => _selectedStreamChoice;
        set
        {
            if (SetProperty(ref _selectedStreamChoice, value))
            {
                // Đổi lựa chọn thì kết quả kiểm tra cũ nói về một bộ luồng khác -- giữ lại là để
                // học viên vào thi dựa trên bằng chứng của lựa chọn họ vừa bỏ.
                _ = CheckRequiredStreamsAsync();
            }
        }
    }

    public bool IsEnteringExam
    {
        get => _isEnteringExam;
        private set
        {
            if (SetProperty(ref _isEnteringExam, value))
            {
                OnPropertyChanged(nameof(CanEnterExam));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsCheckingStreams
    {
        get => _isCheckingStreams;
        private set
        {
            if (SetProperty(ref _isCheckingStreams, value))
            {
                OnPropertyChanged(nameof(CanEnterExam));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool HasStreamChecks => StreamChecks.Count > 0;

    /// <summary>
    /// The gate. Every required stream type must have proved it produces frames on this machine
    /// before the student is allowed in.
    ///
    /// <para>An exam with no monitoring configured has nothing to prove, so the list is empty and
    /// this is trivially true -- "all of nothing" is the right answer there, not a special case.</para>
    /// </summary>
    public bool CanEnterExam =>
        !IsCheckingStreams && !IsEnteringExam && StreamChecks.All(check => check.IsReady);

    public string DeviceTestStatus
    {
        get => _deviceTestStatus;
        set => SetProperty(ref _deviceTestStatus, value);
    }

    public bool IsMicTesting
    {
        get => _isMicTesting;
        set => SetProperty(ref _isMicTesting, value);
    }

    public bool IsCameraTesting
    {
        get => _isCameraTesting;
        set => SetProperty(ref _isCameraTesting, value);
    }

    public double MicrophoneLevel
    {
        get => _microphoneLevel;
        set => SetProperty(ref _microphoneLevel, value);
    }

    public BitmapImage? CameraPreview
    {
        get => _cameraPreview;
        set => SetProperty(ref _cameraPreview, value);
    }

    public AudioInputOption? SelectedAudioInput
    {
        get => _selectedAudioInput;
        set => SetProperty(ref _selectedAudioInput, value);
    }

    public AudioOutputOption? SelectedAudioOutput
    {
        get => _selectedAudioOutput;
        set => SetProperty(ref _selectedAudioOutput, value);
    }

    public ICommand EnterExamCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand PlayTestSoundCommand { get; }
    public ICommand ToggleMicTestCommand { get; }
    public ICommand ToggleCameraTestCommand { get; }
    public ICommand RecheckStreamsCommand { get; }

    public void CleanupDeviceTests()
    {
        _streamCheckCts?.Cancel();
        StopMicTest();
        StopCameraTest();
        _outputTestPlayer?.Dispose();
        _outputTestPlayer = null;
    }

    /// <summary>
    /// Probes every stream type the exam requires, one at a time, and republishes the verdicts.
    ///
    /// <para>Sequential rather than parallel: the camera probe and the screen probe both want the
    /// GPU and the preflight's own camera test wants the same device, so overlapping them turns a
    /// working machine into a failing one.</para>
    /// </summary>
    private async Task CheckRequiredStreamsAsync()
    {
        // Cancel the previous check but leave DISPOSING it to that invocation's own finally: it is
        // still awaiting on a token from that source, and CreateLinkedTokenSource on an already
        // disposed source throws instead of simply reporting cancellation.
        var cts = new CancellationTokenSource();
        _streamCheckCts?.Cancel();
        _streamCheckCts = cts;

        // Releases the physical camera before the probe asks for it.
        StopCameraTest();

        IsCheckingStreams = true;
        StreamChecks.Clear();
        OnPropertyChanged(nameof(HasStreamChecks));

        try
        {
            // ResolveRequestedStreamTypes chứ KHÔNG phải ResolveRecordingStreamTypes: cái sau đọc
            // ticket.StreamTypes, mà danh sách đó chỉ được điền bởi phản hồi token -- thứ giờ đây
            // chỉ tới sau khi bấm "Vào thi". Ở thời điểm này nó còn rỗng, và nhánh dự phòng "không
            // chắc thì coi như cả hai" của nó sẽ lặng lẽ đè lên lựa chọn của học viên.
            var required = _sessionState.EntryTicket
                ?.ResolveRequestedStreamTypes(SelectedStreamChoice?.Value) ?? [];

            foreach (var streamType in required)
            {
                var title = DescribeStreamType(streamType);
                DeviceTestStatus = $"Đang kiểm tra {title.ToLowerInvariant()}...";

                var readiness = await _readinessProbe.ProbeAsync(streamType, cts.Token);
                StreamChecks.Add(new StreamReadinessItem(title, readiness.Message, readiness.IsReady));
                OnPropertyChanged(nameof(HasStreamChecks));
                OnPropertyChanged(nameof(CanEnterExam));
                CommandManager.InvalidateRequerySuggested();

                LocalFileLogger.Info("device_test", "stream_readiness_checked", new
                {
                    streamType = streamType.ToString(),
                    readiness.IsReady,
                    readiness.Message
                });
            }

            // Reads the verdicts directly rather than CanEnterExam, which is still false here
            // because IsCheckingStreams only drops in the finally below.
            DeviceTestStatus = StreamChecks.Count == 0
                ? "Bài thi này không yêu cầu giám sát."
                : StreamChecks.All(check => check.IsReady)
                    ? "Thiết bị giám sát đã sẵn sàng."
                    : "Chưa thể vào thi: còn thiết bị giám sát bắt buộc chưa hoạt động.";
        }
        catch (OperationCanceledException)
        {
            // Left the screen, or a newer check superseded this one. Whoever cancelled owns the
            // state from here.
        }
        finally
        {
            // Only the newest check may clear the flag; an older one losing the race must not
            // re-enable the buttons underneath its successor. Clearing the field as well keeps
            // CleanupDeviceTests from cancelling a source this line is about to dispose.
            if (ReferenceEquals(_streamCheckCts, cts))
            {
                _streamCheckCts = null;
                IsCheckingStreams = false;
            }

            cts.Dispose();
        }
    }

    private static string DescribeStreamType(RecordingStreamType streamType) => streamType switch
    {
        RecordingStreamType.Camera => "Camera",
        RecordingStreamType.Screen => "Chia sẻ màn hình",
        _ => streamType.ToString()
    };

    /// <summary>
    /// Chốt lựa chọn giám sát, xin stream token, rồi mới bàn giao sang phòng thi.
    ///
    /// <para>Đây là nơi lựa chọn của học viên trở thành vĩnh viễn: server ghi nó xuống phiên thi ở
    /// lần phát token đầu tiên và từ chối mọi loại khác về sau. Vì vậy nó phải nằm SAU bước kiểm
    /// tra thiết bị -- chốt sớm hơn là quyết hộ học viên trước khi biết máy họ chạy được gì.</para>
    /// </summary>
    private async Task EnterExamAsync()
    {
        // The button is disabled in this state, but a command can still be invoked directly (a
        // keyboard accelerator, an automation peer), and this is the one gate standing between a
        // broken camera and an exam recorded with no evidence.
        if (!CanEnterExam)
        {
            return;
        }

        IsEnteringExam = true;
        try
        {
            DeviceTestStatus = "Đang xin quyền ghi hình cho phiên thi...";
            await _bootstrapService.IssueStreamAccessAsync(SelectedStreamChoice?.Value);
        }
        catch (Exception ex)
        {
            // Dừng hẳn tại đây. Đi tiếp nghĩa là thả học viên vào phòng thi với StreamJwt rỗng:
            // ghi hình không xác thực được, upload segment hỏng, và buổi thi kết thúc mà không có
            // một mảnh bằng chứng nào -- hỏng âm thầm hơn nhiều so với việc chặn ngay bây giờ.
            LocalFileLogger.Error("device_test", "stream_access_failed", ex, new
            {
                choice = SelectedStreamChoice?.Value
            });
            DeviceTestStatus = $"Không xin được quyền ghi hình: {ex.Message}";
            MessageBox.Show(
                $"Không thể bắt đầu phiên thi: {ex.Message}",
                "Không vào thi được",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        finally
        {
            IsEnteringExam = false;
        }

        // Persist the chosen mic so the exam's audio pipeline uses it (moved here from login).
        _sessionState.SelectedAudioInputDeviceIndex = SelectedAudioInput?.DeviceIndex ?? 0;
        _sessionState.SelectedAudioInputDeviceName = SelectedAudioInput?.DisplayName ?? string.Empty;
        // Persist the chosen speaker/headphone so AvatarWebRtcClient plays the avatar's speech there.
        _sessionState.SelectedAudioOutputDeviceIndex = SelectedAudioOutput?.DeviceIndex ?? 0;
        _sessionState.SelectedAudioOutputDeviceName = SelectedAudioOutput?.DisplayName ?? string.Empty;


        if (!await EnsureAssetsDownloadedAsync())
        {
            return;
        }

        CleanupDeviceTests();
        _navigator.RequestStartExam();
    }

    /// <summary>
    /// Cổng cuối trước phòng thi: tài nguyên phải nằm sẵn trên đĩa mới cho vào.
    ///
    /// <para>Phần lớn tệp đã tải xong nhờ lượt chạy nền khởi động từ lúc nhận đề, nên bình thường
    /// hàm này trả về gần như tức thì. Nó chỉ thật sự chờ khi mạng chậm -- và chờ ở đây là đúng
    /// chỗ: chờ trong phòng chờ thì không mất gì, còn tải giữa bài thì đồng hồ thi vẫn trừ và
    /// hỏng mạng lúc đó là học sinh nhận câu hỏi về tài nguyên không hiện ra.</para>
    ///
    /// <para>Tải hỏng hẳn thì KHÔNG chặn cứng, mà hỏi học sinh. Chặn cứng nghe có vẻ an toàn hơn
    /// nhưng thực ra tệ hơn: một URL hỏng vĩnh viễn (quên upload tệp, tệp bị xoá) sẽ khoá luôn cả
    /// bài thi, không ai vào được, mà đó lại là lỗi của người soạn đề chứ không phải của học sinh.
    /// Đi tiếp thì vẫn còn đường tải trực tiếp lúc tới câu đó.</para>
    /// </summary>
    /// <returns><c>true</c> nếu được phép vào thi.</returns>
    private async Task<bool> EnsureAssetsDownloadedAsync()
    {
        var assets = _sessionState.Questions
            .Select(question => question.Asset)
            .Where(asset => asset is not null)
            .Select(asset => asset!)
            .ToList();

        if (assets.Count == 0)
        {
            return true;
        }

        IsEnteringExam = true;
        try
        {
            DeviceTestStatus = "Đang tải tài nguyên câu hỏi...";
            var failed = await _assetCache.PrefetchAsync(
                assets,
                (done, total) => DeviceTestStatus = $"Đang tải tài nguyên câu hỏi... {done}/{total}");

            if (failed.Count == 0)
            {
                DeviceTestStatus = "Đã tải xong tài nguyên câu hỏi.";
                return true;
            }

            LocalFileLogger.Info("device_test", "asset_prefetch_incomplete", new
            {
                failedCount = failed.Count,
                total = assets.Count
            });
            DeviceTestStatus = $"Còn {failed.Count} tài nguyên chưa tải được.";

            var choice = MessageBox.Show(
                $"Không tải trước được {failed.Count}/{assets.Count} tài nguyên câu hỏi.\n\n"
                    + "Vào thi bây giờ thì những tài nguyên đó sẽ được tải khi tới câu hỏi, và có "
                    + "thể không hiện ra nếu mạng vẫn lỗi.\n\n"
                    + "Báo giám thị trước khi tiếp tục. Vẫn vào thi?",
                "Chưa tải đủ tài nguyên",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            return choice == MessageBoxResult.Yes;
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("device_test", "asset_prefetch_failed", ex);
            DeviceTestStatus = $"Không tải được tài nguyên câu hỏi: {ex.Message}";
            return MessageBox.Show(
                $"Không tải trước được tài nguyên câu hỏi: {ex.Message}\n\nVẫn vào thi?",
                "Chưa tải đủ tài nguyên",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }
        finally
        {
            IsEnteringExam = false;
        }
    }

    /// <summary>
    /// Dựng danh sách lựa chọn, hoặc để trống khi kỳ thi không cho chọn.
    ///
    /// <para>"Cả hai" đứng đầu và là mặc định: quyền tự chọn nghĩa là được phép giám sát ít hơn,
    /// không phải bị buộc chọn một -- nên mức bằng chứng cao nhất phải là thứ xảy ra khi học viên
    /// không đụng gì vào.</para>
    /// </summary>
    private void LoadStreamChoices()
    {
        StreamChoices.Clear();
        if (_sessionState.EntryTicket?.AllowsStreamTypeChoice != true)
        {
            OnPropertyChanged(nameof(HasStreamChoices));
            return;
        }

        StreamChoices.Add(new StreamChoiceOption(
            "CAMERA_AND_SCREEN",
            "Cả camera và màn hình",
            "Mức giám sát đầy đủ nhất."));
        StreamChoices.Add(new StreamChoiceOption(
            "CAMERA",
            "Chỉ camera",
            "Không có bằng chứng về những gì diễn ra trên màn hình của bạn."));
        StreamChoices.Add(new StreamChoiceOption(
            "SCREEN",
            "Chỉ màn hình",
            "Không có bằng chứng xác nhận ai đang ngồi trước máy."));

        _selectedStreamChoice = StreamChoices[0];
        OnPropertyChanged(nameof(SelectedStreamChoice));
        OnPropertyChanged(nameof(HasStreamChoices));
    }

    private void LoadAudioInputDevices()
    {
        AudioInputDevices.Clear();
        foreach (var (deviceIndex, productName) in TurnAudioRecorder.ListInputDevices())
        {
            AudioInputDevices.Add(new AudioInputOption
            {
                DeviceIndex = deviceIndex,
                DisplayName = $"{deviceIndex}. {productName}"
            });
        }

        SelectedAudioInput = AudioInputDevices.FirstOrDefault(option => option.DeviceIndex == _sessionState.SelectedAudioInputDeviceIndex)
            ?? AudioInputDevices.FirstOrDefault();

        DeviceTestStatus = AudioInputDevices.Count == 0
            ? "Không tìm thấy thiết bị microphone"
            : $"Sẵn sàng với mic: {SelectedAudioInput?.DisplayName}";
        LocalFileLogger.Info("device_test", "audio_input_devices_loaded", new
        {
            count = AudioInputDevices.Count,
            selected = SelectedAudioInput?.DisplayName
        });
    }

    private void LoadAudioOutputDevices()
    {
        AudioOutputDevices.Clear();
        foreach (var (deviceIndex, productName) in AvatarWebRtcClient.ListOutputDevices())
        {
            AudioOutputDevices.Add(new AudioOutputOption
            {
                DeviceIndex = deviceIndex,
                DisplayName = $"{deviceIndex}. {productName}"
            });
        }

        SelectedAudioOutput = AudioOutputDevices.FirstOrDefault(option => option.DeviceIndex == _sessionState.SelectedAudioOutputDeviceIndex)
            ?? AudioOutputDevices.FirstOrDefault();

        LocalFileLogger.Info("device_test", "audio_output_devices_loaded", new
        {
            count = AudioOutputDevices.Count,
            selected = SelectedAudioOutput?.DisplayName
        });
    }

    private void PlayTestSound()
    {
        if (SelectedAudioOutput is null)
        {
            DeviceTestStatus = "Hãy kiểm tra thiết bị loa/tai nghe trước khi test";
            LocalFileLogger.Info("device_test", "play_test_sound_skipped_no_device");
            return;
        }

        try
        {
            _outputTestPlayer?.Dispose();
            var tone = new SignalGenerator(16_000, 1) { Type = SignalGeneratorType.Sin, Frequency = 440, Gain = 0.3 };
            _outputTestPlayer = new WaveOut { DeviceNumber = SelectedAudioOutput.DeviceIndex };
            _outputTestPlayer.Init(tone.ToWaveProvider());
            _outputTestPlayer.Play();
            DeviceTestStatus = $"Âm thanh test ra: {SelectedAudioOutput.DisplayName}";
            LocalFileLogger.Info("device_test", "play_test_sound", new
            {
                SelectedAudioOutput.DeviceIndex,
                SelectedAudioOutput.DisplayName
            });

            // Short test tone, not an endless one -- stop it after 600ms instead of relying on the
            // caller to press a second button (there's no natural "level meter" equivalent for
            // output, so a one-shot beep-and-stop is the closest mirror of the mic test).
            var player = _outputTestPlayer;
            _ = Task.Delay(600).ContinueWith(_ =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (!ReferenceEquals(_outputTestPlayer, player))
                    {
                        return;
                    }

                    _outputTestPlayer?.Stop();
                    _outputTestPlayer?.Dispose();
                    _outputTestPlayer = null;
                });
            });
        }
        catch (Exception ex)
        {
            DeviceTestStatus = $"Lỗi khi test âm thanh: {ex.Message}";
            LocalFileLogger.Error("device_test", "play_test_sound_failed", ex);
        }
    }

    private void ToggleMicTest()
    {
        if (IsMicTesting)
        {
            StopMicTest();
            return;
        }

        StartMicTest();
    }

    private void StartMicTest()
    {
        if (SelectedAudioInput is null)
        {
            DeviceTestStatus = "Hãy kết nối microphone trước khi test";
            LocalFileLogger.Info("device_test", "start_mic_test_skipped_no_device");
            return;
        }

        StopMicTest();

        _micTestRecorder = new WaveIn
        {
            DeviceNumber = SelectedAudioInput.DeviceIndex,
            WaveFormat = new WaveFormat(16_000, 16, 1),
            BufferMilliseconds = 50,
            NumberOfBuffers = 3
        };
        _micTestRecorder.DataAvailable += HandleMicTestDataAvailable;
        _micTestRecorder.RecordingStopped += HandleMicTestRecordingStopped;
        _micTestRecorder.StartRecording();
        IsMicTesting = true;
        DeviceTestStatus = $"Đang test mic: {SelectedAudioInput.DisplayName}";
        LocalFileLogger.Info("device_test", "mic_test_started", new
        {
            SelectedAudioInput.DeviceIndex,
            SelectedAudioInput.DisplayName
        });
    }

    private void StopMicTest()
    {
        var recorder = _micTestRecorder;
        _micTestRecorder = null;
        if (recorder is not null)
        {
            recorder.DataAvailable -= HandleMicTestDataAvailable;
            recorder.RecordingStopped -= HandleMicTestRecordingStopped;
            recorder.StopRecording();
            recorder.Dispose();
        }

        IsMicTesting = false;
        MicrophoneLevel = 0;
        LocalFileLogger.Info("device_test", "mic_test_stopped");
    }

    private void HandleMicTestDataAvailable(object? sender, WaveInEventArgs e)
    {
        double max = 0;
        for (var index = 0; index < e.BytesRecorded; index += 2)
        {
            if (index + 1 >= e.BytesRecorded)
            {
                break;
            }

            var sample = BitConverter.ToInt16(e.Buffer, index);
            var normalized = Math.Abs(sample) / (double)short.MaxValue;
            if (normalized > max)
            {
                max = normalized;
            }
        }

        Application.Current.Dispatcher.Invoke(() => MicrophoneLevel = Math.Min(100, max * 100));
    }

    private void HandleMicTestRecordingStopped(object? sender, StoppedEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsMicTesting = false;
            if (e.Exception is not null)
            {
                DeviceTestStatus = $"Lấy mic test: {e.Exception.Message}";
                LocalFileLogger.Error("device_test", "mic_test_failed", e.Exception);
            }
        });
    }

    private void ToggleCameraTest()
    {
        if (IsCameraTesting)
        {
            StopCameraTest();
            return;
        }

        StartCameraTest();
    }

    private async void StartCameraTest()
    {
        // The probe is holding the device right now; opening it a second time would fail and read
        // as a hardware fault to the student.
        if (IsCheckingStreams)
        {
            return;
        }

        try
        {
            StopCameraTest();
            var clock = new RecordingClock();
            _cameraTestService = new CameraService(_settings, clock);
            _cameraTestService.OnPreviewFrame += HandleCameraTestPreviewFrame;
            await _cameraTestService.StartAsync();
            IsCameraTesting = true;
            DeviceTestStatus = $"Đang test camera device {_settings.CameraDeviceIndex}";
            LocalFileLogger.Info("device_test", "camera_test_started", new
            {
                _settings.CameraDeviceIndex
            });
        }
        catch (Exception ex)
        {
            DeviceTestStatus = $"Lấy camera test: {ex.Message}";
            LocalFileLogger.Error("device_test", "camera_test_failed", ex, new
            {
                _settings.CameraDeviceIndex
            });
        }
    }

    private void StopCameraTest()
    {
        if (_cameraTestService is not null)
        {
            _cameraTestService.OnPreviewFrame -= HandleCameraTestPreviewFrame;
            _cameraTestService.Dispose();
            _cameraTestService = null;
        }

        IsCameraTesting = false;
        CameraPreview = null;
        LocalFileLogger.Info("device_test", "camera_test_stopped");
    }

    private void HandleCameraTestPreviewFrame(BitmapImage bitmapImage)
    {
        Application.Current.Dispatcher.Invoke(() => CameraPreview = bitmapImage);
    }
}


