using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VoxOralExam.Core.Dtos;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Infrastructure;

/// <summary>
/// One WebSocket connection per exam attempt to Python's /realtime/attempts/{examAttemptId}
/// (Phase 5 of docs/realtime-self-hosted-avatar-plan.md). Opened once at exam start and held
/// open for every question -- switching questions is an in-band question_start message, never a
/// reconnect, mirroring AttemptConnection's design on the Python side.
///
/// Sends: question_start / turn_end / resume (JSON text frames), continuous mic PCM (binary
/// frames, via MicAudioStreamer). Receives: question_start_ack / decision / resume_ack (JSON)
/// plus the VAD/transcript events AttemptConnection forwards (vad_speech_start, vad_speech_end,
/// partial_transcript, final_transcript) -- exposed as events so RealtimeExamFlowService can
/// react to vad_speech_start (begin turn capture) the same way the old Tavus flow reacted to
/// Daily's user-started-speaking signal.
///
/// Phase 6: an unexpected disconnect (receive loop ending without an explicit CloseAsync) is
/// retried automatically with backoff, then re-sends whatever (answerId, turnOrder) checkpoint
/// RealtimeExamFlowService last reported via SetResumeCheckpoint, via the resume handshake --
/// OnReconnected reports the server's durable last_archived_turn_order so the caller can log/
/// compare against its own local state. This does not (yet) implement full turn-skipping
/// realignment if the two disagree -- a known gap, see the plan doc's Phase 6 section.
/// </summary>
public sealed class RealtimeSessionClient : IAsyncDisposable
{
    private static readonly TimeSpan[] ReconnectBackoff =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)
    ];

    private readonly AppSettings _settings;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveLoopCts;
    private Task? _receiveLoopTask;
    private TaskCompletionSource<RealtimeDecision>? _pendingDecisionTcs;
    private Guid _examAttemptId;
    private bool _intentionalClose;
    private (Guid AnswerId, int TurnOrder)? _resumeCheckpoint;

    public event Action? OnVadSpeechStart;
    public event Action? OnVadSpeechEnd;
    public event Action<string>? OnPartialTranscript;
    public event Action<string>? OnFinalTranscript;
    public event Action<string>? OnError;
    public event Action? OnDisconnected;
    public event Action<int>? OnReconnected;
    public event Action<int, string>? OnAvatarUtteranceComplete;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public RealtimeSessionClient(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// RealtimeExamFlowService calls this after every completed turn so a later automatic
    /// reconnect knows what to resume from.
    /// </summary>
    public void SetResumeCheckpoint(Guid answerId, int turnOrder)
    {
        _resumeCheckpoint = (answerId, turnOrder);
    }

    public async Task ConnectAsync(Guid examAttemptId, CancellationToken ct)
    {
        _examAttemptId = examAttemptId;
        _intentionalClose = false;
        await ConnectCoreAsync(examAttemptId, ct);
    }

    private async Task ConnectCoreAsync(Guid examAttemptId, CancellationToken ct)
    {
        var baseUri = new Uri(_settings.PythonBaseUrl);
        var scheme = baseUri.Scheme == "https" ? "wss" : "ws";
        var uri = new Uri($"{scheme}://{baseUri.Authority}{_settings.RealtimeWebSocketPath.TrimEnd('/')}/{examAttemptId:D}");

        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(uri, ct);
        LocalFileLogger.Info("realtime_ws", "connected", new { examAttemptId, uri = uri.ToString() });

        _receiveLoopCts = new CancellationTokenSource();
        _receiveLoopTask = ReceiveLoopAsync(_receiveLoopCts.Token);
    }

    private async Task AttemptReconnectAsync()
    {
        foreach (var delay in ReconnectBackoff)
        {
            await Task.Delay(delay);
            try
            {
                LocalFileLogger.Info("realtime_ws", "reconnect_attempt", new { _examAttemptId, delaySeconds = delay.TotalSeconds });
                await ConnectCoreAsync(_examAttemptId, CancellationToken.None);

                var checkpoint = _resumeCheckpoint;
                var lastArchivedTurnOrder = 0;
                if (checkpoint is not null)
                {
                    lastArchivedTurnOrder = await SendResumeAndAwaitAckAsync(checkpoint.Value.AnswerId, checkpoint.Value.TurnOrder);
                }

                LocalFileLogger.Info("realtime_ws", "reconnected", new { _examAttemptId, lastArchivedTurnOrder });
                OnReconnected?.Invoke(lastArchivedTurnOrder);
                return;
            }
            catch (Exception ex)
            {
                LocalFileLogger.Error("realtime_ws", "reconnect_attempt_failed", ex);
            }
        }

        LocalFileLogger.Error("realtime_ws", "reconnect_gave_up", new InvalidOperationException("Exhausted all reconnect attempts."));
        OnDisconnected?.Invoke();
    }

    private async Task<int> SendResumeAndAwaitAckAsync(Guid answerId, int turnOrder)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResumeAckTcs = tcs;
        await SendResumeAsync(answerId, turnOrder, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var reg = cts.Token.Register(() => tcs.TrySetResult(-1));
        return await tcs.Task;
    }

    private TaskCompletionSource<int>? _pendingResumeAckTcs;

    public Task SendQuestionStartAsync(Guid answerId, QuestionContextDto question, string language, CancellationToken ct)
    {
        var payload = new
        {
            type = "question_start",
            answer_id = answerId.ToString("D"),
            question,
            language
        };
        return SendJsonAsync(payload, ct);
    }

    public Task SendResumeAsync(Guid answerId, int turnOrder, CancellationToken ct)
    {
        var payload = new
        {
            type = "resume",
            answer_id = answerId.ToString("D"),
            turn_order = turnOrder
        };
        return SendJsonAsync(payload, ct);
    }

    /// <summary>
    /// Sends turn_end and awaits the matching decision response. The protocol is strictly
    /// sequential (RealtimeExamFlowService never sends a second turn_end before the previous
    /// one's decision arrives), so a single pending-TCS field is sufficient -- no per-call
    /// correlation id needed.
    /// </summary>
    public async Task<RealtimeDecision> SendTurnEndAndWaitAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<RealtimeDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingDecisionTcs = tcs;

        await SendJsonAsync(new { type = "turn_end" }, ct);

        using var registration = ct.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }

    public async Task SendAudioFrameAsync(byte[] pcm)
    {
        var socket = _webSocket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            await socket.SendAsync(pcm, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("realtime_ws", "send_audio_frame_failed", ex);
        }
    }

    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        var socket = _webSocket ?? throw new InvalidOperationException("RealtimeSessionClient is not connected.");
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var socket = _webSocket!;
        var buffer = new byte[16 * 1024];

        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var messageStream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    messageStream.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                var json = Encoding.UTF8.GetString(messageStream.ToArray());
                HandleMessage(json);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("realtime_ws", "receive_loop_failed", ex);
            OnError?.Invoke(ex.Message);
        }
        finally
        {
            if (_intentionalClose)
            {
                OnDisconnected?.Invoke();
            }
            else
            {
                LocalFileLogger.Info("realtime_ws", "unexpected_disconnect", new { _examAttemptId });
                _ = AttemptReconnectAsync();
            }
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "decision":
                    var decisionElement = doc.RootElement.GetProperty("decision");
                    var decision = new RealtimeDecision
                    {
                        ShouldContinue = decisionElement.GetProperty("should_continue").GetBoolean(),
                        NextPromptText = decisionElement.TryGetProperty("next_prompt_text", out var p) && p.ValueKind != JsonValueKind.Null
                            ? p.GetString()
                            : null,
                        Reason = decisionElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : ""
                    };
                    _pendingDecisionTcs?.TrySetResult(decision);
                    break;
                case "vad_speech_start":
                    OnVadSpeechStart?.Invoke();
                    break;
                case "vad_speech_end":
                    OnVadSpeechEnd?.Invoke();
                    break;
                case "partial_transcript":
                    OnPartialTranscript?.Invoke(GetText(doc));
                    break;
                case "final_transcript":
                    OnFinalTranscript?.Invoke(GetText(doc));
                    break;
                case "error":
                    OnError?.Invoke(GetText(doc));
                    break;
                case "resume_ack":
                    var lastArchivedTurnOrder = doc.RootElement.TryGetProperty("last_archived_turn_order", out var lto) ? lto.GetInt32() : -1;
                    _pendingResumeAckTcs?.TrySetResult(lastArchivedTurnOrder);
                    LocalFileLogger.Info("realtime_ws", "resume_ack_received", new { lastArchivedTurnOrder });
                    break;
                case "avatar_utterance_complete":
                    var sequence = doc.RootElement.TryGetProperty("sequence", out var seq) ? seq.GetInt32() : -1;
                    var utteranceText = GetText(doc);
                    LocalFileLogger.Info("realtime_ws", "avatar_utterance_complete_received", new { sequence, utteranceText });
                    OnAvatarUtteranceComplete?.Invoke(sequence, utteranceText);
                    break;
                case "question_start_ack":
                    LocalFileLogger.Info("realtime_ws", "ack_received", new { type, json });
                    break;
                default:
                    LocalFileLogger.Info("realtime_ws", "unhandled_message_type", new { type, json });
                    break;
            }
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("realtime_ws", "handle_message_failed", ex, new { json });
        }
    }

    private static string GetText(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";

    public async Task CloseAsync()
    {
        _intentionalClose = true;
        _receiveLoopCts?.Cancel();

        var socket = _webSocket;
        if (socket is not null && socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            catch (Exception ex)
            {
                LocalFileLogger.Error("realtime_ws", "close_failed", ex);
            }
        }

        if (_receiveLoopTask is not null)
        {
            try
            {
                await _receiveLoopTask;
            }
            catch (Exception)
            {
            }
        }

        socket?.Dispose();
        _webSocket = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _receiveLoopCts?.Dispose();
    }
}

public sealed class RealtimeDecision
{
    public bool ShouldContinue { get; set; }
    public string? NextPromptText { get; set; }
    public string Reason { get; set; } = string.Empty;
}
