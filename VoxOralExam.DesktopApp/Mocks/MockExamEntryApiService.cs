using VoxOralExam.Core.Context;

using VoxOralExam.DesktopApp.Services.DomainService;

namespace VoxOralExam.DesktopApp.Mocks;

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
            ScheduleId = Guid.NewGuid().ToString("D"),
            SessionId = Guid.NewGuid().ToString("D"),
            StreamJwt = "dev-stub-stream-jwt",
            // Mock exams stay monitored -- the dev flow exists to exercise recording + upload.
            RequiredStreamType = "CAMERA_AND_SCREEN",
            StreamTypePermission = "ALL",
            StreamTypes = ["camera", "screen"],
            StreamTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            TicketId = Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddHours(2),
            ScheduleEndAt = DateTime.UtcNow.AddMinutes(30),
        };
    }
}

