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
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The absolute schedule end time used by the exam countdown.</summary>
    public DateTimeOffset? ScheduleEndAt { get; set; }

    // TODO(§B/§C/§E): also carry the lockdown blocklist, minEnforcementTier, the presigned-upload
    // source for turn audio, and deliveryMode (ProctoredLab / ProctoredByod / TakeHome) so the
    // navigator can branch (take-home skips OTP + live monitor).
    public string ScheduleId { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public IReadOnlyList<string> StreamTypes { get; set; } = [];

    public DateTimeOffset StreamTokenExpiresAt { get; set; }

    /// <summary>
    /// What the exam requires the student to share: CAMERA, SCREEN, CAMERA_AND_SCREEN -- or null
    /// when the exam is not monitored at all. Sent by Java on the entry ticket.
    ///
    /// <para>Null is not "unknown", it is a decision the teacher made at creation time, and it is
    /// the ONLY way the client can find out: /streams/student/token answers 400 ("Kỳ thi không hỗ
    /// trợ stream") for such an exam, so blindly asking for a token locks the student out of an
    /// exam they are otherwise fully entitled to sit.</para>
    /// </summary>
    public string? RequiredStreamType { get; set; }

    /// <summary>ALL / ANY, only meaningful for CAMERA_AND_SCREEN. Null otherwise.</summary>
    public string? StreamTypePermission { get; set; }

    /// <summary>
    /// False means: skip the stream token request, and record nothing. Distinct from an empty
    /// <see cref="StreamTypes"/>, which just means "the token has not been issued yet".
    /// </summary>
    public bool IsMonitored => !string.IsNullOrWhiteSpace(RequiredStreamType);
}
