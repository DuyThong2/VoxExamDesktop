using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using VoxOralExam.DesktopApp.State;

using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Infra.Clients.AIService;

public sealed record AttemptResumeState(
    int TurnOrder,
    string? ActivePromptText,
    bool HasFollowUp,
    Guid? PaperItemId,
    double ElapsedSpeechSeconds,
    /// <summary>
    /// Mốc đồng hồ lúc câu hỏi này bắt đầu; null nếu server chưa ghi (client cũ) hoặc câu đã có
    /// lượt hoàn thành. Chỉ có ở nhánh CHƯA lượt nào xong -- đúng lúc vào lại sẽ chạy lại cả media
    /// lẫn thời gian chuẩn bị, nên phần đã tiêu ở lần vào trước phải được hoàn.
    /// </summary>
    int? RemainingSecondsAtQuestionStart);

/// <summary>
/// GET /realtime/attempts/{examAttemptId}/current-answer (agents/src/controller/realtime_controller.py)
/// -- lets ExamSessionBootstrapService find out which question an attempt was last on, for a
/// client that lost all local state (app fully closed, not just a WS reconnect within the same
/// process). Backed by Python's own durable per-attempt state
/// (turn_publisher.set_current_answer_id, written unconditionally at every question_start), not
/// Kafka's answer-turns-recorded topic -- that topic only fires after a turn completes, so it
/// would stay silent for a question that was started but never got that far. See
/// task/realtime-exam-flow-review.md.
/// </summary>
public class RealtimeAttemptProgressClient
{
    private readonly AppSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;

    public RealtimeAttemptProgressClient(AppSettings settings, IHttpClientFactory httpClientFactory)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Null if this attempt never started any question yet (a genuinely fresh attempt),
    /// or if the lookup itself failed (Python unreachable) -- callers should treat both the same
    /// way: fall back to starting at question index 0, same as before this client existed.</summary>
    public async Task<Guid?> GetCurrentAnswerIdAsync(Guid examAttemptId, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = $"{_settings.PythonBaseUrl.TrimEnd('/')}/realtime/attempts/{examAttemptId:D}/current-answer";
            var result = await client.GetFromJsonAsync<CurrentAnswerResponse>(url, ct);
            return result?.AnswerId is string raw && Guid.TryParse(raw, out var answerId) ? answerId : null;
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_bootstrap", "get_current_answer_failed", ex, new { examAttemptId });
            return null;
        }
    }

    /// <summary>
    /// Còn bao nhiêu lượt đang được Python lưu (upload S3 + phiên âm Azure) cho bài thi này.
    ///
    /// <para>Thay cho cửa chờ cũ nằm ngay trong app (TurnArchiveQueue.DrainAsync): từ khi audio
    /// do Python lưu, hàng đợi bên này luôn rỗng nên chờ ở đó là chờ hư không.</para>
    ///
    /// <para>Trả 0 khi gọi hỏng -- KHÔNG được chặn việc nộp bài chỉ vì không hỏi được. Không biết
    /// thì đi tiếp, đúng như hành vi trước khi có endpoint này.</para>
    /// </summary>
    public async Task<int> GetPendingArchiveCountAsync(Guid examAttemptId, CancellationToken ct)
    {
        var status = await GetArchiveStatusAsync(examAttemptId, null, null, ct);
        return status.Pending;
    }

    /// <summary>
    /// Như trên, nhưng hỏi thêm ĐÍCH DANH một lượt đã lưu tr��� xong chưa.
    ///
    /// <para>Vì sao cần hỏi đích danh: <c>pending</c> một mình không phân biệt được "đã lưu xong"
    /// với "chưa kịp bắt đầu" -- cả hai đều bằng 0. Lượt CUỐI luôn rơi vào vế sau, vì gửi
    /// <c>turn_end</c> xong là hỏi ngay, không có khoảng đệm như các câu giữa bài (ở đó còn cả
    /// đoạn AI đọc câu kế tiếp). Client thấy 0, tưởng sạch, nộp bài -- Java chấm đồng bộ ngay tại
    /// lần PATCH ấy, và câu cuối mất phần chấm phát âm vì <c>audio_url</c> còn rỗng.</para>
    ///
    /// <para><c>Archived</c> là <c>null</c> khi server không trả lời được hoặc là bản CŨ chưa hỗ
    /// trợ -- người gọi phải hiểu đó là "không biết" và rơi về cách đếm cũ, tuyệt đối không coi là
    /// "chưa lưu" rồi giữ thí sinh ở màn đang lưu.</para>
    /// </summary>
    public async Task<(int Pending, bool? Archived)> GetArchiveStatusAsync(
        Guid examAttemptId,
        Guid? answerId,
        int? turnOrder,
        CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var url = $"{_settings.PythonBaseUrl.TrimEnd('/')}/realtime/attempts/{examAttemptId:D}/pending-archives";
            if (answerId is Guid id && turnOrder is int order)
            {
                url += $"?answer_id={id:D}&turn_order={order}";
            }
            var result = await client.GetFromJsonAsync<PendingArchivesResponse>(url, ct);
            return (result?.Pending ?? 0, result?.Archived);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("exam_flow", "get_pending_archives_failed", ex, new { examAttemptId });
            // 0 + "không biết": giữ đúng hành vi cũ là không chặn nộp bài khi không hỏi được.
            return (0, null);
        }
    }

    public async Task<AttemptResumeState?> GetResumeStateAsync(
        Guid examAttemptId,
        Guid answerId,
        CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            var url =
                $"{_settings.PythonBaseUrl.TrimEnd('/')}/realtime/attempts/{examAttemptId:D}/resume-state"
                + $"?answer_id={Uri.EscapeDataString(answerId.ToString("D"))}";
            var result = await client.GetFromJsonAsync<ResumeStateResponse>(url, ct);
            if (result is null)
            {
                return null;
            }

            return new AttemptResumeState(
                Math.Max(1, result.TurnOrder),
                result.ActivePromptText,
                result.HasFollowUp,
                result.PaperItemId is string rawPaperItemId
                    && Guid.TryParse(rawPaperItemId, out var paperItemId)
                        ? paperItemId
                        : null,
                Math.Max(0, result.ElapsedSpeechSeconds),
                result.RemainingSecondsAtQuestionStart);
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error(
                "exam_bootstrap",
                "get_resume_state_failed",
                ex,
                new { examAttemptId, answerId });
            return null;
        }
    }

    private sealed class CurrentAnswerResponse
    {
        [JsonPropertyName("answer_id")]
        public string? AnswerId { get; set; }
    }

    private sealed class PendingArchivesResponse
    {
        [JsonPropertyName("pending")]
        public int Pending { get; set; }

        /// <summary>Null = server không trả trường này (bản cũ, hoặc đọc lỗi) -- nghĩa là KHÔNG BIẾT.</summary>
        [JsonPropertyName("archived")]
        public bool? Archived { get; set; }
    }

    private sealed class ResumeStateResponse
    {
        [JsonPropertyName("turnOrder")]
        public int TurnOrder { get; set; }

        [JsonPropertyName("activePromptText")]
        public string? ActivePromptText { get; set; }

        [JsonPropertyName("hasFollowUp")]
        public bool HasFollowUp { get; set; }

        [JsonPropertyName("paperItemId")]
        public string? PaperItemId { get; set; }

        [JsonPropertyName("elapsedSpeechSeconds")]
        public double ElapsedSpeechSeconds { get; set; }

        [JsonPropertyName("remainingSecondsAtQuestionStart")]
        public int? RemainingSecondsAtQuestionStart { get; set; }
    }
}

