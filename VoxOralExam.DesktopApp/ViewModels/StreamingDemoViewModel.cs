using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using VoxOralExam.Core.Interfaces;
using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Infra.Clients.StreamService;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.ViewModels;

/// <summary>
/// Dev-only standalone screen (see AppSettings.LaunchStreamingDemo) that exercises camera/screen
/// capture and the client-side recording + segment-upload pipeline against a real, locally-running
/// vox-streaming instance, bypassing login/OTP/exam-paper entirely. The WPF analogue of
/// vox-streaming's demo/web/student.html: mint a token straight from devserver and start recording.
/// </summary>
public class StreamingDemoViewModel : BaseViewModel
{
    private readonly AppSettings _settings;
    private readonly DevStreamTokenClient _devStreamTokenClient;
    private readonly IExamRecordingService _recording;
    private readonly CameraService _camera;

    private string _scheduleId = Guid.NewGuid().ToString("D");
    private string _userId = "02db5954-120c-4ad2-a280-9f91c3bc03f3";
    private bool _includeCamera = true;
    private bool _includeScreen = true;
    private bool _isBusy;
    private string _statusText = "Chưa bắt đầu.";
    private BitmapImage? _cameraPreview;
    private bool _cameraStarted;

    public StreamingDemoViewModel(
        AppSettings settings,
        DevStreamTokenClient devStreamTokenClient,
        IExamRecordingService recording,
        CameraService camera)
    {
        _settings = settings;
        _devStreamTokenClient = devStreamTokenClient;
        _recording = recording;
        _camera = camera;
        _recording.StatusChanged += HandleRecordingStatusChanged;

        StartCommand = new RelayCommand(async () => await StartAsync(), () => !_isBusy && !_recording.IsRecording);
        StopCommand = new RelayCommand(async () => await StopAsync(), () => !_isBusy && _recording.IsRecording);

        AddLog($"devserver: {_settings.DevStreamTokenUrl}  |  vox-streaming: {_settings.StreamingBaseUrl}");
    }

    public string ScheduleId
    {
        get => _scheduleId;
        set => SetProperty(ref _scheduleId, value);
    }

    public string UserId
    {
        get => _userId;
        set => SetProperty(ref _userId, value);
    }

    public bool IncludeCamera
    {
        get => _includeCamera;
        set => SetProperty(ref _includeCamera, value);
    }

    public bool IncludeScreen
    {
        get => _includeScreen;
        set => SetProperty(ref _includeScreen, value);
    }

    public bool IsRecording => _recording.IsRecording;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public BitmapImage? CameraPreview
    {
        get => _cameraPreview;
        private set => SetProperty(ref _cameraPreview, value);
    }

