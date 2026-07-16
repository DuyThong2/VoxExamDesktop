namespace VoxOralExam.DesktopApp.Infra.Clients.DomainService;

/// <summary>
/// Server-issued, short-lived upload target for one turn's audio. Replaces the old model where the
/// client held long-lived static AWS credentials and talked to S3 directly. The client now asks the
/// server (Java) for a presigned PUT URL scoped to (attemptAnswerId, turnOrder) and uploads to it;
/// the server also returns the stable audioRef to hand to Python's /turns/archive.
/// </summary>
public interface ITurnUploadUrlProvider
{
    Task<TurnUploadTarget> GetUploadTargetAsync(Guid attemptAnswerId, int turnOrder, CancellationToken ct);
}

/// <param name="UploadUrl">Presigned PUT URL the client uploads the WAV bytes to.</param>
/// <param name="AudioRef">Stable object URL/key the server persists it as, passed on to /turns/archive.</param>
public sealed record TurnUploadTarget(string UploadUrl, string AudioRef);

