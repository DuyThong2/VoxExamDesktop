using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoxOralExam.DesktopApp.Models;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services;

/// <summary>
/// Real <see cref="IExamApiService"/> that fetches exams/papers from the Java backend, authenticated
/// with the current user's access token. Selected when AppSettings.UseMockData is false.
///
/// The exact JSON contract is a flagged cross-repo dependency (docs/wpf-redesign-plan.md §F): Java
/// has no exam-list/exam-paper endpoints yet, so with UseMockData=false these calls fail loudly
/// (404) until Java exposes:
///   GET /api/v1/exams                 -> Exam[]        (camelCase, matches Models.Exam)
///   GET /api/v1/exams/{examId}/paper  -> ExamPaper     (camelCase, matches Models.ExamPaper graph)
/// This is intentional: failing loudly beats silently serving mock data in production.
/// </summary>
public class ExamApiService : IExamApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppSettings _settings;
    private readonly ExamSessionState _sessionState;

    public ExamApiService(IHttpClientFactory httpClientFactory, AppSettings settings, ExamSessionState sessionState)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _sessionState = sessionState;
    }

    public async Task<IReadOnlyList<Exam>> GetAvailableExamsAsync(CancellationToken ct = default)
    {
        using var request = BuildRequest(HttpMethod.Get, "/api/v1/exams");
        var exams = await SendAsync<List<Exam>>(request, ct);
        return exams ?? [];
    }

    public async Task<ExamPaper> GetExamPaperAsync(string? examId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(examId))
        {
            throw new ArgumentException("examId is required to fetch a real exam paper.", nameof(examId));
        }

        using var request = BuildRequest(HttpMethod.Get, $"/api/v1/exams/{Uri.EscapeDataString(examId)}/paper");
        return await SendAsync<ExamPaper>(request, ct)
            ?? throw new InvalidOperationException($"Exam paper response for {examId} was empty.");
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path)
    {
        var uri = $"{_settings.JavaBaseUrl.TrimEnd('/')}{path}";
        var request = new HttpRequestMessage(method, uri);
        var accessToken = _sessionState.CurrentUser?.AccessToken;
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        return request;
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }
}
