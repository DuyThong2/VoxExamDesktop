using VoxOralExam.Core.Context;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VoxOralExam.DesktopApp.Services.DomainService;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services.DomainService.Impl;

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
        return await SendTicketRequestAsync(request, ct);
    }

    public async Task<ExamEntryTicket> StartClassTestAsync(Guid examId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/exams/{examId:D}/class-test/start");
        return await SendTicketRequestAsync(request, ct);
    }

    private async Task<ExamEntryTicket> SendTicketRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var accessToken = _sessionState.CurrentUser?.AccessToken;
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            ErrorPayload? error = null;
            if (response.Content.Headers.ContentLength is > 0)
            {
                error = await response.Content.ReadFromJsonAsync<ErrorPayload>(JsonOptions, ct);
            }
            throw new OtpVerificationException(error?.Message ?? "Mã OTP không đúng hoặc đã hết hạn.");
        }

        if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
        {
            ErrorPayload? error = null;
            if (response.Content.Headers.ContentLength is > 0)
            {
                error = await response.Content.ReadFromJsonAsync<ErrorPayload>(JsonOptions, ct);
            }
            throw new ExamEntryRejectedException(error?.Message ?? "Bạn chưa đủ điều kiện vào thi.");
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ApiResponse<ExamEntryTicket>>(JsonOptions, ct);
        return payload?.Data
            ?? throw new InvalidOperationException("Phản hồi vào thi không chứa entry ticket.");
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

