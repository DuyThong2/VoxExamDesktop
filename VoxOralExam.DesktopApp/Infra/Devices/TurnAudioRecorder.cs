using System.Diagnostics;
using NAudio.Wave;

using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Infra.Devices;

public sealed class TurnAudioRecorder : IDisposable
{
    private readonly object _syncLock = new();

    /// <summary>
    /// Guards the capture DEVICE — <c>_waveIn</c>, <c>_isStarted</c>, <c>_stopped</c> — across
    /// StartAsync, StopAsync and TryReopenAsync, which now run on different threads: the recovery
    /// poll lives on the thread pool while the runner's cleanup calls StopAsync from its own.
    ///
    /// <para>Deliberately NOT <c>_syncLock</c>, which protects the audio buffers and is taken by
    /// HandleDataAvailable on the capture thread every 50ms. Device work holds its lock across
    /// Dispose and StartRecording — tens of milliseconds — and putting that in the buffer lock's way
    /// would stall capture and risk deadlocking against NAudio's own callbacks.</para>
    ///
    /// <para>Lock ORDER, if the two are ever nested: device first, then buffers. StopAsync is the
    /// only place that does it, and nothing takes <c>_syncLock</c> before this.</para>
    /// </summary>
    private readonly object _deviceLock = new();
    private readonly Queue<byte> _preBuffer = new();
    private readonly List<byte> _turnBuffer = [];
    private readonly int _preBufferBytes;
    private readonly int _deviceNumber;
    private readonly Stopwatch _turnStopwatch = new();

    private WaveIn? _waveIn;
    private bool _isTurnActive;
    private bool _isStarted;
    private bool _isMuted;
    /// <summary>
    /// Set once StopAsync has run, so a recovery poll can never resurrect a recorder the exam has
    /// finished with. Cleared by StartAsync, which is what begins a new run.
    /// </summary>
    private bool _stopped;
    private double _lastTurnDurationSeconds;

    /// <summary>
    /// Fires for every captured chunk regardless of turn-active state (Phase 5 of
    /// docs/realtime-self-hosted-avatar-plan.md) -- continuous streaming to Azure Voice Live for
    /// live VAD/transcription. Independent of the pre-roll/turn-buffer capture above, which stays
    /// for archival upload (one whole-turn WAV per /turns/archive call). Resolves Open Question 8
    /// in the plan doc in favor of extending this class rather than opening a second NAudio
    /// capture device.
    /// </summary>
    public event Action<byte[]>? StreamChunkAvailable;

