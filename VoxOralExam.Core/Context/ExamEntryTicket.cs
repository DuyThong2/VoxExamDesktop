using VoxOralExam.Core.Models;

namespace VoxOralExam.Core.Context;

/// <summary>
/// The result of verifying the OTP (docs/wpf-redesign-plan.md §C). The server issues this once, after
/// a single successful OTP check; every later stage (DevicePreflight, InExam) rides the
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
    /// What this session already locked onto, or null if no stream token has been issued for it yet.
    ///
    /// <para>Only ever set for a resumed attempt. The server locks the choice on the FIRST token it
    /// issues and answers 403 for any other type afterwards, so re-entering an interrupted attempt
    /// must show the locked choice rather than ask again -- otherwise the student picks freely,
    /// sits through the whole device check, and is rejected at the last step.</para>
    /// </summary>
    public string? ChosenStreamType { get; set; }

    /// <summary>
    /// True when the exam lets the student choose which stream(s) to share. Requires BOTH that the
    /// exam accepts either one and that this session has not already locked a choice.
    /// </summary>
    public bool AllowsStreamTypeChoice =>
        string.Equals(RequiredStreamType, "CAMERA_AND_SCREEN", StringComparison.OrdinalIgnoreCase)
        && string.Equals(StreamTypePermission, "ANY", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(ChosenStreamType);

    /// <summary>
    /// The stream types to verify and record BEFORE a token exists, given the student's choice.
    ///
    /// <para>Distinct from <see cref="ResolveRecordingStreamTypes"/>, which reads
    /// <see cref="StreamTypes"/> -- that list is filled by the token response, so during the device
    /// preflight it is still empty and its "unsure, so assume both" fallback would quietly override
    /// a student who picked camera only.</para>
    /// </summary>
    public IReadOnlyList<RecordingStreamType> ResolveRequestedStreamTypes(string? preferredStreamType)
    {
        if (!IsMonitored)
        {
            return [];
        }

        var effective = preferredStreamType
            ?? (string.IsNullOrWhiteSpace(ChosenStreamType) ? RequiredStreamType : ChosenStreamType);

        return effective?.Trim().ToUpperInvariant() switch
        {
            "CAMERA" => [RecordingStreamType.Camera],
            "SCREEN" => [RecordingStreamType.Screen],
            _ => [RecordingStreamType.Camera, RecordingStreamType.Screen]
        };
    }

    /// <summary>
    /// False means: skip the stream token request, and record nothing. Distinct from an empty
    /// <see cref="StreamTypes"/>, which just means "the token has not been issued yet".
    /// </summary>
    public bool IsMonitored => !string.IsNullOrWhiteSpace(RequiredStreamType);

    /// <summary>
    /// The stream types this attempt must actually produce.
    ///
    /// <para>Deliberately shared by the device preflight (which decides whether the student may
    /// start at all) and by the exam window (which decides what to record). These two MUST agree:
    /// a preflight that clears the camera while the exam records screen -- or vice versa -- is a
    /// gate that proves nothing, and the two would drift the moment either side's copy of this
    /// mapping was edited alone.</para>
    ///
    /// <para><see cref="StreamTypes"/> is the authority because it is what the server actually
    /// locked onto the session when it issued the token; <see cref="RequiredStreamType"/> is only
    /// what the exam asks for in general. Falling back to both types when the list is empty is the
    /// conservative reading for both callers: unsure what is required means check (and record)
    /// everything, never nothing.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A stream type the client does not know how to capture. Fails loudly rather than being
    /// skipped: silently ignoring it would record less than the exam demanded.
    /// </exception>
    public IReadOnlyList<RecordingStreamType> ResolveRecordingStreamTypes()
    {
        if (!IsMonitored)
        {
            return [];
        }

        if (StreamTypes.Count == 0)
        {
            return [RecordingStreamType.Camera, RecordingStreamType.Screen];
        }

        return [.. StreamTypes
            .Select(value => value.Trim().ToLowerInvariant())
            .Select(value => value switch
            {
                "camera" => RecordingStreamType.Camera,
                "screen" => RecordingStreamType.Screen,
                _ => throw new InvalidOperationException($"Unsupported stream type: {value}")
            })
            .Distinct()];
    }
}
