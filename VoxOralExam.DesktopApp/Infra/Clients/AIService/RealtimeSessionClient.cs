using System.IO;
using System.Net;
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

    // Giữ cookie qua MỌI lần reconnect của cùng một bài thi.
    //
    // ALB của cả agents lẫn vox bật sticky bằng cookie (charts/*/templates/ingress.yaml:
    // stickiness.enabled=true, lb_cookie.duration_seconds=7200) vì trạng thái phiên nằm trong RAM
    // của từng pod. Nhưng ClientWebSocket.Options.Cookies mặc định là null: cookie AWSALB trả về ở
    // lần bắt tay đầu bị vứt đi, và mỗi ConnectAsync sau đó không gửi cookie nào -- ALB chia lại
    // theo vòng, hoàn toàn có thể rơi sang pod khác. Cấu hình sticky ở cụm vì thế KHÔNG có tác dụng
    // với máy thi (trình duyệt thì có, vì nó tự quản cookie).
    //
    // agents chạy autoscaling 1-3 pod nên đây là chuyện thật khi có tải. Một container dùng chung
    // cho cả vòng đời client là đủ: nó tự lưu cookie từ response bắt tay và gửi lại ở lần sau.
    private readonly CookieContainer _cookies = new();

    /// <summary>
    /// Trả PCM đã thu được của lượt ĐANG DỞ, tính từ offset truyền vào; mảng rỗng nếu không có lượt
    /// nào đang chạy. ExamAttemptRunner gắn vào, trỏ tới TurnAudioRecorder.PeekTurnBufferFrom.
    /// </summary>
    public Func<int, byte[]>? CurrentTurnAudioProvider { get; set; }

    /// <summary>
    /// Số giây đồng hồ còn lại NGAY LÚC NÀY, hoặc null nếu chưa chạy đồng hồ.
    /// </summary>
    /// <remarks>
    /// Gửi kèm mỗi <c>question_start</c> để Python chốt mốc theo câu hỏi. Thí sinh bị ngắt giữa câu
    /// mà chưa trả lời lượt nào thì lúc vào lại cả media lẫn thời gian chuẩn bị đều chạy LẠI TỪ ĐẦU
    /// -- không có mốc này thì phần đã tiêu ở lần vào trước bị tính thêm lần nữa.
    /// Xem <c>archive_store.persist_question_snapshot</c>.
    /// </remarks>
    public Func<int?>? CurrentRemainingSecondsProvider { get; set; }

    private volatile bool _audioResyncInProgress;

    /// <summary>
    /// Coi là mất kết nối khi quá chừng này không nghe thấy gì từ server. Bằng ba nhịp tim
    /// (HEARTBEAT_INTERVAL_SECONDS = 5 ở agents/src/realtime/attempt/connection.py) nên một gói
    /// ping tới trễ không làm đồng hồ dừng oan.
    /// </summary>
    private static readonly TimeSpan ServerSilenceTimeout = TimeSpan.FromSeconds(15);

    private long _lastServerMessageAtTicks;

    /// <summary>
    /// Server còn sống hay không, đo bằng "lần cuối nghe thấy gì đó từ server".
    ///
    /// <para>KHÔNG hỏi <c>WebSocketState</c>: trạng thái đó vẫn báo Open một lúc lâu sau khi mạng
    /// đã chết. Cũng KHÔNG suy từ việc server im lặng đơn thuần -- giao thức này im lặng một cách
    /// hợp lệ trong lúc thí sinh đang nghĩ, nên phía server có nhịp tim riêng để phân biệt hai kiểu
    /// im lặng đó.</para>
    ///
    /// <para><c>ExamViewModel</c> đọc cờ này để NGỪNG TRỪ GIÂY khi mất kết nối. Ngừng trừ chứ không
    /// hoàn lại sau: checkpoint phía server chỉ cho giảm, cộng ngược lên sẽ bị từ chối.</para>
    /// </summary>
    public bool IsServerAlive
    {
        get
        {
            // Chưa từng thấy nhịp tim nào thì KHÔNG áp luật im lặng.
            //
            // Bảo vệ trường hợp máy thi đã cập nhật mà backend thì chưa: server cũ chỉ nói khi có
            // việc, nên 15 giây im lặng là chuyện bình thường -- áp luật ở đó sẽ đóng băng đồng hồ
            // gần như vĩnh viễn, kể cả lúc thí sinh đang nói. Thấy được một nhịp tim mới chứng minh
            // server bên kia là bản có hỗ trợ, từ đó mới bắt đầu tin vào sự im lặng.
            if (!_serverHeartbeatSeen)
            {
                return true;
            }

            var ticks = Interlocked.Read(ref _lastServerMessageAtTicks);
            if (ticks == 0)
            {
                return false;
            }

            return DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < ServerSilenceTimeout;
        }
    }

    private volatile bool _serverHeartbeatSeen;

    /// <summary>Nguyên văn turn_end đang chờ hồi đáp, giữ để gửi lại sau khi nối lại.</summary>
    private object? _pendingTurnEndPayload;

    /// <summary>
    /// Lượt mà resume_ack vừa báo là đã khôi phục được quyết định. Dùng để KHÔNG gửi lại turn_end
    /// cho chính lượt đó.
    /// </summary>
    private int? _recoveredTurnOrder;

    private void MarkServerAlive() =>
        Interlocked.Exchange(ref _lastServerMessageAtTicks, DateTime.UtcNow.Ticks);

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

    /// <summary>
    /// Lượt gần nhất đã hoàn tất, hoặc null nếu chưa lượt nào xong.
    ///
    /// <para>Dùng chung đúng mốc mà reconnect vẫn dùng, thay vì dựng một biến đếm thứ hai: hai
    /// nguồn sự thật cho cùng một con số là hai nguồn để lệch nhau. ExamAttemptRunner đọc nó để
    /// hỏi Python xem ĐÚNG lượt cuối đã lưu trữ xong chưa trước khi nộp bài.</para>
    /// </summary>
    public (Guid AnswerId, int TurnOrder)? LastCompletedTurn => _resumeCheckpoint;

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
        // Xem chú thích ở _cookies: không có dòng này thì sticky của ALB vô hiệu với máy thi.
        _webSocket.Options.Cookies = _cookies;
        // Rút dây mạng KHÔNG làm ReceiveAsync lỗi ngay: TCP giữ socket "mở" cho tới khi hết thời
        // gian truyền lại, có thể hàng phút, và suốt lúc đó vòng nối lại không hề khởi động. Đặt
        // hai mốc này để socket tự hỏng sau ~13 giây im lặng, tức nối lại bắt đầu sớm hơn nhiều.
        _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(8);
        _webSocket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(5);
        await _webSocket.ConnectAsync(uri, ct);
        MarkServerAlive();
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

    /// <summary>
    /// Gửi lại <c>turn_end</c> CHỈ KHI chắc chắn server chưa từng nhận được nó.
    ///
    /// <para>Vì sao cần: <c>ResumeAfterReconnectAsync</c> gửi lại <c>exam_end</c> nhưng cố ý không
    /// gửi lại <c>turn_end</c>, vì <c>turn_end</c> mang nội dung -- gửi lại một lượt server ĐÃ xử lý
    /// sẽ chốt và archive lượt đó lần thứ hai, lần này với đệm audio đã bị xả rỗng. Đường cứu sẵn có
    /// (<c>recovered_turn_order</c> trong <c>resume_ack</c>) chỉ phủ trường hợp "turn_end tới nơi
    /// rồi, chỉ mất gói trả lời". Trường hợp "turn_end chưa từng tới" thì không ai phủ, và kết cục
    /// là chờ hết <c>QuestionTurnTimeoutSeconds</c> rồi mất trắng lượt đó.</para>
    ///
    /// <para>Phân biệt được hai trường hợp là nhờ trạng thái BỀN phía Python:
    /// <c>persist_realtime_transcript</c> được await NGAY tại <c>turn_end</c>, trước lời gọi LLM
    /// chậm -- đúng để việc khôi phục sau nối lại luôn nhìn thấy một <c>turn_end</c> đã tới server.
    /// Nên các điều kiện dưới đây không phải phỏng đoán, chúng đọc đúng dấu vết đó.</para>
    ///
    /// <para>Cả hai đều lấy từ lưu trữ bền chứ không phải RAM, nên vẫn đúng dù
    /// <c>AttemptConnection</c> được dựng mới ở mỗi lần accept WebSocket.</para>
    ///
    /// <para>Có BA trạng thái, không phải hai, và trạng thái thứ ba là chỗ từng thủng:
    /// "server đã xử lý" (không gửi), "server chắc chắn chưa nhận" (gửi), và "KHÔNG BIẾT" (không
    /// gửi). <paramref name="lastArchivedTurnOrder"/> là <c>int?</c> chính vì thế -- trước đây
    /// trạng thái thứ ba bị mã hoá thành số (-1 khi ack quá hạn hoặc thiếu trường, 0 khi chưa có
    /// checkpoint), mà mọi con số đều thua phép so <c>>= turnOrder</c> nên "không biết" âm thầm bị
    /// xử như "chắc chắn chưa nhận". Đường mạng chậm -- đúng thứ cơ chế này sinh ra để chịu -- do đó
    /// lại là đường dẫn tới archive lượt hai lần với audio rỗng. Đừng nhét "không biết" vào một
    /// con số lần nữa.</para>
    /// </summary>
    private async Task ResendTurnEndIfServerNeverGotItAsync(int? lastArchivedTurnOrder)
    {
        var payload = _pendingTurnEndPayload;
        var pendingTurnOrder = _pendingDecisionTurnOrder;
        var pending = _pendingDecisionTcs;

        if (payload is null || pendingTurnOrder is not int turnOrder
            || pending is null || pending.Task.IsCompleted)
        {
            return;
        }

        // Server đã xử lý lượt này -- quyết định vừa được khôi phục qua resume_ack.
        if (_recoveredTurnOrder == turnOrder)
        {
            return;
        }

        // KHÔNG BIẾT thì KHÔNG gửi. Hợp đồng của hàm này là "gửi lại CHỈ KHI chắc chắn server chưa
        // từng nhận được" (xem doc ở trên), mà không biết thì không phải là chắc chắn.
        //
        // Vì sao im lặng lại đúng, dù nó làm mất lượt: hai kết cục không cân nhau. Không gửi mà lẽ
        // ra nên gửi thì lượt hết hạn theo QuestionTurnTimeoutSeconds -- mất một lượt, nhưng mất
        // lộ thiên, và bản ghi còn nguyên. Gửi mà lẽ ra không nên thì Python chốt và archive lượt
        // đó LẦN THỨ HAI với đệm audio đã bị xả rỗng: bản ghi bị GHI ĐÈ bằng một lượt trống, âm
        // thầm, ngay trong hồ sơ dùng để chấm và để phúc khảo. Sai kiểu thứ hai không sửa được sau.
        if (lastArchivedTurnOrder is not int archivedTurnOrder)
        {
            LocalFileLogger.Info("realtime_ws", "turn_end_resend_skipped_unknown_archive_state", new
            {
                _examAttemptId,
                turnOrder
            });
            return;
        }

        // Server đã lưu xong lượt này (và có thể cả những lượt sau).
        if (archivedTurnOrder >= turnOrder)
        {
            return;
        }

        LocalFileLogger.Info("realtime_ws", "resending_turn_end_after_reconnect", new
        {
            _examAttemptId,
            turnOrder,
            lastArchivedTurnOrder
        });
        await SendJsonAsync(payload, CancellationToken.None);
    }

    private async Task ResumeAfterReconnectAsync()
    {
        var checkpoint = _resumeCheckpoint;
        // null = KHÔNG BIẾT, và không có checkpoint thì đúng là không biết thật: chưa lượt nào hoàn
        // tất nên không có gì để `resume` từ đó, ta không hỏi server câu nào, và ta không nhận được
        // câu trả lời nào. Giá trị khởi tạo cũ là 0 -- một con số, nên nó THUA phép so
        // `>= turnOrder` ở dưới y như -1 và mở cùng một lối gửi lại turn_end mù.
        //
        // Đây là đường dễ dính nhất trong ba đường, vì nó không cần mạng chậm hay server bản cũ:
        // chỉ cần rớt mạng giữa LƯỢT ĐẦU TIÊN của bài thi. Server có thể đã nhận turn_end, đã
        // persist transcript, và đang gọi LLM -- từ phía client thì lượt đó vẫn "chưa hoàn tất".
        int? lastArchivedTurnOrder = null;
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

        await ResyncTurnAudioAsync();

        // SAU ResyncTurnAudioAsync, không được trước: _handle_turn_end bên Python chốt và XẢ đệm
        // audio ngay đầu hàm, nên gửi lệnh trước khi tiếng lên tới nơi là chốt một lượt rỗng.
        await ResendTurnEndIfServerNeverGotItAsync(lastArchivedTurnOrder);

        LocalFileLogger.Info("realtime_ws", "reconnected", new { _examAttemptId, lastArchivedTurnOrder });
        // Sự kiện vẫn mang int để không đổi chữ ký công khai; -1 ở ĐÂY chỉ là "không biết" dùng cho
        // log/hiển thị, và nó an toàn vì không còn nhánh quyết định nào đọc con số này nữa -- chốt
        // gửi lại turn_end ở trên đã nhận int? trực tiếp.
        OnReconnected?.Invoke(lastArchivedTurnOrder ?? -1);
    }

    /// <summary>
    /// Chép ngược toàn bộ PCM của lượt đang dở lên kết nối vừa lập lại.
    /// </summary>
    /// <remarks>
    /// Vì sao cần: bộ đệm audio của lượt nằm trong RAM của MỘT đối tượng AttemptConnection bên
    /// Python, mà mỗi lần accept WebSocket nó dựng đối tượng MỚI (realtime_controller.py) -- bộ đệm
    /// mới luôn rỗng. Cộng với việc SendAudioFrameAsync âm thầm bỏ khung khi socket không mở, đứt
    /// mạng giữa lượt làm mất TOÀN BỘ audio của lượt đó, kể cả phần đã gửi trước khi đứt.
    ///
    /// <para>Hậu quả đã gặp thật (agents/src/realtime/attempt/connection.py, sự cố 2026-08-18:
    /// "15 giây, 37 từ, audio_url null"): transcript vẫn có vì Voice Live ghi thẳng xuống Postgres,
    /// nên bài vẫn chấm được -- nhưng KHÔNG còn bản ghi âm để đối chiếu lúc phúc khảo.</para>
    ///
    /// <para>Vòng lặp đuổi bắt chứ không chụp một phát: mic vẫn thu trong lúc đang gửi, nên sau mỗi
    /// đợt phải hỏi lại phần mới thu thêm. Cờ _audioResyncInProgress chặn khung trực tiếp suốt thời
    /// gian đó để audio không bị đảo thứ tự; hạ cờ khi không còn phần dư nào.</para>
    ///
    /// <para>Không cần đổi giao thức: Python nối các khung nhị phân vào đúng bộ đệm lượt như bình
    /// thường, và bộ đệm mới đang rỗng nên nối vào chính là nạp lại.</para>
    /// </remarks>
    private async Task ResyncTurnAudioAsync()
    {
        var provider = CurrentTurnAudioProvider;
        if (provider is null)
        {
            return;
        }

        var offset = 0;
        _audioResyncInProgress = true;
        try
        {
            // Chặn trên để một lượt bất thường (mic kẹt, provider trả hoài) không giữ cờ mãi mãi --
            // giữ cờ là chặn luôn audio trực tiếp, tức hỏng đúng thứ đang đi cứu.
            for (var pass = 0; pass < 50; pass++)
            {
                var chunk = provider(offset);
                if (chunk.Length == 0)
                {
                    break;
                }

                offset += chunk.Length;
                await SendAudioFrameCoreAsync(chunk);
            }
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("realtime_ws", "turn_audio_resync_failed", ex, new { _examAttemptId });
        }
        finally
        {
            _audioResyncInProgress = false;
        }

        if (offset > 0)
        {
            LocalFileLogger.Info("realtime_ws", "turn_audio_resynced", new { _examAttemptId, bytes = offset });
        }
    }

    /// <summary>
    /// Gửi <c>resume</c> và chờ <c>resume_ack</c>. Trả về <c>last_archived_turn_order</c> của server,
    /// hoặc <c>null</c> nghĩa là KHÔNG BIẾT -- ack không về kịp trong 10 giây.
    ///
    /// <para>Null chứ không phải -1, và đây là điểm mấu chốt: giá trị này đi thẳng vào phép so
    /// <c>lastArchivedTurnOrder >= turnOrder</c> ở <c>ResendTurnEndIfServerNeverGotItAsync</c>. Mọi
    /// số nguyên đại diện cho "không biết" đều THUA phép so đó (-1 thua mọi lượt), tức là mở toang
    /// đúng cái chốt được dựng lên để chặn -- và hậu quả là archive lượt lần thứ hai với đệm audio
    /// đã rỗng. "Không biết" phải là một trạng thái riêng, không được là một con số.</para>
    ///
    /// <para>Ack chậm quá 10 giây KHÔNG hiếm: đây đúng là kịch bản đường truyền chậm mà toàn bộ cơ
    /// chế nối lại này sinh ra để phục vụ.</para>
    /// </summary>
    private async Task<int?> SendResumeAndAwaitAckAsync(Guid answerId, int turnOrder)
    {
        var tcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResumeAckTcs = tcs;
        await SendResumeAsync(answerId, turnOrder, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var reg = cts.Token.Register(() =>
        {
            if (tcs.TrySetResult(null))
            {
                LocalFileLogger.Info(
                    "realtime_ws", "resume_ack_timeout",
                    new { _examAttemptId, answerId, turnOrder });
            }
        });
        return await tcs.Task;
    }

    private TaskCompletionSource<int?>? _pendingResumeAckTcs;

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
            section_instruction = sectionInstruction,
            // Mốc đồng hồ để hoàn giờ khi bị ngắt giữa câu -- xem CurrentRemainingSecondsProvider.
            remaining_seconds_at_question_start = CurrentRemainingSecondsProvider?.Invoke()
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

    /// <summary>
    /// Tài nguyên audio/video của câu hỏi không phát được, kể cả sau một lần thử lại.
    ///
    /// <para>Đi nhờ WS sẵn có như <see cref="SendFocusLostAsync"/>, nhưng Python CHỈ GHI LOG chứ
    /// không đẩy thành cảnh báo giám thị: đây là lỗi kỹ thuật, không phải hành vi của thí sinh.
    /// Mục đích là để lại dấu vết ở phía server cho người chấm truy được khi thí sinh khiếu nại
    /// "em không nghe thấy gì" -- log trên máy trạm thì không ai đọc tới.</para>
    /// </summary>
    public Task SendAssetPlaybackFailedAsync(string reason, int questionNumber, CancellationToken ct)
    {
        var payload = new
        {
            type = "asset_playback_failed",
            reason,
            questionNumber
        };
        return SendJsonAsync(payload, ct);
    }

    /// <summary>
    /// Báo camera đã ngừng gửi khung hình quá ngưỡng. Đi chung đường với
    /// <see cref="SendFocusLostAsync"/> vì cùng một lý do: WS đã mở sẵn, đã xác thực theo phiên thi,
    /// và Python là nơi DUY NHẤT trong hệ nối được tới AlertService của vox-streaming.
    ///
    /// <para><paramref name="capturedAt"/> là mốc khung hình CUỐI CÙNG, không phải lúc phát hiện.
    /// Chênh lệch giữa hai mốc đúng bằng ngưỡng cảnh báo, và nếu gửi mốc phát hiện thì mọi khoảng
    /// trống ghi trong sổ đều lệch khỏi khoảng trống thật trong bản ghi.</para>
    /// </summary>
    public Task SendCameraSignalLostAsync(
        DateTimeOffset capturedAt,
        bool neverDelivered,
        CancellationToken ct)
    {
        var payload = new
        {
            type = "camera_signal_lost",
            capturedAt = capturedAt.ToUniversalTime().ToString("O"),
            neverDelivered
        };
        return SendJsonAsync(payload, ct);
    }

    /// <summary>
    /// Báo khung hình đã trở lại, kèm tổng thời lượng mất.
    ///
    /// <para>Tồn tại để đóng KHOẢNG. "Mất camera lúc 10:32" gần như vô dụng với người chấm: hai
    /// mươi giây hay suốt phần còn lại của bài thi là hai kết luận hoàn toàn khác nhau, và nếu
    /// không có sự kiện này thì sổ bằng chứng chỉ có điểm bắt đầu.</para>
    /// </summary>
    public Task SendCameraSignalRestoredAsync(
        DateTimeOffset capturedAt,
        TimeSpan outage,
        CancellationToken ct)
    {
        var payload = new
        {
            type = "camera_signal_restored",
            capturedAt = capturedAt.ToUniversalTime().ToString("O"),
            outageSeconds = Math.Round(outage.TotalSeconds, 1)
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
        // Dọn dấu vết của lượt trước: nó chỉ có nghĩa cho đúng lượt đã sinh ra nó. Giữ lại thì
        // điều kiện "đã khôi phục rồi" ở ResendTurnEndIfServerNeverGotItAsync phải dựa vào việc so
        // sánh may mắn không trùng số, thay vì dựa vào một bất biến rõ ràng.
        _recoveredTurnOrder = null;

        // Python applies the limit after it knows whether this decision is a clarification.
        // That prevents a clarification at the boundary from consuming an assessment turn.
        var payload = new
        {
            type = "turn_end",
            is_last_allowed_turn = speechBudgetExceeded,
            speech_budget_exceeded = speechBudgetExceeded,
            duration_seconds = durationSeconds,
            assessment_turn_count = assessmentTurnCount,
            max_assessment_turns = maxAssessmentTurns
        };
        // Giữ lại nguyên văn để gửi lại được sau khi nối lại -- xem ResendTurnEndIfServerNeverGotItAsync.
        // Dựng lại từ trí nhớ ở chỗ khác là mở đường cho hai lần gửi mang tham số khác nhau.
        _pendingTurnEndPayload = payload;

        // Gửi HỎNG không có nghĩa là lượt hỏng -- vào chờ như thường.
        //
        // Toàn bộ bộ máy cứu lượt sau khi nối lại đã nằm sẵn ngay trên: payload vừa được cất, TCS
        // vừa được gán, và ResendTurnEndIfServerNeverGotItAsync sẽ gửi lại đúng nó ngay khi
        // ResumeAfterReconnectAsync chạy xong phần chép ngược audio. Nhưng cả bộ máy đó chỉ với tới
        // được nếu luồng này đi tiếp xuống `await tcs.Task`. Ngoại lệ ở dòng gửi làm nó nhảy qua
        // hết, và đường cứu chưa bao giờ có cơ hội chạy.
        //
        // Hậu quả không phải là mất một lượt, mà là mất CẢ CÂU: ngoại lệ bay lên
        // QuestionFlowRunner, bị bắt bởi nhánh dựng quyết định thay thế "connection_lost_timeout"
        // với ShouldContinue=false, và câu hỏi bị đánh dấu đã xong với 0 lượt. Bài đi tiếp câu sau.
        //
        // Đo thật 2026-08-26, ca 01a03d85: đứt mạng lúc đọc lời dẫn chuẩn bị của câu 2. 17:04:53
        // ObjectDisposedException tại đúng dòng này -> câu 2 chết ngay giây đó; 16 giây sau máy đã
        // sang câu 3; tới lúc nối lại được (17:05:09) thì không còn gì để cứu. DB: câu 2, 3, 4 đều
        // no_recording. Nếu luồng vào được chỗ chờ thì nối lại ở giây thứ 30 vẫn còn thừa thời gian
        // -- trần chờ dưới đây là 180 giây.
        //
        // KHÔNG nuốt OperationCanceledException: nộp bài/dừng bài đi qua đường đó và phải thoát ngay.
        try
        {
            await SendJsonAsync(payload, ct);
        }
        catch (Exception ex) when (IsTransportDown(ex))
        {
            LocalFileLogger.Error(
                "realtime_ws",
                "turn_end_gui_that_bai_van_cho_noi_lai",
                ex,
                new { turnOrder });
        }

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
            _pendingTurnEndPayload = null;
        }
    }

    public async Task SendAudioFrameAsync(byte[] pcm)
    {
        // Đang chép ngược bộ đệm lượt lên server thì bỏ khung trực tiếp: chúng đã nằm trong bộ đệm
        // của TurnAudioRecorder rồi, và vòng lặp đuổi bắt trong ResyncTurnAudioAsync sẽ gửi nốt.
        // Không chặn ở đây thì khung trực tiếp chen vào giữa phần chép ngược, audio bị đảo thứ tự.
        if (_audioResyncInProgress)
        {
            return;
        }

        await SendAudioFrameCoreAsync(pcm);
    }

    /// <summary>Gửi thật, KHÔNG qua cổng chặn resync -- chính vòng resync dùng đường này.</summary>
    private async Task SendAudioFrameCoreAsync(byte[] pcm)
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

    /// <summary>
    /// Lỗi "đường truyền đã chết", phân biệt với lỗi giao thức hay lỗi lập trình.
    ///
    /// <para><c>ObjectDisposedException</c> kế thừa <c>InvalidOperationException</c> nên đã nằm
    /// trong nhánh đầu -- liệt kê riêng chỉ để người đọc sau không tưởng là thiếu. Đó cũng là dạng
    /// hay gặp nhất: vòng nối lại đã <c>Dispose</c> socket cũ trước khi luồng gửi kịp chạm vào nó.</para>
    /// </summary>
    private static bool IsTransportDown(Exception ex) =>
        ex is InvalidOperationException
            or ObjectDisposedException
            or WebSocketException
            or System.IO.IOException;

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

                MarkServerAlive();
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
                // TẠM THỜI, THÊM 2026-08-26 ĐỂ CHẨN ĐOÁN -- xoá khi xong.
                //
                // Lượt nói chỉ kết thúc khi vad_speech_end tới đây. Đo thật cùng ngày: sau khi nối
                // lại giữa lúc thí sinh đang nói, cửa sổ thu tiếng KHÔNG BAO GIỜ tự đóng -- chạy
                // 96 giây tới lúc thí sinh bấm nộp mới kết thúc qua đường salvage. Không biết được
                // vad_speech_end có tới hay không vì hai sự kiện này chưa từng được ghi log, nên
                // không phân biệt nổi "server không gửi" với "client nhận mà không xử lý".
                //
                // Ghi cả hai chiều để lần sau đối chiếu được với mốc turn_capture_began/completed.
                case "vad_speech_start":
                    LocalFileLogger.Info("realtime_ws", "vad_speech_start", null);
                    OnVadSpeechStart?.Invoke();
                    break;
                case "vad_speech_end":
                    LocalFileLogger.Info("realtime_ws", "vad_speech_end", null);
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
                    _intentionalClose = true;
                    var reason = GetPropertyText(doc, "reason");
                    LocalFileLogger.Info("realtime_ws", "force_end_received", new { reason });
                    _pendingDecisionTcs?.TrySetCanceled();
                    _pendingExamEndAckTcs?.TrySetCanceled();
                    _pendingResumeAckTcs?.TrySetCanceled();
                    OnForceEnded?.Invoke(reason);
                    break;
                case "resume_ack":
                    // Thiếu trường -> null ("server không nói"), KHÔNG phải -1. Xem
                    // SendResumeAndAwaitAckAsync: bất kỳ con số nào đại diện cho "không biết" cũng
                    // lọt qua chốt chặn gửi lại turn_end. Đây là đường thứ hai sinh ra cùng một giá
                    // trị -1 đó -- một server bản cũ, hoặc một ack thiếu trường, là đủ.
                    int? lastArchivedTurnOrder = doc.RootElement.TryGetProperty("last_archived_turn_order", out var lto)
                        && lto.ValueKind == JsonValueKind.Number
                            ? lto.GetInt32()
                            : null;
                    _pendingResumeAckTcs?.TrySetResult(lastArchivedTurnOrder);
                    LocalFileLogger.Info("realtime_ws", "resume_ack_received", new { lastArchivedTurnOrder });

                    // Recovery: if this attempt reconnected while SendTurnEndAndWaitAsync was still
                    // waiting on a decision that never arrived, Python may have finished computing
                    // it anyway and persisted it durably before the WS reply was lost --
                    // recovered_turn_order/decision being present and matching what we're still
                    // waiting on means exactly that. Resolve the pending TCS directly instead of
                    // leaving it to sit until SendTurnEndAndWaitAsync's own timeout gives up.
                    if (doc.RootElement.TryGetProperty("recovered_turn_order", out var rtoSeen))
                    {
                        // Ghi nhận DÙ có khớp lượt đang chờ hay không: đây là bằng chứng server đã
                        // xử lý lượt đó, tức tuyệt đối không được gửi lại turn_end cho nó.
                        _recoveredTurnOrder = rtoSeen.GetInt32();
                    }

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
                case "ping":
                    // Nhịp tim từ server, 5 giây một lần. KHÔNG ghi log: giá trị của nó đã được
                    // MarkServerAlive() ở vòng nhận thu nhận trước khi vào đây, còn ghi lại thì
                    // một bài thi 25 phút sinh ra 300 dòng rác trong đúng cái log dùng để chẩn
                    // đoán sự cố.
                    //
                    // Nhịp đầu tiên bật luật im lặng lên -- xem IsServerAlive.
                    _serverHeartbeatSeen = true;
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
