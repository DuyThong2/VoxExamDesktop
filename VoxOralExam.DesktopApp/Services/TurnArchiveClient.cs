using System.Net.Http;
using System.Text;
using System.Text.Json;
using VoxOralExam.Core.Dtos;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services;

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
        content.Add(new StringContent(JsonSerializer.Serialize(request.Question), Encoding.UTF8), "question");

        httpRequest.Content = content;

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(httpRequest, ct);
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
