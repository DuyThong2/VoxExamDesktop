using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using VoxOralExam.DesktopApp.Dtos.Responses;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Infra.Clients.StreamService;

public sealed record StudentStreamAccess(
    string Token, 
    string ScheduleId, 
    string SessionId, 
    IReadOnlyList<string> StreamTypes, 
    DateTimeOffset ExpiresAt
);

public sealed class StudentStreamAccessClient
{
    private readonly HttpClient _http;
    private readonly ExamSessionState _state;

    public StudentStreamAccessClient(HttpClient http, ExamSessionState state)
    {
        _http = http;
        _state = state;
    }

    public async Task<StudentStreamAccess> IssueAsync(Guid examSessionId, string? preferredStreamType, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/streams/student/token");

        var accessToken = _state.CurrentUser?.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("A student access token is required before stream access can be issued.");
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        request.Content = JsonContent.Create(new
        {
            examSessionId, 
            streamType = preferredStreamType
        });

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<ApiResponse<StudentStreamAccess>>(cancellationToken: ct);

        return envelope?.Data ?? throw new InvalidOperationException("Stream access response is empty.");
    }
}
