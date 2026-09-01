using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using VoxOralExam.DesktopApp.Dtos.Responses;
using VoxOralExam.DesktopApp.Services.Auth;

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
    private readonly AuthSessionManager _authSession;

    public StudentStreamAccessClient(HttpClient http, AuthSessionManager authSession)
    {
        _http = http;
        _authSession = authSession;
    }

    public async Task<StudentStreamAccess> IssueAsync(Guid examSessionId, string? preferredStreamType, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/streams/student/token");

        // Through the session manager rather than off ExamSessionState directly: this call is the
        // first step of renewing an upload credential (UploadCredentialRefresher), so an access
        // token that expired earlier in the exam used to end with vox-streaming answering 410 Gone
        // and the segments already buffered on disk becoming permanently un-uploadable.
        var accessToken = await _authSession.GetAccessTokenAsync(ct);
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
