namespace VoxOralExam.Core.Context;

public class AuthenticatedUserContext
{
    public string Login { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public string DateOfBirth { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Arrives ONLY as a Set-Cookie header, never in a response body -- vox's AuthController
    /// deliberately puts null in the JSON and the real value in the refresh_token cookie. Rotated
    /// on every refresh, and single-use: presenting a spent one makes the server revoke the whole
    /// device session (RefreshUseCase.validateValidRequest), so this must never be sent twice.
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// The XSRF-TOKEN cookie value. POST /api/v1/auth/refresh is the ONE endpoint on the API chain
    /// with CSRF enabled (SecurityConfig.CSRF_PROTECTED_API_PATH) precisely because it authenticates
    /// from a cookie, so a refresh without this echoed back in X-XSRF-TOKEN is a 403.
    /// </summary>
    public string XsrfToken { get; set; } = string.Empty;

    public string TokenType { get; set; } = "Bearer";
    public List<string> Roles { get; set; } = [];
    public string RawResponseJson { get; set; } = string.Empty;
    public LoginDeviceContext Device { get; set; } = new();
}
