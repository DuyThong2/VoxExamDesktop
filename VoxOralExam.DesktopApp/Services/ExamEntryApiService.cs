using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services;

public class ExamEntryApiService : IExamEntryApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly ExamSessionState _sessionState;

    public ExamEntryApiService(HttpClient http, ExamSessionState sessionState)
    {
        _http = http;
        _sessionState = sessionState;
    }

    public async Task<ExamEntryTicket> VerifyOtpAsync(string examId, string otp, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/exams/{Uri.EscapeDataString(examId)}/otp/verify")
        {
            Content = JsonContent.Create(new { otp })
        };
        var accessToken = _sessionState.CurrentUser?.AccessToken;
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        using var response = await _http.SendAsync(request, ct);

        // Java's VerifyExamScheduleOtpUseCase reports every "can't proceed with this OTP attempt"
        // case (wrong code, not a candidate, no schedule/paper assigned, expired/mismatched
        // schedule) using the codebase's ordinary exceptions (Unauthorized/NotFound/BadRequest)
        // rather than bespoke 410/422 statuses. Surface the server's own message for all of
        // them instead of a generic string -- it's already specific and student-facing.
        //
        // A 401 with no Authorization header at all (e.g. missing/expired access token) comes
        // back from Spring Security itself with an EMPTY body, not the app's JSON error shape --
        // ReadFromJsonAsync on an empty body throws JsonException, so guard on content length
        // before attempting to parse it.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            ErrorPayload? error = null;
            if (response.Content.Headers.ContentLength is > 0)
            {
                error = await response.Content.ReadFromJsonAsync<ErrorPayload>(JsonOptions, ct);
            }
            throw new OtpVerificationException(error?.Message ?? "Mã OTP không đúng hoặc đã hết hạn.");
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ApiResponse<ExamEntryTicket>>(JsonOptions, ct);
        return payload?.Data
            ?? throw new InvalidOperationException("Phản hồi xác thực OTP không chứa entry ticket.");
    }

    private sealed class ApiResponse<T>
    {
        public T? Data { get; set; }
    }

    private sealed class ErrorPayload
    {
        public string? Message { get; set; }
    }
}
