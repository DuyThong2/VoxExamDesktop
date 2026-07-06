using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services;

/// <summary>
/// Dev-only <see cref="IExamEntryApiService"/>. Lets the OTP screen run end-to-end before Java's OTP
/// endpoint exists: it accepts a fixed dev code and rejects everything else so both the success and the
/// error path can be exercised. Selected when AppSettings.UseMockData is true.
/// </summary>
public class MockExamEntryApiService : IExamEntryApiService
{
    // Dev code to type on the OTP screen while there is no backend.
    public const string DevOtp = "123456";

    public async Task<ExamEntryTicket> VerifyOtpAsync(string examId, string otp, CancellationToken ct = default)
    {
        // Small delay so the "Đang xác thực..." state is visible, like a real round-trip.
        await Task.Delay(400, ct);

        if (otp != DevOtp)
        {
            throw new OtpVerificationException($"Mã OTP không đúng (chế độ dev: nhập {DevOtp}).");
        }

        return new ExamEntryTicket
        {
            AttemptId = Guid.NewGuid(),
            StreamJwt = "dev-stub-stream-jwt",
            TicketId = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddHours(2),
        };
    }
}
