using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services;

/// <summary>
/// Verifies the OTP the student typed and, on success, returns the entry ticket. Also starts class
/// tests directly when no OTP is required.
/// </summary>
public interface IExamEntryApiService
{
    Task<ExamEntryTicket> VerifyOtpAsync(string examId, string otp, CancellationToken ct = default);

    Task<ExamEntryTicket> StartClassTestAsync(Guid examId, CancellationToken ct = default);
}

public class ExamEntryRejectedException : Exception
{
    public ExamEntryRejectedException(string message) : base(message)
    {
    }
}

public class OtpVerificationException : ExamEntryRejectedException
{
    public OtpVerificationException(string message) : base(message)
    {
    }
}
