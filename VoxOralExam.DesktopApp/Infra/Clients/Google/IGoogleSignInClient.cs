namespace VoxOralExam.DesktopApp.Infra.Clients.Google;

/// <summary>
/// Drives an interactive Google sign-in and returns the resulting OpenID Connect ID token.
///
/// <para>Returns the ID token rather than an <c>AuthenticatedUserContext</c> on purpose: this type
/// knows how to talk to Google and nothing at all about vox. Exchanging that token for a vox session
/// is <c>IAuthApiService.LoginWithGoogleAsync</c>'s job, and keeping the two apart means the Google
/// half can be exercised without a running backend.</para>
/// </summary>
public interface IGoogleSignInClient
{
    /// <summary>
    /// Opens the system browser, waits for the user to finish, and returns a freshly minted ID token.
    /// </summary>
    /// <returns>
    /// The raw ID token (a JWT), or null when the user closed the browser or denied consent.
    /// Cancelling is a normal outcome of a login screen rather than an error, so it does not throw --
    /// the caller simply goes back to showing the form.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Google refused the request or answered with something unusable: a missing client id, a
    /// rejected redirect URI, a token response with no id_token.
    /// </exception>
    Task<string?> AcquireIdTokenAsync(CancellationToken cancellationToken = default);
}
