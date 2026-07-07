using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services;

/// <summary>
/// Verifies the OTP the student typed (the code rotates every 60s on the proctor's screen) and, on
/// success, returns the entry ticket (docs/wpf-redesign-plan.md §C). One implementation talks to Java;
/// a dev mock lets the flow run before that endpoint exists (selected by AppSettings.UseMockData).
/// </summary>
public interface IExamEntryApiService
{
    /// <summary>
    /// Verify <paramref name="otp"/> for <paramref name="examId"/>. Returns the entry ticket on
    /// success; throws <see cref="OtpVerificationException"/> when the code is wrong or already rotated.
    /// </summary>
    Task<ExamEntryTicket> VerifyOtpAsync(string examId, string otp, CancellationToken ct = default);
}

/// <summary>Thrown when the OTP is rejected (wrong or expired) -- distinct from transport errors so the
/// UI can show a friendly "enter the current code" message and let the student retry.</summary>
public class OtpVerificationException : Exception
{
    public OtpVerificationException(string message) : base(message)
    {
    }
}
