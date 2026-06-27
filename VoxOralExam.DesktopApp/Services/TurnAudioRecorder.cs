using NAudio.Wave;

namespace VoxOralExam.DesktopApp.Services;

public sealed class TurnAudioRecorder : IDisposable
{
    private readonly object _syncLock = new();
    private readonly Queue<byte> _preBuffer = new();
    private readonly List<byte> _turnBuffer = [];
    private readonly int _preBufferBytes;
    private readonly int _deviceNumber;

    private WaveInEvent? _waveIn;
    private bool _isTurnActive;
    private bool _isStarted;

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

        if (_isStarted)
        {
            return Task.CompletedTask;
        }

        _waveIn = new WaveInEvent
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

    public static string DescribeDefaultInputDevice()
    {
        if (WaveInEvent.DeviceCount <= 0)
        {
            return "No audio input device detected by NAudio.";
        }

        try
        {
            var caps = WaveInEvent.GetCapabilities(0);
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
        for (var index = 0; index < WaveInEvent.DeviceCount; index++)
        {
            var caps = WaveInEvent.GetCapabilities(index);
            devices.Add((index, caps.ProductName));
        }

        return devices;
    }

    public static string DescribeInputDevice(int deviceIndex)
    {
        if (WaveInEvent.DeviceCount <= 0)
        {
            return "No audio input device detected by NAudio.";
        }

        if (deviceIndex < 0 || deviceIndex >= WaveInEvent.DeviceCount)
        {
            return $"Requested audio input device {deviceIndex} is out of range.";
        }

        var caps = WaveInEvent.GetCapabilities(deviceIndex);
        return $"NAudio input device {deviceIndex}: {caps.ProductName}";
    }

    public Task StopAsync()
    {
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
        }

        _isStarted = false;
        return Task.CompletedTask;
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

    public void BeginTurnCapture()
    {
        lock (_syncLock)
        {
            _turnBuffer.Clear();
            _turnBuffer.AddRange(_preBuffer);
            _isTurnActive = true;
            LocalFileLogger.Info("turn_audio", "turn_capture_began", new
            {
                deviceNumber = _deviceNumber,
                preBufferBytesCopied = _turnBuffer.Count
            });
        }
    }

    public byte[] GetTurnBufferAndReset()
    {
        lock (_syncLock)
        {
            var buffer = _turnBuffer.ToArray();
            _turnBuffer.Clear();
            _isTurnActive = false;
            LocalFileLogger.Info("turn_audio", "turn_capture_completed", new
            {
                deviceNumber = _deviceNumber,
                capturedBytes = buffer.Length
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
            for (var index = 0; index < e.BytesRecorded; index++)
            {
                _preBuffer.Enqueue(e.Buffer[index]);
            }

            while (_preBuffer.Count > _preBufferBytes)
            {
                _preBuffer.Dequeue();
            }

            if (_isTurnActive)
            {
                _turnBuffer.AddRange(e.Buffer.AsSpan(0, e.BytesRecorded).ToArray());
            }

            if (StreamChunkAvailable is not null)
            {
                streamChunk = e.Buffer.AsSpan(0, e.BytesRecorded).ToArray();
            }
        }

        // Raised outside the lock -- subscribers (MicAudioStreamer) may do async work
        // (WebSocket sends) that must never hold up the next NAudio callback.
        if (streamChunk is not null)
        {
            StreamChunkAvailable?.Invoke(streamChunk);
        }
    }

    private void HandleRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            throw e.Exception;
        }
    }
}