    public ObservableCollection<string> LogEntries { get; } = [];

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }

    private async Task StartAsync()
    {
        if (_isBusy || _recording.IsRecording)
        {
            return;
        }

        _isBusy = true;
        RaiseCommandStates();
        try
        {
            var streamTypes = new List<RecordingStreamType>();
            if (IncludeCamera)
            {
                streamTypes.Add(RecordingStreamType.Camera);
            }
            if (IncludeScreen)
            {
                streamTypes.Add(RecordingStreamType.Screen);
            }
            if (streamTypes.Count == 0)
            {
                AddLog("Chọn ít nhất một luồng (camera hoặc screen) trước khi bắt đầu.");
                return;
            }

            var scheduleId = string.IsNullOrWhiteSpace(ScheduleId) ? Guid.NewGuid().ToString("D") : ScheduleId.Trim();
            var userId = string.IsNullOrWhiteSpace(UserId) ? "student-1" : UserId.Trim();
            // A fresh component per attempt, not just "{scheduleId}:{userId}", is deliberate: this
            // screen's ScheduleId/UserId fields default to fixed values that don't change between
            // repeated Start clicks in the same window session. vox-streaming's RegisterOrGetUpload
            // resumes an existing, not-yet-completed session for the same (schedule, session,
            // candidate, streamType) tuple -- returning the SAME streamId as a previous attempt.
            // But each Start here also creates a brand-new local AttemptId/manifest directory, which
            // knows nothing about that streamId's prior segments, so it restarts local numbering at
            // seq 0 -- colliding with whatever seq 0 the server already has from the earlier attempt
            // (different recording bytes) and getting rejected with "segment sequence already
            // contains different content". This screen is for repeated ad-hoc testing, not resume
            // testing, so always mint a new session identity instead.
            var sessionId = Guid.NewGuid().ToString();
            var wireStreamTypes = streamTypes
                .Select(t => t == RecordingStreamType.Camera ? "camera" : "screen")
                .ToArray();

            AddLog($"Đang xin token từ devserver cho schedule={scheduleId}...");
            var access = await _devStreamTokenClient.IssueAsync(
                scheduleId, sessionId, userId, wireStreamTypes, TimeSpan.FromHours(2), CancellationToken.None);
            AddLog("Đã nhận token, bắt đầu ghi hình cục bộ + upload segment...");

            var context = new RecordingSessionContext(
                Guid.NewGuid(), access.ScheduleId, access.SessionId, access.Token, streamTypes);
            await _recording.StartAsync(context, CancellationToken.None);

            try
            {
                if (IncludeCamera)
                {
                    _camera.OnPreviewFrame += HandlePreviewFrame;
                    _cameraStarted = true;
                    await _camera.StartAsync();
                }
            }
            catch
            {
                // Recording already started server-side (upload session created for every
                // requested stream type) by the time the camera device itself fails to open --
                // leaving it running would record zero camera frames forever, guaranteeing a
                // "no segments uploaded" assembly failure on vox-streaming minutes later with no
                // signal here. Roll the whole attempt back instead of limping along.
                StopCameraPreview();
                await _recording.StopAsync(RecordingStopReason.CaptureFailure, CancellationToken.None);
                throw;
            }

            StatusText = $"Đang ghi hình (schedule={access.ScheduleId}, session={access.SessionId}).";
        }
        catch (Exception ex)
        {
            AddLog($"Lỗi khi bắt đầu: {ex.Message}");
            StatusText = "Lỗi khi bắt đầu.";
            LocalFileLogger.Error("streaming_demo", "start_failed", ex);
        }
        finally
        {
            _isBusy = false;
            OnPropertyChanged(nameof(IsRecording));
            RaiseCommandStates();
        }
    }

    private async Task StopAsync()
    {
        if (_isBusy || !_recording.IsRecording)
        {
            return;
        }

        _isBusy = true;
        RaiseCommandStates();
        try
        {
            await _recording.StopAsync(RecordingStopReason.UserClosed, CancellationToken.None);
            StopCameraPreview();
            StatusText = "Đã dừng ghi hình.";
        }
        catch (Exception ex)
        {
            AddLog($"Lỗi khi dừng: {ex.Message}");
            LocalFileLogger.Error("streaming_demo", "stop_failed", ex);
        }
        finally
        {
            _isBusy = false;
            OnPropertyChanged(nameof(IsRecording));
            RaiseCommandStates();
        }
    }

    public async Task CleanupAsync()
    {
        _recording.StatusChanged -= HandleRecordingStatusChanged;
        if (_recording.IsRecording)
        {
            try
            {
                await _recording.StopAsync(RecordingStopReason.ApplicationShutdown, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LocalFileLogger.Error("streaming_demo", "cleanup_stop_failed", ex);
            }
        }

        // StreamingDemoWindow is always the only/last window in the demo flow -- safe to tear down
        // the shared upload worker here rather than leaving it for App.xaml.cs's OnExit fallback.
        try
        {
            await _recording.ShutdownAsync();
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("streaming_demo", "cleanup_shutdown_failed", ex);
        }

        StopCameraPreview();
    }

    private void StopCameraPreview()
    {
        if (!_cameraStarted)
        {
            return;
        }

        _camera.OnPreviewFrame -= HandlePreviewFrame;
        _camera.Stop();
        _cameraStarted = false;
        CameraPreview = null;
    }

    private void HandlePreviewFrame(BitmapImage bitmapImage)
    {
        Application.Current.Dispatcher.Invoke(() => CameraPreview = bitmapImage);
    }

    private void HandleRecordingStatusChanged(RecordingStatus status)
    {
        Application.Current.Dispatcher.Invoke(() => AddLog(status.Message));
    }

    private void AddLog(string message)
    {
        LogEntries.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
    }

    private static void RaiseCommandStates() => CommandManager.InvalidateRequerySuggested();
}
