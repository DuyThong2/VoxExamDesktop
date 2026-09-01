using System.Text.Json;
using VoxOralExam.Core.Interfaces;
using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services.Proctoring;

public class ScreenProctoringService : IProctoringService, IDisposable
{
    private readonly CameraService _camera;
    private readonly WebRtcClient _webRtc;
    private readonly ExamSessionState _sessionState;
    private bool _isStarted;
    private bool _isStopping;
    private bool _isDisposed;

    public event Action<string>? OnStatusChanged;
    public event Action<ProctoringEvent>? OnProctoringEvent;

    public ScreenProctoringService(CameraService camera, WebRtcClient webRtc, ExamSessionState sessionState)
    {
        _camera = camera;
        _webRtc = webRtc;
        _sessionState = sessionState;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(ScreenProctoringService));
        }

        if (_isStarted)
        {
            return;
        }

        _webRtc.OnProctoringEvent += HandleSseEvent;
        _webRtc.OnReconnecting += HandleReconnecting;
        _webRtc.OnReconnected += HandleReconnected;

        // Marked started HERE, not after the work below succeeds.
        //
        // StopAsync is gated on this flag, and from this line on there is already something that has
        // to be undone: three event subscriptions, and -- once ConnectAsync returns -- a client that
        // may be retrying in the background rather than connected. Setting it only on full success
        // meant a throw anywhere below left both of those running with nothing able to stop them,
        // and now that a failed first connect starts a retry loop instead of giving up, that leak
        // would outlive the exam.
        _isStarted = true;

        OnStatusChanged?.Invoke("Đang kết nối WebRTC...");
        var examAttemptId = _sessionState.ExamAttemptId != Guid.Empty
            ? _sessionState.ExamAttemptId.ToString("D")
            : _sessionState.SessionId;
        cancellationToken.ThrowIfCancellationRequested();
        await _webRtc.ConnectAsync(examAttemptId);

        OnStatusChanged?.Invoke("Đang khởi động camera...");
        _camera.OnRawFrame += OnCameraRawFrame;
        cancellationToken.ThrowIfCancellationRequested();
        await _camera.StartAsync();

        OnStatusChanged?.Invoke("Proctoring đang hoạt động");
    }

    public async Task StopAsync()
    {
        if (_isDisposed || !_isStarted || _isStopping)
        {
            return;
        }

        _isStopping = true;

        try
        {
            _webRtc.OnProctoringEvent -= HandleSseEvent;
            _webRtc.OnReconnecting -= HandleReconnecting;
            _webRtc.OnReconnected -= HandleReconnected;
            _camera.OnRawFrame -= OnCameraRawFrame;
            _camera.Stop();
            await _webRtc.DisconnectAsync();

            _isStarted = false;
            OnStatusChanged?.Invoke("Proctoring đã dừng");
        }
        finally
        {
            _isStopping = false;
        }
    }

    /// <summary>
    /// Reports the detector feed dropping and coming back.
    ///
    /// <para>Goes to OnStatusChanged, NOT to OnProctoringEvent: the latter is the violation feed,
    /// and putting an infrastructure hiccup in there would have it read as something the student
    /// did. This is the same separation ExamRecordingService already draws between a degraded
    /// recording and a proctoring event.</para>
    ///
    /// <para>Both lines are deliberately vague about what is being monitored. ExamWindow can show
    /// status text to the student, and telling them the exact second cheating detection went down
    /// -- and came back -- would hand them the one piece of information the whole subsystem exists
    /// to withhold. The precise account goes to desktopapp.jsonl, which is where a reviewer looks
    /// and a candidate does not. Same reasoning as AppSettings.ShowDebugLogPanel.</para>
    /// </summary>
    private void HandleReconnecting() =>
        OnStatusChanged?.Invoke("Đang kết nối lại giám sát...");

    private void HandleReconnected(int attempts) =>
        OnStatusChanged?.Invoke("Giám sát đang hoạt động");

    private void HandleSseEvent(string json)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<ProctoringEvent>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (evt is not null)
            {
                OnProctoringEvent?.Invoke(evt);
            }
        }
        catch
        {
        }
    }

    private void OnCameraRawFrame(byte[] bgrBytes, int width, int height)
    {
        _webRtc.PushRawFrame(bgrBytes, width, height);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _camera.OnRawFrame -= OnCameraRawFrame;
        _camera.Dispose();
        _webRtc.Dispose();
        _isDisposed = true;
    }
}
