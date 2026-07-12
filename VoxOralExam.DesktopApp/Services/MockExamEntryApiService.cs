using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services;

public class MockExamEntryApiService : IExamEntryApiService
{
    public const string DevOtp = "123456";

    public async Task<ExamEntryTicket> VerifyOtpAsync(string examId, string otp, CancellationToken ct = default)
    {
        await Task.Delay(400, ct);

        if (otp != DevOtp)
        {
            throw new OtpVerificationException($"Ma OTP khong dung (che do dev: nhap {DevOtp}).");
        }

        return CreateTicket();
    }

    public async Task<ExamEntryTicket> StartClassTestAsync(Guid examId, CancellationToken ct = default)
    {
        await Task.Delay(400, ct);
        return CreateTicket();
    }

    private static ExamEntryTicket CreateTicket()
    {
        return new ExamEntryTicket
        {
            AttemptId = Guid.NewGuid(),
            StreamJwt = "dev-stub-stream-jwt",
            TicketId = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddHours(2),
        };
    }
}
