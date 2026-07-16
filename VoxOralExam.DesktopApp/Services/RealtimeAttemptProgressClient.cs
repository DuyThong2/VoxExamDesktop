using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services;

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

    private sealed class CurrentAnswerResponse
    {
        [JsonPropertyName("answer_id")]
        public string? AnswerId { get; set; }
    }
}
