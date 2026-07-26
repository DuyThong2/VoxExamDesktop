using System.Text.Json;
using VoxOralExam.Core.Interfaces;
using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services.ExamFlow;

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

    public async Task StartAsync()
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

        OnStatusChanged?.Invoke("Đang kết nối WebRTC...");
        var examAttemptId = _sessionState.ExamAttemptId != Guid.Empty
            ? _sessionState.ExamAttemptId.ToString("D")
            : _sessionState.SessionId;
        await _webRtc.ConnectAsync(examAttemptId);

        OnStatusChanged?.Invoke("Đang khởi động camera...");
        _camera.OnRawFrame += OnCameraRawFrame;
        await _camera.StartAsync();

        _isStarted = true;
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
