using System.IdentityModel.Tokens.Jwt;
using VoxOralExam.DesktopApp.Services.DomainService;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services.Auth;

/// <summary>
/// The single place the app gets an access token that is actually still valid.
///
/// <para>Before this existed, every Java-facing client read
/// <c>ExamSessionState.CurrentUser.AccessToken</c> directly and nothing ever renewed it. An exam
/// outlasting the token's lifetime therefore broke quietly and permanently, and the worst casualty
/// was evidence rather than the exam itself: UploadCredentialRefresher renews an upload session by
/// first minting a stream token from Java, so an expired access token made renewal impossible,
/// vox-streaming began answering 410 Gone, and the recording segments buffered on disk became
/// un-uploadable for good -- the "stranded evidence" case OrphanedUploadRecoveryService can detect
/// but not fix. It failed silently because TryRefreshAsync catches everything and returns null,
/// which is the correct behaviour for a transient outage and indistinguishable from this.</para>
///
/// <para>Refresh is PROACTIVE, not just reactive to a 401. Reacting alone would mean every renewal
/// path had to handle a 401 correctly, and the one that matters most runs on a timer in the
/// background where a failure is a log line nobody reads.</para>
/// </summary>
public sealed class AuthSessionManager
{
    /// <summary>
    /// How much life an access token must have left to be handed out as-is.
    ///
    /// <para>Covers the whole journey, not just the call: the token is minted into a request that
    /// still has to reach Java, and for stream tokens it is then used to open an upload session
    /// whose own validity is derived from it. A token that is technically alive for another two
    /// seconds is not worth starting any of that with, and the cost of being early is one extra
    /// refresh per exam.</para>
    /// </summary>
    private static readonly TimeSpan RenewalWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Resolved per refresh rather than captured. IAuthApiService is a typed client, so holding one
    /// instance in a singleton would pin a single HttpMessageHandler for the life of the app and
    /// defeat IHttpClientFactory's handler recycling. Refreshes happen a handful of times an exam,
    /// so resolving each time costs nothing worth measuring.
    /// </summary>
    private readonly Func<IAuthApiService> _authApi;
    private readonly ExamSessionState _state;

    /// <summary>
    /// Serialises refresh, and this is a correctness requirement rather than an optimisation. vox
    /// rotates the refresh token on every use and treats a second presentation of a spent one as
    /// replay: RefreshUseCase.validateValidRequest calls sessionManagerPort.revoke on the whole
    /// device session. Two clients refreshing at once would therefore not merely race -- they would
    /// log the student out mid-exam.
    /// </summary>
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public AuthSessionManager(Func<IAuthApiService> authApi, ExamSessionState state)
    {
        _authApi = authApi;
        _state = state;
    }

    /// <summary>
    /// Returns an access token good for at least <see cref="RenewalWindow"/>, refreshing first if
    /// the current one is not. Returns null when nobody is signed in, or when a refresh was needed
    /// and could not be done -- callers should treat that exactly as they treated a missing token
    /// before, because that is what it is.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var user = _state.CurrentUser;
        if (user is null || string.IsNullOrWhiteSpace(user.AccessToken))
        {
            return null;
        }

        if (!NeedsRenewal(user.AccessToken))
        {
            return user.AccessToken;
        }

