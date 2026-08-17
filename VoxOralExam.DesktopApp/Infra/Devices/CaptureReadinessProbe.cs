using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Infra.Recording;
using VoxOralExam.DesktopApp.Infra.Recording.Capture;
using VoxOralExam.DesktopApp.Infra.Recording.Interop;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Infra.Devices;

/// <summary>
/// Verdict for one stream type. <see cref="Message"/> is written for the student, not for the log:
/// when it says not ready, this text is the only explanation they get for why the exam will not
/// start, so it has to name the device and suggest what to do about it.
/// </summary>
public sealed record CaptureReadiness(RecordingStreamType StreamType, bool IsReady, string Message);

/// <summary>
/// Answers "can this machine actually produce a <see cref="RecordingStreamType"/> right now?".
///
/// <para>The bar is a delivered FRAME, not an opened device, and that distinction is the entire
/// point of this class. A webcam that is disabled in Device Manager, held exclusively by another
/// application, or simply broken still reports <c>IsOpened() == true</c> often enough that opening
/// it proves nothing at all. One real frame is the cheapest check that a camera which would have
/// recorded nothing for the next hour cannot pass.</para>
///
/// <para>Every probe runs on its OWN capture objects rather than the DI singletons the exam
/// records through -- the same precedent DevicePreflightViewModel's camera test already set. A
/// probe has to be able to fail, be retried, and be torn down without leaving the exam's own
/// pipeline half-started, which is not true of the shared instances.</para>
/// </summary>
public sealed class CaptureReadinessProbe
{
    private readonly AppSettings _settings;

    public CaptureReadinessProbe(AppSettings settings)
    {
        _settings = settings;
    }

    public Task<CaptureReadiness> ProbeAsync(RecordingStreamType streamType, CancellationToken ct) =>
        streamType switch
        {
            RecordingStreamType.Camera => ProbeCameraAsync(ct),
            RecordingStreamType.Screen => ProbeScreenAsync(ct),
            _ => Task.FromResult(new CaptureReadiness(
                streamType,
                false,
                $"Ứng dụng không hỗ trợ ghi loại luồng này ({streamType})."))
        };

    private async Task<CaptureReadiness> ProbeCameraAsync(CancellationToken ct)
    {
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Its own CameraService, never the singleton: the singleton is what the exam will record
        // from, and opening then closing it here would leave the exam's camera in a state nobody
        // owns if the student never gets past this screen.
        var camera = new CameraService(_settings, new RecordingClock());

        void OnFrame(CameraFrame _) => firstFrame.TrySetResult();
        camera.OnCapturedFrame += OnFrame;

        try
        {
            // Off the UI thread on purpose. Constructing a VideoCapture enumerates DirectShow
            // devices, which on a machine with no camera (or a driver mid-recovery) blocks for
            // seconds before it decides to fail -- long enough to freeze the preflight window.
            await Task.Run(() => camera.StartAsync(), ct);

            if (await WaitForFirstFrameAsync(firstFrame.Task, ct))
            {
                return new CaptureReadiness(RecordingStreamType.Camera, true, "Camera đang hoạt động.");
            }

            return new CaptureReadiness(
                RecordingStreamType.Camera,
                false,
                "Camera mở được nhưng không gửi hình. Hãy kiểm tra nắp che ống kính, " +
                "hoặc đóng ứng dụng khác đang dùng camera (Zoom, Teams, Camera của Windows).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("capture_probe", "camera_probe_failed", ex, new
            {
                _settings.CameraDeviceIndex
            });
            return new CaptureReadiness(
                RecordingStreamType.Camera,
                false,
                $"Không mở được camera: {ex.Message}");
        }
        finally
        {
            camera.OnCapturedFrame -= OnFrame;
            camera.Dispose();
        }
    }

    private async Task<CaptureReadiness> ProbeScreenAsync(CancellationToken ct)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            // Not worth a capture attempt: this is a property of the Windows build, so no amount of
            // retrying or unplugging will change the answer.
            return new CaptureReadiness(
                RecordingStreamType.Screen,
                false,
                "Windows trên máy này không hỗ trợ ghi màn hình (cần Windows 10 phiên bản 1903 trở lên).");
        }

        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ID3D11Device? device = null;
        IDirect3DDevice? winRtDevice = null;
        ScreenCaptureSource? capture = null;

        // ScreenCaptureSource hands ownership of every emitted texture to its subscriber, so the
        // probe has to dispose what it is given even though it only cares that a frame existed.
        void OnFrame(ID3D11Texture2D texture, TimeSpan _)
        {
            texture.Dispose();
            firstFrame.TrySetResult();
        }

        void OnFailed(Exception ex) => firstFrame.TrySetException(ex);

        try
        {
            await Task.Run(
                () =>
                {
                    (device, winRtDevice) = Direct3D11Interop.CreateSharedDevice();
                    // Its own clock, started, so emitted timestamps advance normally instead of
                    // sitting at zero -- and its own context lock, because nothing else is drawing
                    // on this throwaway device.
                    var clock = new RecordingClock();
                    clock.Start();
                    capture = new ScreenCaptureSource(
                        device,
                        winRtDevice,
                        clock,
                        new object(),
                        Math.Clamp(_settings.ScreenRecordingFps, 1, 60));
                    capture.FrameArrived += OnFrame;
                    capture.CaptureFailed += OnFailed;
                    capture.Initialize();
                    capture.Start();
                },
                ct);

            if (await WaitForFirstFrameAsync(firstFrame.Task, ct))
            {
                return new CaptureReadiness(RecordingStreamType.Screen, true, "Ghi màn hình đang hoạt động.");
            }

            return new CaptureReadiness(
                RecordingStreamType.Screen,
                false,
                "Không nhận được hình ảnh màn hình. Hãy thử lại, hoặc đăng nhập trực tiếp trên máy " +
                "thay vì qua Remote Desktop.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("capture_probe", "screen_probe_failed", ex);
            return new CaptureReadiness(
                RecordingStreamType.Screen,
                false,
                $"Không khởi động được ghi màn hình: {ex.Message}");
        }
        finally
        {
            if (capture is not null)
            {
                capture.FrameArrived -= OnFrame;
                capture.CaptureFailed -= OnFailed;
                capture.Stop();
                capture.Dispose();
            }

            // After the capture that draws on them has stopped, same order as
            // LiveMonitorStreamService's teardown.
            (winRtDevice as IDisposable)?.Dispose();
            device?.Dispose();
        }
    }

    /// <returns>
    /// True if a frame arrived in time. False on timeout -- which is a verdict, not an error: the
    /// device is there and simply is not producing anything, and that is exactly the case this
    /// class exists to catch.
    /// </returns>
    private async Task<bool> WaitForFirstFrameAsync(Task firstFrame, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _settings.CaptureReadinessTimeoutSeconds));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var expiry = Task.Delay(timeout, timeoutCts.Token);

        if (!ReferenceEquals(await Task.WhenAny(firstFrame, expiry), firstFrame))
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }

        // Releases the timer immediately instead of leaving it pending for the rest of the timeout.
        timeoutCts.Cancel();
        // Rethrows a CaptureFailed reported through the TaskCompletionSource, so a capture that
        // broke while starting is reported with its real cause rather than as a bare timeout.
        await firstFrame;
        return true;
    }
}
