using System.Collections.ObjectModel;
using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using VoxOralExam.DesktopApp.Models;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.ViewModels;

/// <summary>
/// Stage: DevicePreflight (camera / microphone / speaker test), the last stage before InExam. The
/// device-test UI that used to live on the login screen now lives here, after OTP -- the standard
/// pre-flight pattern (docs/wpf-redesign-plan.md §A). "Vào thi" persists the chosen mic to the session,
/// releases the test devices, and hands off to the exam surface.
/// </summary>
public class DevicePreflightViewModel : BaseViewModel
{
    private readonly IExamEntryNavigator _navigator;
    private readonly AppSettings _settings;
    private readonly ExamSessionState _sessionState;

    private string _deviceTestStatus = "Chưa kiểm tra thiết bị";
    private bool _isMicTesting;
    private bool _isCameraTesting;
    private double _microphoneLevel;
    private BitmapImage? _cameraPreview;
    private AudioInputOption? _selectedAudioInput;
    private WaveInEvent? _micTestRecorder;
    private CameraService? _cameraTestService;

    public DevicePreflightViewModel(
        IExamEntryNavigator navigator,
        AppSettings settings,
        ExamSessionState sessionState)
    {
        _navigator = navigator;
        _settings = settings;
        _sessionState = sessionState;

        EnterExamCommand = new RelayCommand(EnterExam);
        BackCommand = new RelayCommand(() => _navigator.Back());
        PlayTestSoundCommand = new RelayCommand(PlayTestSound);
        ToggleMicTestCommand = new RelayCommand(ToggleMicTest);
        ToggleCameraTestCommand = new RelayCommand(ToggleCameraTest);

        LoadAudioInputDevices();
    }

    public ObservableCollection<AudioInputOption> AudioInputDevices { get; } = [];

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

    public ICommand EnterExamCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand PlayTestSoundCommand { get; }
    public ICommand ToggleMicTestCommand { get; }
    public ICommand ToggleCameraTestCommand { get; }

    /// <summary>Stop any running mic/camera test. Called on "Vào thi" and when the view is unloaded.</summary>
    public void CleanupDeviceTests()
    {
        StopMicTest();
        StopCameraTest();
    }

    private void EnterExam()
    {
        // Persist the chosen mic so the exam's audio pipeline uses it (moved here from login).
        _sessionState.SelectedAudioInputDeviceIndex = SelectedAudioInput?.DeviceIndex ?? 0;
        _sessionState.SelectedAudioInputDeviceName = SelectedAudioInput?.DisplayName ?? string.Empty;

        // Release the test devices BEFORE the exam opens so InExam can grab the camera/mic cleanly.
        // TODO(§E): open each device ONCE via a MediaCaptureHub and hand the WARM device to InExam so
        // ExamViewModel stops cold-starting the camera. For now InExam re-opens them, as before.
        // TODO(§A): only allow "Vào thi" once the camera + mic checks have actually passed.
        CleanupDeviceTests();
        _navigator.RequestStartExam();
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
            ? "Không tìm thấy microphone nào"
            : $"Sẵn sàng với mic: {SelectedAudioInput?.DisplayName}";
        LocalFileLogger.Info("device_test", "audio_input_devices_loaded", new
        {
            count = AudioInputDevices.Count,
            selected = SelectedAudioInput?.DisplayName
        });
    }

    private void PlayTestSound()
    {
        SystemSounds.Asterisk.Play();
        DeviceTestStatus = "Đã phát âm thanh test ra tai nghe/loa mặc định";
        LocalFileLogger.Info("device_test", "play_test_sound");
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
            DeviceTestStatus = "Hãy chọn microphone trước khi test";
            LocalFileLogger.Info("device_test", "start_mic_test_skipped_no_device");
            return;
        }

        StopMicTest();

        _micTestRecorder = new WaveInEvent
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
                DeviceTestStatus = $"Lỗi mic test: {e.Exception.Message}";
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
        try
        {
            StopCameraTest();
            _cameraTestService = new CameraService(_settings);
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
            DeviceTestStatus = $"Lỗi camera test: {ex.Message}";
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
