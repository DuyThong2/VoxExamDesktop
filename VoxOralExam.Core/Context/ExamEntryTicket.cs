namespace VoxOralExam.Core.Context;

/// <summary>
/// The result of verifying the OTP (docs/wpf-redesign-plan.md §C). The server issues this once, after
/// a single successful OTP check; every later stage (SystemCheck, DevicePreflight, InExam) rides the
/// ticket instead of re-validating the (now-rotated) OTP. The ticket has its own, longer validity.
///
/// Only the fields the client already needs are modelled now; the rest are TODO until the Java
/// endpoint exists.
/// </summary>
public class ExamEntryTicket
{
    /// <summary>Server-generated attempt id. Replaces the client-minted Guid in ExamSessionState.</summary>
    public Guid AttemptId { get; set; }

    /// <summary>JWT for vox-streaming's /ws/stream (issued by Java, verified by Go).</summary>
    public string StreamJwt { get; set; } = string.Empty;

    /// <summary>Opaque ticket id, for logging / server-side revocation.</summary>
    public string TicketId { get; set; } = string.Empty;

    /// <summary>When the ticket itself expires (longer than the 60s OTP window).</summary>
    public DateTime ExpiresAt { get; set; }

    // TODO(§B/§C/§E): also carry the lockdown blocklist, minEnforcementTier, the presigned-upload
    // source for turn audio, and deliveryMode (ProctoredLab / ProctoredByod / TakeHome) so the
    // navigator can branch (take-home skips OTP + live monitor).
}