    public TurnAudioRecorder(int preRollMilliseconds = 400, int deviceNumber = 0)
    {
        _preBufferBytes = Math.Max(3200, (16_000 * 2 * Math.Max(100, preRollMilliseconds)) / 1000);
        _deviceNumber = Math.Max(0, deviceNumber);
    }

    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_deviceLock)
        {
            if (_isStarted)
            {
                return Task.CompletedTask;
            }

            _stopped = false;
            _waveIn = new WaveIn
            {
                DeviceNumber = _deviceNumber,
                WaveFormat = new WaveFormat(16_000, 16, 1),
                BufferMilliseconds = 50,
                NumberOfBuffers = 3
            };
            _waveIn.DataAvailable += HandleDataAvailable;
            _waveIn.RecordingStopped += HandleRecordingStopped;
            _waveIn.StartRecording();
            _isStarted = true;
            LocalFileLogger.Info("turn_audio", "recording_started", new
            {
                deviceNumber = _deviceNumber,
                preBufferBytes = _preBufferBytes
            });
            return Task.CompletedTask;
        }
    }

    public static string DescribeDefaultInputDevice()
    {
        if (WaveIn.DeviceCount <= 0)
        {
            return "No audio input device detected by NAudio.";
        }

        try
        {
            var caps = WaveIn.GetCapabilities(0);
            return $"NAudio input device 0: {caps.ProductName}";
        }
        catch (Exception ex)
        {
            return $"Unable to read NAudio input device capabilities: {ex.Message}";
        }
    }

    public static IReadOnlyList<(int DeviceIndex, string ProductName)> ListInputDevices()
    {
        var devices = new List<(int DeviceIndex, string ProductName)>();
        for (var index = 0; index < WaveIn.DeviceCount; index++)
        {
            var caps = WaveIn.GetCapabilities(index);
            devices.Add((index, caps.ProductName));
        }

        return devices;
    }

    public static string DescribeInputDevice(int deviceIndex)
    {
        if (WaveIn.DeviceCount <= 0)
        {
            return "No audio input device detected by NAudio.";
        }

        if (deviceIndex < 0 || deviceIndex >= WaveIn.DeviceCount)
        {
            return $"Requested audio input device {deviceIndex} is out of range.";
        }

        var caps = WaveIn.GetCapabilities(deviceIndex);
        return $"NAudio input device {deviceIndex}: {caps.ProductName}";
    }

    /// <summary>
    /// Re-opens the capture device after it died, WITHOUT touching the audio already captured.
    ///
    /// <para>Deliberately not StopAsync + StartAsync, which is the obvious composition and the wrong
    /// one: StopAsync clears <c>_preBuffer</c>, <c>_turnBuffer</c> and <c>_isTurnActive</c>, so
    /// recovering the microphone that way would throw away the very answer the student was in the
    /// middle of giving -- the part HandleRecordingStopped goes out of its way to preserve. Here the
    /// dead handle is replaced underneath the buffers and everything already captured survives, with
    /// a silent gap where the device was missing.</para>
    ///
    /// <para>Returns false rather than throwing while the device is still absent: the caller polls,
    /// and an unplugged headset is an expected state, not an error.</para>
    /// </summary>
    public Task<bool> TryReopenAsync(CancellationToken ct)
    {
        // Whole body under the lock, not just a re-check before the assignment.
        //
        // The check-then-act window here is wide -- disposing a dead handle and constructing a new
        // WaveIn takes tens of milliseconds -- and StopAsync running inside it produced exactly the
        // outcome the _stopped flag was added to prevent: StopAsync sets _stopped, finds _waveIn
        // already null, returns satisfied, and then this method finishes by starting a device that
        // nothing will ever stop. A microphone left capturing until process exit, indicator light
        // and all, pushing chunks at a socket that closed with the exam.
        //
        // Serialising the whole operation is simpler than reasoning about the partial states, and
        // makes both interleavings correct rather than one: StopAsync first means this returns false
        // on _stopped, and this first means StopAsync finds a live handle and tears it down properly.
        lock (_deviceLock)
        {
            if (_stopped || ct.IsCancellationRequested || _isStarted)
            {
                return Task.FromResult(_isStarted);
            }

            var dead = _waveIn;
            _waveIn = null;
            if (dead is not null)
            {
                dead.DataAvailable -= HandleDataAvailable;
                dead.RecordingStopped -= HandleRecordingStopped;
                try
                {
                    dead.Dispose();
                }
                catch (Exception ex)
                {
                    // A handle whose device vanished can fault on the way out; nothing here depends
                    // on its cooperation, and holding onto it would only leak.
                    LocalFileLogger.Error("turn_audio", "dead_capture_dispose_failed", ex);
                }
            }

            try
            {
                var reopened = new WaveIn
                {
                    DeviceNumber = _deviceNumber,
                    WaveFormat = new WaveFormat(16_000, 16, 1),
                    BufferMilliseconds = 50,
                    NumberOfBuffers = 3
                };
                reopened.DataAvailable += HandleDataAvailable;
                reopened.RecordingStopped += HandleRecordingStopped;
                reopened.StartRecording();
                _waveIn = reopened;
                _isStarted = true;
                return Task.FromResult(true);
            }
            catch (Exception)
            {
                // Expected while the device is still unplugged. Not logged per attempt -- the caller
                // polls every couple of seconds and would otherwise fill the log for the whole exam.
                return Task.FromResult(false);
            }
        }
    }

    public Task StopAsync()
    {
        lock (_deviceLock)
        {
            // Before the early return: a recorder whose device died has no handle left to stop, but
            // a recovery poll may still be waiting its turn on this very lock and has to be refused.
            _stopped = true;

            if (_waveIn is null)
            {
                return Task.CompletedTask;
            }

            var recorder = _waveIn;
            _waveIn = null;
            recorder.DataAvailable -= HandleDataAvailable;
            recorder.RecordingStopped -= HandleRecordingStopped;
            recorder.StopRecording();
            recorder.Dispose();
            LocalFileLogger.Info("turn_audio", "recording_stopped", new
            {
                deviceNumber = _deviceNumber
            });

            lock (_syncLock)
            {
                _preBuffer.Clear();
                _turnBuffer.Clear();
                _isTurnActive = false;
                _turnStopwatch.Reset();
                _lastTurnDurationSeconds = 0;
            }

            _isStarted = false;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// True between BeginTurnCapture and GetTurnBufferAndReset. Phase 5's turn-end detection
    /// (RealtimeExamFlowService) calls BeginTurnCapture on every VAD speech_start, including a
    /// resumption after a mid-turn pause -- checking this first is required there, since calling
    /// BeginTurnCapture a second time mid-turn would otherwise wipe whatever was already
    /// captured (it clears _turnBuffer and re-seeds from the pre-roll buffer).
    /// </summary>
    public bool IsTurnActive
    {
        get
        {
            lock (_syncLock)
            {
                return _isTurnActive;
            }
        }
    }

    public bool IsMuted
    {
        get
        {
            lock (_syncLock)
            {
                return _isMuted;
            }
        }
        set
        {
            lock (_syncLock)
            {
                _isMuted = value;
            }
        }
    }

    public double LastTurnDurationSeconds
    {
        get
        {
            lock (_syncLock)
            {
                return _lastTurnDurationSeconds;
            }
        }
    }

    public void BeginTurnCapture()
    {
        lock (_syncLock)
        {
            _turnBuffer.Clear();
            _turnBuffer.AddRange(_preBuffer);
            _isTurnActive = true;
            _turnStopwatch.Restart();
            LocalFileLogger.Info("turn_audio", "turn_capture_began", new
            {
                deviceNumber = _deviceNumber,
                preBufferBytesCopied = _turnBuffer.Count
            });
        }
    }

    /// <summary>
    /// Ảnh chụp KHÔNG phá huỷ phần PCM của lượt đang dở, từ <paramref name="offset"/> tới hết.
    /// Trả mảng rỗng khi chưa có gì mới hoặc không có lượt nào đang chạy.
    /// </summary>
    /// <remarks>
    /// Dùng để nạp lại bộ đệm audio phía server sau khi WebSocket đứt giữa lượt: bộ đệm đó nằm trong
    /// RAM của MỘT đối tượng AttemptConnection bên Python, mà mỗi lần nối lại nó dựng đối tượng mới
    /// với bộ đệm rỗng. Máy trạm là nơi duy nhất còn giữ đủ audio của lượt.
    ///
    /// <para>Có tham số offset để gọi được nhiều lần theo kiểu đuổi bắt: mic vẫn thu trong lúc đang
    /// gửi, nên phải hỏi lại phần vừa thu thêm cho tới khi hết -- xem
    /// <c>RealtimeSessionClient.ResyncTurnAudioAsync</c>.</para>
    /// </remarks>
    public byte[] PeekTurnBufferFrom(int offset)
    {
        lock (_syncLock)
        {
            if (!_isTurnActive || offset >= _turnBuffer.Count)
            {
                return [];
            }

            var start = Math.Max(0, offset);
            var buffer = new byte[_turnBuffer.Count - start];
            _turnBuffer.CopyTo(start, buffer, 0, buffer.Length);
            return buffer;
        }
    }

    public byte[] GetTurnBufferAndReset()
    {
        lock (_syncLock)
        {
            var buffer = _turnBuffer.ToArray();
            if (_turnStopwatch.IsRunning)
            {
                _turnStopwatch.Stop();
            }
            _lastTurnDurationSeconds = Math.Round(Math.Max(0, _turnStopwatch.Elapsed.TotalSeconds), 2);
            _turnStopwatch.Reset();
            _turnBuffer.Clear();
            _isTurnActive = false;
            LocalFileLogger.Info("turn_audio", "turn_capture_completed", new
            {
                deviceNumber = _deviceNumber,
                capturedBytes = buffer.Length,
                durationSeconds = _lastTurnDurationSeconds
            });
            return buffer;
        }
    }

    public void Dispose()
    {
        _ = StopAsync();
    }

    private void HandleDataAvailable(object? sender, WaveInEventArgs e)
    {
        byte[]? streamChunk = null;
        lock (_syncLock)
        {
            var effectiveBuffer = _isMuted ? new byte[e.BytesRecorded] : e.Buffer;
            for (var index = 0; index < e.BytesRecorded; index++)
            {
                _preBuffer.Enqueue(effectiveBuffer[index]);
            }

            while (_preBuffer.Count > _preBufferBytes)
            {
                _preBuffer.Dequeue();
            }

            if (_isTurnActive)
            {
                _turnBuffer.AddRange(effectiveBuffer.AsSpan(0, e.BytesRecorded).ToArray());
            }

            if (StreamChunkAvailable is not null)
            {
                streamChunk = effectiveBuffer.AsSpan(0, e.BytesRecorded).ToArray();
            }
        }

        // Raised outside the lock -- subscribers (MicAudioStreamer) may do async work
        // (WebSocket sends) that must never hold up the next NAudio callback.
        if (streamChunk is not null)
        {
            StreamChunkAvailable?.Invoke(streamChunk);
        }
    }

    /// <summary>
    /// The capture device stopped on its own -- an unplugged headset, a reconfigured audio stack, a
    /// driver that went away. The exam continues; this is how the rest of the app finds out the
    /// microphone is gone.
    /// </summary>
    public event Action<Exception>? CaptureFailed;

    /// <summary>
    /// Reports a capture device that died, and deliberately does NOT rethrow.
    ///
    /// <para>It used to be a bare <c>throw e.Exception</c>, and that one line killed the process.
    /// NAudio raises RecordingStopped through the WPF dispatcher, so the throw landed on the UI
    /// thread, where App's DispatcherUnhandledException handler logs but never sets
    /// <c>e.Handled</c> -- so the AppDomain terminated. Unplugging a headset at any point during an
    /// exam took the whole app down with it.</para>
    ///
    /// <para>Observed on 2026-09-02: the device disconnected 1.2 seconds into the 8-second settle
    /// delay that precedes the SUBMITTED PATCH, so a finished exam -- every answer archived, zero
    /// pending -- was left reading "Đang làm" because the process died before its last HTTP call.
    /// A missing microphone has to degrade the exam, never end it.</para>
    /// </summary>
    private void HandleRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
        {
            return;
        }

        // The device is gone; a later StartAsync or TryReopenAsync is allowed to open a new one.
        //
        // Written WITHOUT _deviceLock, deliberately. NAudio raises this event through the WPF
        // dispatcher, so taking a lock that StopAsync/TryReopenAsync hold across Dispose and
        // StartRecording would let a device teardown on one thread meet a dispatcher callback
        // waiting on it -- a deadlock traded for a race that costs nothing. Worst case here is a
        // late write landing after a successful reopen, which makes the recovery loop run one
        // redundant cycle: TryReopenAsync replaces the handle it finds rather than leaking it.
        _isStarted = false;

        LocalFileLogger.Error("turn_audio", "capture_device_stopped", e.Exception, new
        {
            deviceNumber = _deviceNumber,
            turnActive = IsTurnActive
        });

        // Whatever this turn captured before the device died stays in the buffer on purpose: a
        // truncated answer is worth more than no answer, and the archive path already handles short
        // audio. Clearing here would throw away the only copy.
        try
        {
            CaptureFailed?.Invoke(e.Exception);
        }
        catch (Exception handlerException)
        {
            // A subscriber throwing would land on exactly the fatal dispatcher path this method
            // exists to close, so it stops here too.
            LocalFileLogger.Error("turn_audio", "capture_failed_handler_threw", handlerException);
        }
    }
}
