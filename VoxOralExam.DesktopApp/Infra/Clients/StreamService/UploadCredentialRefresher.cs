using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.Infra.Clients.StreamService;

public sealed record RefreshUploadCredential(StreamUploadSession Session, string StreamToken);

/// <summary>
/// Renews an upload credential that is about to expire, without starting a new stream.
///
/// vox-streaming pins an upload session's lifetime to the stream JWT it was opened with (plus a
/// fixed grace) and never extends it on upload activity, so a machine that spends long enough
/// offline comes back to a credential the server answers 410 Gone -- with segments on disk that can
/// then never be sent, however well the client buffered them. Refreshing is the only way out, and
/// it works because POST /stream/sessions is a resume, not just a create: for the same candidate,
/// session and stream type, RegisterOrGetUpload hands back the SAME streamId with a new token and a
/// pushed-out expiry, so everything already uploaded under that stream still belongs to it.
///
/// Bounded by the exam itself, deliberately: the Java endpoint behind this only issues a token
/// while the exam session is in progress and the current time is inside the schedule window (see
/// IssueStudentStreamTokenUseCase). A machine that returns after the exam window closed cannot be
/// rescued from here, and should not be able to be -- that is a policy decision about accepting
/// evidence after the fact, not a plumbing gap.
/// </summary>
public sealed class UploadCredentialRefresher
{
    private readonly StudentStreamAccessClient _streamAccessClient;
    private readonly StreamSessionClient _sessionClient;

    public UploadCredentialRefresher(
        StudentStreamAccessClient streamAccessClient,
        StreamSessionClient sessionClient)
    {
        _streamAccessClient = streamAccessClient;
        _sessionClient = sessionClient;
    }

    /// <summary>
    /// Mints a fresh stream token and re-opens the upload session with it, returning the renewed
    /// credential for the same stream. Returns null when renewal is not possible right now -- no
    /// signed-in student, an exam window that has closed, an unreachable server -- which is a
    /// normal outcome, not an error: the caller keeps the credential it has and tries again later.
    /// </summary>
    public async Task<RefreshUploadCredential?> TryRefreshAsync(
        Guid examSessionId,
        string streamType,
        CancellationToken ct)
    {
        try
        {
            // No preferred stream type on purpose. The token belongs to the exam session, not to one
            // stream: it carries every type the session was granted, and the session locks that
            // choice on the first issue (IssueStudentStreamTokenUseCase). Naming a single type here
            // would be asking to change a locked choice -- rejected for a CAMERA_AND_SCREEN session,
            // which is exactly the case that has two credentials to refresh. streamType still
            // matters below: the upload session IS per-stream, and vox-streaming checks it against
            // the token's streamTypes.
            var access = await _streamAccessClient.IssueAsync(examSessionId, preferredStreamType: null, ct);
            var renewed = await _sessionClient.CreateAsync(streamType, access.Token, ct);

            LocalFileLogger.Info(
                "upload_credential",
                "refreshed",
                new { renewed.StreamId, streamType, renewed.ExpiresAt });
            return new RefreshUploadCredential(renewed, access.Token);
        }
        catch (Exception ex)
        {
            // Includes the ordinary "nobody is signed in" case: StudentStreamAccessClient requires
            // the student's own access token, so a refresh attempted outside a live session (for
            // example during startup recovery, before login) simply cannot succeed.
            LocalFileLogger.Error(
                "upload_credential",
                "refresh_failed",
                ex,
                new { examSessionId, streamType });
            return null;
        }
    }
}
