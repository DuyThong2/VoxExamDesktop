using VoxOralExam.Core.Context;

namespace VoxOralExam.DesktopApp.Services.DomainService;

public interface IAuthApiService
{
    Task<AuthenticatedUserContext> LoginAsync(string login, string password, LoginDeviceContext deviceContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exchanges the rotating refresh cookie for a new access token. Callers should go through
    /// AuthSessionManager rather than calling this directly -- refresh tokens are single-use and a
    /// second concurrent call revokes the device session.
    /// </summary>
    Task<RefreshedAuthTokens> RefreshAsync(string refreshToken, string xsrfToken, string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes the device session server-side. Callers should go through
    /// AuthSessionManager.SignOutAsync, which pairs this with clearing the local tokens.
    /// </summary>
    Task LogoutAsync(string refreshToken, string xsrfToken, string deviceId, string? accessToken, CancellationToken cancellationToken = default);
}