        return await RefreshAsync(user.AccessToken, ct) ? _state.CurrentUser?.AccessToken : null;
    }

    /// <summary>
    /// Reactive counterpart to <see cref="GetAccessTokenAsync"/>, for a caller that got a 401 with a
    /// token this class believed was fine -- a server-side revocation, or a clock skew wide enough
    /// that the expiry check was wrong. Pass the token that was rejected so a refresh another caller
    /// already completed is not repeated.
    /// </summary>
    public Task<bool> TryRefreshAsync(string? rejectedToken, CancellationToken ct = default) =>
        RefreshAsync(rejectedToken, ct);

    /// <summary>
    /// Ends the session on the server, then locally. Safe to call when nobody is signed in.
    ///
    /// <para>The local clear runs in a finally, so it happens whether or not the server was
    /// reachable. That ordering is the contract the endpoint was written against -- AuthController's
    /// logout javadoc says the same thing from the other side -- and it is the right way round: a
    /// client that kept its tokens because the network was down would go on holding a working
    /// 72-hour credential, which is the exact thing being cleaned up.</para>
    ///
    /// <para>Does not strand evidence, and that is worth stating because it is not obvious.
    /// Revocation kills the SESSION, but JwtAuthenticationFilter never checks session state, so an
    /// access token already issued keeps working until its own exp. Uploads still draining when this
    /// runs therefore keep authenticating -- they simply cannot be renewed past that point, which is
    /// why the caller runs this after the upload workers have been given their window to finish.</para>
    /// </summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        // Under the same gate as refresh: a refresh landing concurrently would rotate a token whose
        // session this is revoking, and the two racing produces nothing useful in either order.
        await _refreshGate.WaitAsync(ct);
        try
        {
            var user = _state.CurrentUser;
            if (user is null)
            {
                return;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(user.Device.DeviceId))
                {
                    // deviceId is @NotBlank on the endpoint, so this would be a 400 rather than a
                    // logout. Nothing to do but clear locally and say why.
                    LocalFileLogger.Error(
                        "auth",
                        "logout_skipped_no_device_id",
                        new InvalidOperationException("No device id on the current session."));
                }
                else
                {
                    await _authApi().LogoutAsync(
                        user.RefreshToken,
                        user.XsrfToken,
                        user.Device.DeviceId,
                        // Only when it is actually still usable. An expired one buys nothing here
                        // (the filter rejects it and the server falls back to the refresh cookie)
                        // and this is not the moment to start a refresh to obtain a fresh one.
                        NeedsRenewal(user.AccessToken) ? null : user.AccessToken,
                        ct);
                    LocalFileLogger.Info("auth", "logged_out", new { user.UserId });
                }
            }
            catch (Exception ex)
            {
                // Never escalated: this runs on the way out, and a failed revoke must not stop the
                // local tokens from being dropped.
                LocalFileLogger.Error("auth", "logout_failed", ex, new { user.UserId });
            }
        }
        finally
        {
            _state.ClearAuthenticatedUser();
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Performs at most one refresh for a given stale token, however many callers arrive with it.
    /// </summary>
    /// <param name="staleToken">
    /// The token the caller found unusable. Everyone queued behind the gate compares it against
    /// what is current on the way in, so the first caller through refreshes and the rest simply
    /// discover the answer is already there. Without that check a burst of clients -- the two
    /// upload credential refreshers and the exam API all noticing at once, which is exactly what a
    /// token expiry causes -- would spend the rotated token several times over and revoke the
    /// session.
    /// </param>
    private async Task<bool> RefreshAsync(string? staleToken, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var user = _state.CurrentUser;
            if (user is null)
            {
                return false;
            }

            // Somebody refreshed while this caller was queued.
            if (!string.IsNullOrWhiteSpace(staleToken)
                && !string.Equals(user.AccessToken, staleToken, StringComparison.Ordinal))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(user.RefreshToken))
            {
                // Nothing to refresh with. Sessions signed in before this build have no stored
                // refresh cookie, and there is no way to obtain one without a fresh login.
                LocalFileLogger.Error(
                    "auth",
                    "refresh_unavailable",
                    new InvalidOperationException("No refresh token stored for the current session."));
                return false;
            }

            try
            {
                var refreshed = await _authApi().RefreshAsync(
                    user.RefreshToken, user.XsrfToken, user.Device.DeviceId, ct);

                // Adopted as a set: see RefreshedAuthTokens for why splitting them is unsafe.
                user.AccessToken = refreshed.AccessToken;
                user.RefreshToken = refreshed.RefreshToken;
                user.XsrfToken = refreshed.XsrfToken;

                LocalFileLogger.Info("auth", "access_token_refreshed", new { user.UserId });
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Not fatal here on purpose. A refresh can fail because the network is down, which
                // is transient and worth retrying on the next call, or because the session was
                // revoked, which is not -- and this layer cannot tell them apart. Callers already
                // handle "no token available"; escalating would turn a recoverable blip into a
                // failed exam.
                LocalFileLogger.Error("auth", "access_token_refresh_failed", ex, new { user.UserId });
                return false;
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Whether a token is expired or close enough to it to be worth replacing now.
    /// </summary>
    /// <remarks>
    /// An unreadable token counts as needing renewal rather than as an error: it cannot be used for
    /// anything, so the only useful move is to try to replace it.
    /// </remarks>
    private static bool NeedsRenewal(string accessToken)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(accessToken))
            {
                return true;
            }

            // ValidTo is already UTC, and DateTime.MinValue means the token carried no exp claim --
            // which reads as "expired long ago" and would send this into a refresh loop, so treat a
            // token with no expiry as one that never needs replacing.
            var expiresAt = handler.ReadJwtToken(accessToken).ValidTo;
            if (expiresAt == DateTime.MinValue)
            {
                return false;
            }

            return expiresAt - DateTime.UtcNow <= RenewalWindow;
        }
        catch
        {
            return true;
        }
    }
}
