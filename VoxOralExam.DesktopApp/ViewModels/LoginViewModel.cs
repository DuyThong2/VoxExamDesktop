using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using NAudio.Wave;
using System.Collections.ObjectModel;
using System.Media;
using System.Windows.Media.Imaging;
using VoxOralExam.DesktopApp.Models;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.ViewModels;

public class LoginViewModel : BaseViewModel
{
    private readonly IAuthApiService _authApiService;
    private readonly IDeviceContextProvider _deviceContextProvider;
    private readonly ExamSessionState _sessionState;
    private readonly IServiceProvider _serviceProvider;
    private readonly AppSettings _settings;

    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _errorMessage = string.Empty;
    private string _deviceTestStatus = "Chua kiem tra thiet bi";
    private bool _hasError;
    private bool _isLoggingIn;
    private bool _isMicTesting;
    private bool _isCameraTesting;
    private double _microphoneLevel;
    private BitmapImage? _cameraPreview;
    private AudioInputOption? _selectedAudioInput;
    private WaveInEvent? _micTestRecorder;
    private CameraService? _cameraTestService;

    public string Email
    {
        get => _email;
        set => SetProperty(ref _email, value);
    }

    public void CleanupDeviceTests()
    {
        StopMicTest();
        StopCameraTest();
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public bool HasError
    {
        get => _hasError;
        set => SetProperty(ref _hasError, value);
    }

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

    public ICommand LoginCommand { get; }
    public ICommand PlayTestSoundCommand { get; }
    public ICommand ToggleMicTestCommand { get; }
    public ICommand ToggleCameraTestCommand { get; }
    public ObservableCollection<AudioInputOption> AudioInputDevices { get; } = [];

    public LoginViewModel(
        IAuthApiService authApiService,
        IDeviceContextProvider deviceContextProvider,
        ExamSessionState sessionState,
        IServiceProvider serviceProvider,
        AppSettings settings)
    {
        _authApiService = authApiService;
        _deviceContextProvider = deviceContextProvider;
        _sessionState = sessionState;
        _serviceProvider = serviceProvider;
        _settings = settings;
        Email = "admin@vox.local";
        Password = "Admin123456";
        LoginCommand = new RelayCommand(ExecuteLogin, CanLogin);
        PlayTestSoundCommand = new RelayCommand(PlayTestSound);
        ToggleMicTestCommand = new RelayCommand(ToggleMicTest);
        ToggleCameraTestCommand = new RelayCommand(ToggleCameraTest);
        LoadAudioInputDevices();
    }

    private bool CanLogin()
    {
        return !string.IsNullOrWhiteSpace(Email)
            && !string.IsNullOrWhiteSpace(Password)
            && !_isLoggingIn;
    }

    private async void ExecuteLogin()
    {
        _isLoggingIn = true;
        HasError = false;
        ErrorMessage = string.Empty;
        LocalFileLogger.Info("login", "login_begin", new
        {
            email = Email.Trim(),
            selectedAudioInput = SelectedAudioInput?.DisplayName,
            selectedAudioInputDeviceIndex = SelectedAudioInput?.DeviceIndex
        });
        CommandManager.InvalidateRequerySuggested();

        try
        {
            var device = _deviceContextProvider.GetCurrentDevice();
            _sessionState.SelectedAudioInputDeviceIndex = SelectedAudioInput?.DeviceIndex ?? 0;
            _sessionState.SelectedAudioInputDeviceName = SelectedAudioInput?.DisplayName ?? string.Empty;
            var userContext = await _authApiService.LoginAsync(Email.Trim(), Password, device);

            _sessionState.SetAuthenticatedUser(userContext);
            LocalFileLogger.Info("login", "login_success", new
            {
                userContext.UserId,
                userContext.Email,
                userContext.DisplayName
            });

            var examWindow = _serviceProvider.GetRequiredService<Views.ExamWindow>();
            CleanupDeviceTests();
            examWindow.Show();

            Application.Current.Windows
                .OfType<Views.LoginView>()
                .FirstOrDefault()
                ?.Close();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Dang nhap that bai: {ex.Message}";
            HasError = true;
            LocalFileLogger.Error("login", "login_failed", ex, new
            {
                email = Email.Trim()
            });
        }
        finally
        {
            _isLoggingIn = false;
            CommandManager.InvalidateRequerySuggested();
        }
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
            ? "Khong tim thay microphone nao"
            : $"San sang voi mic: {SelectedAudioInput?.DisplayName}";
        LocalFileLogger.Info("device_test", "audio_input_devices_loaded", new
        {
            count = AudioInputDevices.Count,
            selected = SelectedAudioInput?.DisplayName
        });
    }

    private void PlayTestSound()
    {
        SystemSounds.Asterisk.Play();
        DeviceTestStatus = "Da phat test sound tai nghe/loa mac dinh";
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
            DeviceTestStatus = "Hay chon microphone truoc khi test";
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
        DeviceTestStatus = $"Dang test mic: {SelectedAudioInput.DisplayName}";
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
                DeviceTestStatus = $"Loi mic test: {e.Exception.Message}";
                HasError = true;
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
            DeviceTestStatus = $"Dang test camera device {_settings.CameraDeviceIndex}";
            LocalFileLogger.Info("device_test", "camera_test_started", new
            {
                _settings.CameraDeviceIndex
            });
        }
        catch (Exception ex)
        {
            DeviceTestStatus = $"Loi camera test: {ex.Message}";
            HasError = true;
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

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
}

public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke((T?)parameter) ?? true;
    public void Execute(object? parameter) => _execute((T?)parameter);
}
