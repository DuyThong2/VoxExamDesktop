using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace VoxOralExam.DesktopApp.Infra.Clients.StreamService;

public sealed record StreamUploadSession(
    string StreamId,
    string StreamType,
    DateTimeOffset ExpiresAt,
    string UploadToken
);

public sealed class StreamSessionClient
{
    private readonly HttpClient _http;

    public StreamSessionClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<StreamUploadSession> CreateAsync(string streamType, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/stream/sessions"
        );

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            streamType
        });

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<StreamUploadSession>(cancellationToken: ct) ?? throw new InvalidOperationException("Streaming service return an empty session.");
    }

    public async Task CompleteAsync(string streamId, string uploadToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/stream/sessions/{Uri.EscapeDataString(streamId)}/complete"
        );

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", uploadToken);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
