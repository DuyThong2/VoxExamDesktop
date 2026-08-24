using VoxOralExam.Core.Context;
using VoxOralExam.Core.Interfaces;
using VoxOralExam.DesktopApp.State;

using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Infra.Clients.StreamService;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.Services.DomainService;

namespace VoxOralExam.DesktopApp.Services.ExamFlow.Impl;

public class ExamSessionBootstrapService : IExamSessionBootstrapService
{
    private readonly IExamApiService _examApi;
    private readonly ExamSessionState _sessionState;
    private readonly RealtimeAttemptProgressClient _attemptProgressClient;
    private readonly StudentStreamAccessClient _streamAccessClient;
    private readonly DevStreamTokenClient _devStreamTokenClient;
    private readonly AppSettings _settings;
    private readonly IQuestionAssetCache _assetCache;

    public ExamSessionBootstrapService(
        IExamApiService examApi,
        ExamSessionState sessionState,
        RealtimeAttemptProgressClient attemptProgressClient,
        StudentStreamAccessClient streamAccessClient,
        DevStreamTokenClient devStreamTokenClient,
        AppSettings settings,
        IQuestionAssetCache assetCache)
    {
        _assetCache = assetCache;
        _examApi = examApi;
        _sessionState = sessionState;
        _attemptProgressClient = attemptProgressClient;
        _streamAccessClient = streamAccessClient;
        _devStreamTokenClient = devStreamTokenClient;
        _settings = settings;
    }

    public async Task EnterWithTicketAsync(ExamEntryTicket ticket, CancellationToken ct = default)
    {
        _sessionState.EntryTicket = ticket;
        _sessionState.ExamAttemptId = ticket.AttemptId;
        ApplySessionIdentity(ticket);
        var paper = await _examApi.GetExamPaperAsync(ticket.AttemptId.ToString(), ct);
        _sessionState.LoadExamPaper(paper, ticket.AttemptId);
        StartAssetPrefetchInBackground();
        await ResumeQuestionIndexIfNeededAsync(ct);
    }

