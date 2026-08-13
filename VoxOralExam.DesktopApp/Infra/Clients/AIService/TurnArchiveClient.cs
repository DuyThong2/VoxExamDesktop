using System.Net.Http;
using System.Text;
using System.Text.Json;
using VoxOralExam.DesktopApp.Dtos;
using VoxOralExam.DesktopApp.State;

using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Infra.Clients.AIService;

/// <summary>
/// Extracted from the former TavusFullPipelineExamFlowService.ArchiveTurnAsync: POSTs a turn's
/// archive payload to the Python backend's /turns/archive endpoint, same multipart shape and
/// same EvaluateTurnRequest DTO as before.
/// </summary>
public class TurnArchiveClient
{
    private readonly AppSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;

    public TurnArchiveClient(AppSettings settings, IHttpClientFactory httpClientFactory)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<bool> ArchiveTurnAsync(EvaluateTurnRequest request, CancellationToken ct)
    {
        LocalFileLogger.Info("archive", "archive_turn_begin", new
        {
            request.AnswerId,
            request.TurnOrder,
            request.PromptText,
            request.AudioRef,
            questionText = request.Question.QuestionText,
            questionType = request.Question.QuestionType,
            difficultyLevel = request.Question.DifficultyLevel
        });

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_settings.PythonBaseUrl.TrimEnd('/')}/turns/archive");

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(request.AudioRef), "audio_ref");
        content.Add(new StringContent(request.AnswerId.ToString()), "answer_id");
        if (request.PaperItemId.HasValue)
        {
            content.Add(new StringContent(request.PaperItemId.Value.ToString()), "paper_item_id");
        }
        content.Add(new StringContent(request.TurnOrder.ToString()), "turn_order");
        content.Add(new StringContent(request.PromptText), "prompt_text");
        content.Add(new StringContent(request.Language), "language");
        if (request.DurationSeconds.HasValue)
        {
            content.Add(new StringContent(request.DurationSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)), "duration_seconds");
        }
        content.Add(new StringContent(JsonSerializer.Serialize(request.Question), Encoding.UTF8), "question");

        httpRequest.Content = content;

        var client = _httpClientFactory.CreateClient();

        // Trần riêng cho lời gọi này, thay vì để nguyên mặc định 100 giây của HttpClient.
        // Dùng CTS ghép chứ không gán client.Timeout: client lấy từ factory có thể dùng chung,
        // và ghép token giữ nguyên đường huỷ theo vòng đời phiên thi (ct) -- ArchiveAsync phân
        // biệt được "huỷ vì thoát bài" với "hết giờ" nhờ _lifetime.IsCancellationRequested.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings.ArchiveRequestTimeoutSeconds)));

        using var response = await client.SendAsync(httpRequest, timeout.Token);
        response.EnsureSuccessStatusCode();
        LocalFileLogger.Info("archive", "archive_turn_complete", new
        {
            request.AnswerId,
            request.TurnOrder,
            statusCode = (int)response.StatusCode,
            questionText = request.Question.QuestionText
        });
        return true;
    }
}

