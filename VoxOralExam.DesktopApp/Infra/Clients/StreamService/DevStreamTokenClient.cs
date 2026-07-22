using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace VoxOralExam.DesktopApp.Infra.Clients.StreamService;

public sealed record DevStreamTokenAccess(
    string Token,
    string ScheduleId,
    string SessionId,
    IReadOnlyList<string> StreamTypes,
    DateTimeOffset ExpiresAt
);

/// <summary>
/// Dev-only alternative to <see cref="StudentStreamAccessClient"/>: mints a real, signed
/// vox-streaming JWT from vox-streaming/demo/devserver's <c>/token</c> endpoint instead of asking
/// the real Java backend. Only ever used when AppSettings.UseDevStreamToken is true (see that
/// setting's doc comment) so the client-side recording/upload pipeline can be exercised against a
/// real, locally-running vox-streaming instance while exam content stays mocked.
/// </summary>
public sealed class DevStreamTokenClient
{
    private readonly HttpClient _http;

    public DevStreamTokenClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<DevStreamTokenAccess> IssueAsync(
        string scheduleId,
        string sessionId,
        string candidateId,
        IReadOnlyList<string> streamTypes,
        TimeSpan ttl,
        CancellationToken ct)
    {
        var streamTypesParam = string.Join(',', streamTypes.Count == 0 ? ["camera", "screen"] : streamTypes);
        var query = string.Join('&',
            "role=student",
            $"scheduleId={Uri.EscapeDataString(scheduleId)}",
            $"sessionId={Uri.EscapeDataString(sessionId)}",
            $"userId={Uri.EscapeDataString(candidateId)}",
            $"streamTypes={Uri.EscapeDataString(streamTypesParam)}",
            $"ttl={(int)ttl.TotalSeconds}s");

        using var response = await _http.GetAsync($"/token?{query}", ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<DevTokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("devserver /token returned an empty response.");

        if (string.IsNullOrWhiteSpace(payload.Token))
        {
            throw new InvalidOperationException("devserver /token response is missing the token field.");
        }

        return new DevStreamTokenAccess(
            payload.Token,
            payload.ScheduleId ?? scheduleId,
            payload.SessionId ?? sessionId,
            payload.StreamTypes is { Count: > 0 } ? payload.StreamTypes : streamTypes,
            DateTimeOffset.UtcNow.Add(ttl));
    }

    // devserver (vox-streaming/demo/devserver/main.go) replies with lowercase camelCase JSON keys;
    // JsonPropertyName pins the mapping explicitly rather than relying on ambient case-insensitive
    // deserialization defaults.
    private sealed record DevTokenResponse(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("scheduleId")] string? ScheduleId,
        [property: JsonPropertyName("sessionId")] string? SessionId,
        [property: JsonPropertyName("streamTypes")] IReadOnlyList<string>? StreamTypes
    );
}
