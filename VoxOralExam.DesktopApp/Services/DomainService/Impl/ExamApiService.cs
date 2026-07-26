using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoxOralExam.Core.Models;
using VoxOralExam.DesktopApp.Services.DomainService;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services.DomainService.Impl;

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

    public async Task<ExamPaper> GetExamPaperAsync(string? sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("sessionId is required to fetch a real exam paper.", nameof(sessionId));
        }

        using var request = BuildRequest(HttpMethod.Get, $"/api/v1/exam-sessions/{Uri.EscapeDataString(sessionId)}/paper");
        return await SendAsync<ExamPaper>(request, ct)
            ?? throw new InvalidOperationException($"Exam paper response for session {sessionId} was empty.");
    }

    public async Task UpdateSessionStatusAsync(Guid sessionId, string status, CancellationToken ct = default)
    {
        using var request = BuildRequest(HttpMethod.Patch, $"/api/v1/exam-sessions/{sessionId:D}");
        request.Content = JsonContent.Create(new { status });
        await SendAsync<object>(request, ct);
    }

    public async Task UpdateRemainingTimeAsync(
        Guid sessionId,
        int remainingSeconds,
        CancellationToken ct = default)
    {
        using var request = BuildRequest(
            HttpMethod.Patch,
            $"/api/v1/exam-sessions/{sessionId:D}/remaining-time");
        request.Content = JsonContent.Create(new { remainingSeconds });
        await SendAsync<object>(request, ct);
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
        where T : class
    {
        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<T>>(JsonOptions, ct);
        return envelope?.Data;
    }

    private sealed class ApiResponse<T>
        where T : class
    {
        public T? Data { get; set; }
    }
}