    /// <summary>
    /// Bắt đầu tải tài nguyên ngay khi vừa có đề, chạy nền trong lúc học sinh kiểm tra camera/mic.
    ///
    /// <para>KHÔNG chờ ở đây: chờ là chặn đường sang màn kiểm tra thiết bị, biến một việc vốn ẩn
    /// sau thao tác của học sinh thành một màn hình đứng im. Cổng chờ-cho-xong nằm ở
    /// <c>DevicePreflightViewModel.EnterExamAsync</c>, ngay trước lúc vào phòng thi -- tới đó thì
    /// phần lớn tệp đã tải xong nhờ lượt nền này.</para>
    ///
    /// <para>Nuốt mọi lỗi: lượt nền chỉ để làm ấm đệm. Tải hỏng thì cổng kia tải lại và báo lỗi
    /// đàng hoàng, chứ một ngoại lệ không ai bắt ở đây sẽ giết tiến trình.</para>
    /// </summary>
    private void StartAssetPrefetchInBackground()
    {
        var assets = _sessionState.Questions
            .Select(question => question.Asset)
            .Where(asset => asset is not null)
            .Select(asset => asset!)
            .ToList();

        if (assets.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _assetCache.PrefetchAsync(assets);
            }
            catch (Exception ex)
            {
                LocalFileLogger.Error("exam_bootstrap", "asset_prefetch_background_failed", ex);
            }
        });
    }

    public async Task IssueStreamAccessAsync(string? preferredStreamType, CancellationToken ct = default)
    {
        var ticket = _sessionState.EntryTicket
            ?? throw new InvalidOperationException("Chưa có vé vào thi để xin quyền truy cập stream.");

        if (!ticket.IsMonitored)
        {
            // The exam was created with monitoring off. Asking for a stream token here is not just
            // pointless, it is fatal: the server rejects the request and the student never gets in.
            LocalFileLogger.Info("exam_bootstrap", "stream_token_skipped_exam_not_monitored", new
            {
                ticket.AttemptId
            });
            return;
        }

        if (!_settings.UseMockData)
        {
            var access = await _streamAccessClient.IssueAsync(ticket.AttemptId, preferredStreamType, ct);
            ApplyStreamAccess(ticket, access.Token, access.ScheduleId, access.SessionId, access.StreamTypes, access.ExpiresAt);
        }
        else if (_settings.UseDevStreamToken)
        {
            // Mock exam content, but a real signed JWT from vox-streaming/demo/devserver -- see
            // AppSettings.UseDevStreamToken's doc comment. Reuses the mock ticket's own
            // schedule/session/attempt ids so LocalSegmentStore's identity check (and a resumed
            // run) stay consistent across the whole flow.
            var access = await _devStreamTokenClient.IssueAsync(
                ticket.ScheduleId,
                ticket.SessionId,
                ticket.AttemptId.ToString("D"),
                // Lựa chọn của học viên cũng phải tới được devserver, nếu không nhánh mock sẽ ghi
                // khác nhánh thật và bug chỉ lộ ra khi chạy production.
                ResolveDevStreamTypes(ticket, preferredStreamType),
                TimeSpan.FromHours(2),
                ct);
            ApplyStreamAccess(ticket, access.Token, access.ScheduleId, access.SessionId, access.StreamTypes, access.ExpiresAt);
        }

        LocalFileLogger.Info("exam_bootstrap", "stream_access_issued", new
        {
            ticket.AttemptId,
            preferredStreamType,
            granted = ticket.StreamTypes
        });
    }

    private static IReadOnlyList<string> ResolveDevStreamTypes(
        ExamEntryTicket ticket,
        string? preferredStreamType) => preferredStreamType switch
        {
            "CAMERA" => ["camera"],
            "SCREEN" => ["screen"],
            "CAMERA_AND_SCREEN" => ["camera", "screen"],
            _ => ticket.StreamTypes
        };

    private void ApplyStreamAccess(
        ExamEntryTicket ticket,
        string token,
        string scheduleId,
        string sessionId,
        IReadOnlyList<string> streamTypes,
        DateTimeOffset expiresAt)
    {
        ticket.StreamJwt = token;
        ticket.ScheduleId = scheduleId;
        ticket.SessionId = sessionId;
        ticket.StreamTypes = streamTypes;
        ticket.StreamTokenExpiresAt = expiresAt;
        // Bắt buộc: scheduleId/sessionId chỉ TỒN TẠI trong phản hồi token -- vé vào thi không mang
        // chúng. Từ khi bước này bị dời ra sau EnterWithTicketAsync, không đồng bộ lại ở đây nghĩa
        // là ExamSessionState giữ mãi giá trị tạm đặt lúc nhận vé.
        ApplySessionIdentity(ticket);
    }

    private void ApplySessionIdentity(ExamEntryTicket ticket)
    {
        _sessionState.SessionId = string.IsNullOrWhiteSpace(ticket.SessionId)
            ? ticket.AttemptId.ToString("D")
            : ticket.SessionId;
        _sessionState.ScheduleId = ticket.ScheduleId;
    }

    /// <summary>
    /// LoadExamPaper always resets QuestionIndex to 0 -- correct for a brand-new attempt, wrong
    /// for re-entering one that already started (app fully closed and reopened, not just a WS
    /// reconnect within the same process, which RealtimeExamFlowService/RealtimeSessionClient
    /// already resume correctly on their own). Asks Python which answer_id this attempt was last
    /// on and, if found, maps it back to a question index via AttemptAnswerIdsByQuestionId so the
    /// exam flow starts at the right question instead of silently restarting from the first one.
    /// No-op (stays at index 0) if this is a genuinely fresh attempt, the lookup fails, or the
    /// returned answer_id doesn't match any question in this paper (defensive -- should not
    /// happen, but a bad resume-into-wrong-question is worse than just restarting from 0).
    /// </summary>
    private async Task ResumeQuestionIndexIfNeededAsync(CancellationToken ct)
    {
        _sessionState.ResumeTurnOrder = null;
        _sessionState.ResumeActivePromptText = null;
        _sessionState.ResumeSpokenSeconds = 0;

        var currentAnswerId = await _attemptProgressClient.GetCurrentAnswerIdAsync(_sessionState.ExamAttemptId, ct);
        if (currentAnswerId is null)
        {
            return;
        }

        var resumeState = await _attemptProgressClient.GetResumeStateAsync(
            _sessionState.ExamAttemptId,
            currentAnswerId.Value,
            ct);
        var matchingQuestionId = _sessionState.AttemptAnswerIdsByQuestionId
            .Where(pair => pair.Value == currentAnswerId.Value)
            .Select(pair => (Guid?)pair.Key)
            .FirstOrDefault();
        if (matchingQuestionId is null && resumeState?.PaperItemId is Guid paperItemId)
        {
            matchingQuestionId = _sessionState.PaperItemIdsByQuestionId
                .Where(pair => pair.Value == paperItemId)
                .Select(pair => (Guid?)pair.Key)
                .FirstOrDefault();
        }
        if (matchingQuestionId is null)
        {
            LocalFileLogger.Info("exam_bootstrap", "current_answer_not_in_paper", new
            {
                _sessionState.ExamAttemptId,
                currentAnswerId
            });
            return;
        }

        var index = _sessionState.Questions.FindIndex(q => q.Id == matchingQuestionId.Value);
        if (index < 0)
        {
            return;
        }

        LocalFileLogger.Info("exam_bootstrap", "resuming_at_question_index", new
        {
            _sessionState.ExamAttemptId,
            currentAnswerId,
            questionIndex = index
        });
        _sessionState.QuestionIndex = index;
        _sessionState.AttemptAnswerIdsByQuestionId[matchingQuestionId.Value] = currentAnswerId.Value;
        _sessionState.ResumeSpokenSeconds =
            Math.Max(0, resumeState?.ElapsedSpeechSeconds ?? 0);

        // HOÀN GIỜ khi bị ngắt giữa câu mà chưa trả lời lượt nào: vào lại thì media phát LẠI TỪ ĐẦU
        // và thời gian chuẩn bị cũng chạy lại (QuestionFlowRunner: PresentInitialAsync rồi
        // RunPreparationAsync), nên phần đã tiêu ở lần vào trước phải trả lại -- không trả thì thí
        // sinh mất giờ hai lần cho cùng một đoạn băng và cùng một khoảng chuẩn bị.
        //
        // Server chỉ trả mốc này ở nhánh CHƯA có lượt nào xong. Đã có lượt thì vào lại đi nhánh
        // resume: media không phát lại, chuẩn bị không chạy lại, nên không có gì để hoàn và mốc là
        // null.
        //
        // Math.Max chứ không gán đè: mốc luôn ≥ checkpoint hiện tại vì nó được ghi sớm hơn. Lấy max
        // để một mốc cũ bất thường (client cũ, hoặc dữ liệu lạ) không bao giờ CƯỚP thêm giờ của thí
        // sinh -- xấu nhất là không hoàn, chứ không phải trừ oan.
        if (resumeState?.RemainingSecondsAtQuestionStart is int markedRemaining and > 0)
        {
            var restored = Math.Max(_sessionState.RemainingSeconds ?? 0, markedRemaining);
            LocalFileLogger.Info("exam_bootstrap", "restoring_remaining_time_for_replay", new
            {
                _sessionState.ExamAttemptId,
                currentAnswerId,
                checkpointed = _sessionState.RemainingSeconds,
                markedRemaining,
                restored
            });
            _sessionState.RemainingSeconds = restored;
        }

        if (resumeState?.HasFollowUp == true)
        {
            _sessionState.ResumeTurnOrder = resumeState.TurnOrder;
            _sessionState.ResumeActivePromptText = resumeState.ActivePromptText;
            LocalFileLogger.Info("exam_bootstrap", "resuming_at_follow_up", new
            {
                _sessionState.ExamAttemptId,
                currentAnswerId,
                resumeState.TurnOrder
            });
        }
    }
}

