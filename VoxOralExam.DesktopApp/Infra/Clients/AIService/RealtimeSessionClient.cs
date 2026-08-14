using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VoxOralExam.DesktopApp.Dtos;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Infra.Clients.AIService;

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

    // ClientWebSocket.SendAsync must never be called concurrently on the same instance -- a
    // second overlapping call throws InvalidOperationException. MicAudioStreamer fires one
    // SendAudioFrameAsync per PCM chunk without waiting for the previous send to finish, and
    // SendJsonAsync (question_start/turn_end/resume/exam_end) can land at the same time from a
    // different call path. Under a fast network each send completes well within one chunk
    // interval so this never collided in practice; under a slow network SendAsync can take long
    // enough that two calls do overlap, and the failure was being silently swallowed (see the
    // old catch in SendAudioFrameAsync), dropping mic audio without any visible error. This
    // semaphore serializes every send instead of relying on timing luck.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _receiveLoopCts;
    private Task? _receiveLoopTask;
    private TaskCompletionSource<RealtimeDecision>? _pendingDecisionTcs;
    // Which turn_order _pendingDecisionTcs is currently waiting on -- lets a resume_ack's
    // recovered_turn_order/decision (see HandleMessage's "resume_ack" case) know whether it
    // actually matches what SendTurnEndAndWaitAsync is still waiting for.
    private int? _pendingDecisionTurnOrder;
    private TaskCompletionSource<bool>? _pendingExamEndAckTcs;
    private Guid _examAttemptId;
    private bool _intentionalClose;
    private bool _forceEndRequested;
    private (Guid AnswerId, int TurnOrder)? _resumeCheckpoint;

    public event Action? OnVadSpeechStart;
    public event Action? OnVadSpeechEnd;
    public event Action<string>? OnPartialTranscript;
    public event Action<string>? OnFinalTranscript;
    public event Action<string>? OnError;
    /// <summary>Fired only when the connection is closed intentionally (CloseAsync/exam ended) --
    /// NOT fired for an unexpected drop, which goes through AttemptReconnectAsync/OnReconnecting
    /// instead. Do not treat this as an error signal.</summary>
    public event Action? OnDisconnected;
    /// <summary>Fired once, the first time ReconnectBackoff's fast attempts (~30s total) are
    /// exhausted without success after an unexpected drop -- signals a likely real outage rather
    /// than a brief blip. Does NOT mean reconnect gave up: an indefinite slower retry loop
    /// (LongOutageRetryInterval) keeps running afterward and still fires OnReconnected if/when it
    /// eventually succeeds. The caller should surface this as "still trying to reconnect".</summary>
    public event Action? OnReconnecting;
    public event Action<int>? OnReconnected;
    public event Action<int, string>? OnAvatarUtteranceComplete;
    public event Action<int, string, string?>? OnSpeakRequested;
    public event Action<string>? OnForceEnded;

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
        _forceEndRequested = false;
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

    // After ReconnectBackoff's fast attempts (~30s total) are exhausted, a real outage (e.g. the
    // exam site's internet actually being down, not just a brief blip) is more likely than not --
    // giving up permanently there would silently abandon the exam attempt with no way back. Keep
    // retrying at this fixed interval indefinitely instead; it only stops via _intentionalClose
    // (exam ended/stopped) or ConnectCoreAsync finally succeeding.
    private static readonly TimeSpan LongOutageRetryInterval = TimeSpan.FromSeconds(20);

    private async Task AttemptReconnectAsync()
    {
        foreach (var delay in ReconnectBackoff)
        {
            if (_intentionalClose)
            {
                return;
            }

            await Task.Delay(delay);
            if (_intentionalClose)
            {
                return;
            }

            try
            {
                LocalFileLogger.Info("realtime_ws", "reconnect_attempt", new { _examAttemptId, delaySeconds = delay.TotalSeconds });
                await ConnectCoreAsync(_examAttemptId, CancellationToken.None);
                await ResumeAfterReconnectAsync();
                return;
            }
            catch (Exception ex)
            {
                LocalFileLogger.Error("realtime_ws", "reconnect_attempt_failed", ex);
            }
        }

        // Tells the UI once that this looks like a real outage, not a blip -- distinct from
        // OnDisconnected (which is reserved for an intentional close) since this means "still
        // trying, just slower now", not "connection is done". OnReconnected still fires normally
        // whenever a later attempt (below) finally succeeds.
        LocalFileLogger.Error(
            "realtime_ws", "reconnect_short_backoff_exhausted",
            new InvalidOperationException("Short reconnect backoff exhausted; entering long-retry mode."));
        OnReconnecting?.Invoke();

        while (!_intentionalClose)
        {
            await Task.Delay(LongOutageRetryInterval);
            if (_intentionalClose)
            {
                return;
            }

            try
            {
                LocalFileLogger.Info(
                    "realtime_ws", "reconnect_attempt",
                    new { _examAttemptId, delaySeconds = LongOutageRetryInterval.TotalSeconds, longRetry = true });
                await ConnectCoreAsync(_examAttemptId, CancellationToken.None);
                await ResumeAfterReconnectAsync();
                return;
            }
            catch (Exception ex)
            {
                LocalFileLogger.Error("realtime_ws", "reconnect_attempt_failed", ex);
            }
        }
    }

    private async Task ResumeAfterReconnectAsync()
    {
        var checkpoint = _resumeCheckpoint;
        var lastArchivedTurnOrder = 0;
        if (checkpoint is not null)
        {
            lastArchivedTurnOrder = await SendResumeAndAwaitAckAsync(checkpoint.Value.AnswerId, checkpoint.Value.TurnOrder);
        }

        // exam_end/exam_end_ack carries no content to recover (unlike turn_end's decision, see
        // HandleMessage's "resume_ack" case) -- it's a plain "did you get this" handshake, so if
        // SendExamEndAndWaitForAckAsync is still waiting when we reconnect, simplest is to just
        // resend exam_end using the SAME pending TCS (not a new one) so that original caller's
        // await still resolves once this resend's ack comes back.
        var pendingExamEnd = _pendingExamEndAckTcs;
        if (pendingExamEnd is not null && !pendingExamEnd.Task.IsCompleted)
        {
            LocalFileLogger.Info("realtime_ws", "resending_exam_end_after_reconnect", new { _examAttemptId });
            await SendJsonAsync(new { type = "exam_end" }, CancellationToken.None);
        }

        LocalFileLogger.Info("realtime_ws", "reconnected", new { _examAttemptId, lastArchivedTurnOrder });
        OnReconnected?.Invoke(lastArchivedTurnOrder);
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

    public Task SendQuestionStartAsync(
        Guid answerId,
        Guid? paperItemId,
        QuestionContextDto question,
        string language,
        string? promptText,
        string? sectionInstruction,
        CancellationToken ct)
    {
        var payload = new
        {
            type = "question_start",
            answer_id = answerId.ToString("D"),
            paper_item_id = paperItemId?.ToString("D"),
            question,
            language,
            prompt_text = promptText,
            section_instruction = sectionInstruction
        };
        return SendJsonAsync(payload, ct);
    }

    /// <summary>
    /// Báo thí sinh vừa rời khỏi cửa sổ thi. Python nhận rồi đẩy tiếp thành alert
    /// WINDOW_FOCUS_LOST tới giám thị (xem attempt/connection._handle_focus_lost).
    ///
    /// Đi nhờ WS này thay vì gọi REST riêng vì kết nối đã mở sẵn và đã xác thực theo phiên thi
    /// -- không phải thêm endpoint, không phải phát token thứ hai. Ngoài ra Java không hề có
    /// client AlertService, Python là đường duy nhất tới được vox-streaming.
    ///
    /// Best-effort có chủ ý: mất một cảnh báo còn hơn làm gián đoạn bài thi, nên caller nuốt
    /// lỗi và bằng chứng vẫn còn trong log máy trạm.
    /// </summary>
    public Task SendFocusLostAsync(DateTimeOffset capturedAt, CancellationToken ct)
    {
        var payload = new
        {
            type = "focus_lost",
            capturedAt = capturedAt.ToUniversalTime().ToString("O")
        };
        return SendJsonAsync(payload, ct);
    }

    public Task SendPresentQuestionAsync(string promptText, CancellationToken ct)
    {
        var payload = new
        {
            type = "present_question",
            prompt_text = promptText
        };
        return SendJsonAsync(payload, ct);
    }

    public async Task SendSpeechBudgetProgressAsync(
        Guid answerId,
        double elapsedSeconds)
    {
        try
        {
            await SendJsonAsync(
                new
                {
                    type = "speech_budget_progress",
                    answer_id = answerId.ToString("D"),
                    elapsed_seconds = Math.Max(0, elapsedSeconds)
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error(
                "realtime_ws",
                "speech_budget_progress_failed",
                ex,
                new { answerId, elapsedSeconds });
        }
    }

    public async Task SendExamEndAndWaitForAckAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingExamEndAckTcs = tcs;
        await SendJsonAsync(new { type = "exam_end" }, ct);

        using var registration = ct.Register(() => tcs.TrySetCanceled());

        // Same rationale as SendTurnEndAndWaitAsync's timeout: if the connection drops right here,
        // ResumeAfterReconnectAsync re-sends exam_end on reconnect (see there) rather than trying
        // to recover a specific decision -- exam_end/exam_end_ack carries no content to recover,
        // it's a plain "did you get this" handshake, so a resend is simplest. This timeout is only
        // the last-resort ceiling for when even that never gets through.
        var timeoutSeconds = Math.Max(15, _settings.QuestionTurnTimeoutSeconds);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var timeoutRegistration = timeoutCts.Token.Register(() =>
        {
            LocalFileLogger.Error(
                "realtime_ws", "exam_end_ack_timeout",
                new TimeoutException($"No exam_end_ack within {timeoutSeconds}s."));
            tcs.TrySetResult(false);
        });

        await tcs.Task;
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
    ///
    /// If the connection drops between turn_end being sent and the decision arriving, this used
    /// to hang forever even after a successful reconnect -- nothing ever resolved this TCS except
    /// an actual "decision" message. Two things now unstick it: (1) a resume_ack carrying a
    /// recovered_turn_order/decision (see HandleMessage) if Python finished and durably persisted
    /// the decision before the WS reply was lost; (2) a last-resort timeout below, for the rarer
    /// case where turn_end itself never reached the server at all (nothing to recover) -- this
    /// throws a TimeoutException rather than inventing a RealtimeDecision itself: deciding what
    /// "no answer ever came" means for the exam (stop the question? retry?) is
    /// RealtimeExamFlowService's call, not this network client's.
    /// </summary>
    public async Task<RealtimeDecision> SendTurnEndAndWaitAsync(
        int turnOrder,
        bool speechBudgetExceeded,
        double durationSeconds,
        int assessmentTurnCount,
        int maxAssessmentTurns,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<RealtimeDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingDecisionTcs = tcs;
        _pendingDecisionTurnOrder = turnOrder;

        // Python applies the limit after it knows whether this decision is a clarification.
        // That prevents a clarification at the boundary from consuming an assessment turn.
        await SendJsonAsync(new
        {
            type = "turn_end",
            is_last_allowed_turn = speechBudgetExceeded,
            speech_budget_exceeded = speechBudgetExceeded,
            duration_seconds = durationSeconds,
            assessment_turn_count = assessmentTurnCount,
            max_assessment_turns = maxAssessmentTurns
        }, ct);

        using var registration = ct.Register(() => tcs.TrySetCanceled());

        // Generous ceiling reusing the existing per-turn timeout setting -- covers the rare case
        // where reconnect/resume can't recover anything (turn_end never reached the server at
        // all), so the exam flow gives up on this one turn instead of blocking forever.
        var timeoutSeconds = Math.Max(15, _settings.QuestionTurnTimeoutSeconds);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var timeoutRegistration = timeoutCts.Token.Register(() =>
        {
            var timeoutException = new TimeoutException($"No decision (direct or resume-recovered) within {timeoutSeconds}s.");
            LocalFileLogger.Error("realtime_ws", "turn_end_decision_timeout", timeoutException, new { turnOrder });
            tcs.TrySetException(timeoutException);
        });

        try
        {
            return await tcs.Task;
        }
        finally
        {
            _pendingDecisionTurnOrder = null;
        }
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
            await _sendLock.WaitAsync();
            try
            {
                if (socket.State != WebSocketState.Open)
                {
                    return;
                }

                await socket.SendAsync(pcm, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
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

        await _sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
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
                    var decision = ParseDecision(doc.RootElement.GetProperty("decision"));
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
                case "force_end":
                    _forceEndRequested = true;
                    _intentionalClose = true;
                    var reason = GetPropertyText(doc, "reason");
                    LocalFileLogger.Info("realtime_ws", "force_end_received", new { reason });
                    _pendingDecisionTcs?.TrySetCanceled();
                    _pendingExamEndAckTcs?.TrySetCanceled();
                    _pendingResumeAckTcs?.TrySetCanceled();
                    OnForceEnded?.Invoke(reason);
                    break;
                case "resume_ack":
                    var lastArchivedTurnOrder = doc.RootElement.TryGetProperty("last_archived_turn_order", out var lto) ? lto.GetInt32() : -1;
                    _pendingResumeAckTcs?.TrySetResult(lastArchivedTurnOrder);
                    LocalFileLogger.Info("realtime_ws", "resume_ack_received", new { lastArchivedTurnOrder });

                    // Recovery: if this attempt reconnected while SendTurnEndAndWaitAsync was still
                    // waiting on a decision that never arrived, Python may have finished computing
                    // it anyway and persisted it durably before the WS reply was lost --
                    // recovered_turn_order/decision being present and matching what we're still
                    // waiting on means exactly that. Resolve the pending TCS directly instead of
                    // leaving it to sit until SendTurnEndAndWaitAsync's own timeout gives up.
                    if (doc.RootElement.TryGetProperty("recovered_turn_order", out var rto)
                        && doc.RootElement.TryGetProperty("decision", out var recoveredDecisionElement)
                        && _pendingDecisionTcs is not null
                        && _pendingDecisionTurnOrder == rto.GetInt32())
                    {
                        var recoveredDecision = ParseDecision(recoveredDecisionElement);
                        LocalFileLogger.Info("realtime_ws", "decision_recovered_via_resume", new { turnOrder = rto.GetInt32() });
                        _pendingDecisionTcs.TrySetResult(recoveredDecision);
                    }
                    break;
                case "avatar_utterance_complete":
                    var sequence = doc.RootElement.TryGetProperty("sequence", out var seq) ? seq.GetInt32() : -1;
                    var utteranceText = GetText(doc);
                    LocalFileLogger.Info("realtime_ws", "avatar_utterance_complete_received", new { sequence, utteranceText });
                    OnAvatarUtteranceComplete?.Invoke(sequence, utteranceText);
                    break;
                case "speak":
                    var speakSequence = doc.RootElement.TryGetProperty("sequence", out var sq) ? sq.GetInt32() : -1;
                    var speakText = GetText(doc);
                    var speakRate = doc.RootElement.TryGetProperty("rate", out var rt) && rt.ValueKind != JsonValueKind.Null
                        ? rt.GetString()
                        : null;
                    LocalFileLogger.Info("realtime_ws", "speak_received", new { speakSequence, speakText });
                    OnSpeakRequested?.Invoke(speakSequence, speakText, speakRate);
                    break;
                case "question_start_ack":
                    LocalFileLogger.Info("realtime_ws", "ack_received", new { type, json });
                    break;
                case "exam_end_ack":
                    LocalFileLogger.Info("realtime_ws", "ack_received", new { type, json });
                    _pendingExamEndAckTcs?.TrySetResult(true);
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

    private static string GetPropertyText(JsonDocument doc, string propertyName) =>
        doc.RootElement.TryGetProperty(propertyName, out var value) ? value.GetString() ?? "" : "";

    private static RealtimeDecision ParseDecision(JsonElement decisionElement) => new()
    {
        ShouldContinue = decisionElement.GetProperty("should_continue").GetBoolean(),
        NextPromptText = decisionElement.TryGetProperty("next_prompt_text", out var p) && p.ValueKind != JsonValueKind.Null
            ? p.GetString()
            : null,
        Reason = decisionElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
    };
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
        _sendLock.Dispose();
    }
}

public sealed class RealtimeDecision
{
    public bool ShouldContinue { get; set; }
    public string? NextPromptText { get; set; }
    public string Reason { get; set; } = string.Empty;
}
