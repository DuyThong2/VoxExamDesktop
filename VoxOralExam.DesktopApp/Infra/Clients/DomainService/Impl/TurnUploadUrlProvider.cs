using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VoxOralExam.DesktopApp.Infra.Clients.DomainService;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Infra.Clients.DomainService.Impl;

/// <summary>
/// Default <see cref="ITurnUploadUrlProvider"/>: requests a presigned turn-audio upload target from
/// the Java backend, authenticated with the current user's access token. The exact endpoint shape
/// is a flagged cross-repo dependency (see docs/wpf-redesign-plan.md §D/§F) -- Java must expose
/// GET /api/v1/exam-turns/upload-url returning { uploadUrl, audioRef }.
/// </summary>
public class TurnUploadUrlProvider : ITurnUploadUrlProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppSettings _settings;
    private readonly ExamSessionState _sessionState;

    public TurnUploadUrlProvider(IHttpClientFactory httpClientFactory, AppSettings settings, ExamSessionState sessionState)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _sessionState = sessionState;
    }

    public async Task<TurnUploadTarget> GetUploadTargetAsync(Guid attemptAnswerId, int turnOrder, CancellationToken ct)
    {
        var baseUrl = _settings.JavaBaseUrl.TrimEnd('/');
        var uri = $"{baseUrl}/api/v1/exam-turns/upload-url?attemptAnswerId={attemptAnswerId:D}&turnOrder={turnOrder}";

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        var accessToken = _sessionState.CurrentUser?.AccessToken;
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        var client = _httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<TurnUploadTargetResponse>>(JsonOptions, ct)
            ?? throw new InvalidOperationException("Upload-url response is empty.");
        var target = envelope.Data;
        if (target is null || string.IsNullOrWhiteSpace(target.UploadUrl) || string.IsNullOrWhiteSpace(target.AudioRef))
        {
            throw new InvalidOperationException("Upload-url response is missing uploadUrl or audioRef.");
        }

        return new TurnUploadTarget(target.UploadUrl, target.AudioRef);
    }

    private sealed class ApiResponse<T>
    {
        public T? Data { get; set; }
    }

    private sealed class TurnUploadTargetResponse
    {
        public string UploadUrl { get; set; } = string.Empty;
        public string AudioRef { get; set; } = string.Empty;
    }
}

