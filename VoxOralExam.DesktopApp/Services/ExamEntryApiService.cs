using System.Net.Http;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services;

/// <summary>
/// Real <see cref="IExamEntryApiService"/> against Java. Skeleton only -- the HTTP call is left for when
/// the backend OTP endpoint exists (docs/wpf-redesign-plan.md §C, §F). Until then, run with
/// AppSettings.UseMockData = true so <see cref="MockExamEntryApiService"/> is used instead.
/// </summary>
public class ExamEntryApiService : IExamEntryApiService
{
    private readonly HttpClient _http;

    public ExamEntryApiService(HttpClient http)
    {
        _http = http;
    }

    public Task<ExamEntryTicket> VerifyOtpAsync(string examId, string otp, CancellationToken ct = default)
    {
        // TODO(§C/§F - backend chưa có): gọi Java để xác thực OTP và nhận entry ticket. Dự kiến:
        //   POST {JavaBaseUrl}/api/v1/exams/{examId}/otp/verify   body: { "otp": "<otp>" }
        //   200 -> map JSON sang ExamEntryTicket:
        //          attemptId (server-generated), streamJwt (cho vox-streaming), blocklist,
        //          minEnforcementTier, presigned-upload source, deliveryMode, expiresAt.
        //          Lưu vào ExamSessionState.EntryTicket và NGỪNG mint attemptId client-side.
        //   401/410/422 -> throw new OtpVerificationException("Mã OTP không đúng hoặc đã hết hạn.")
        //   Lưu ý: OTP xoay 60s và verify MỘT lần ở đây; các bước sau đi bằng ticket (hạn dài hơn).
        //          Server nên rate-limit số lần verify.
        _ = _http;
        throw new NotImplementedException(
            "ExamEntryApiService.VerifyOtpAsync chưa nối backend. Đặt AppSettings.UseMockData=true để dùng " +
            "MockExamEntryApiService trong lúc phát triển.");
    }
}
