namespace VoxOralExam.Core.Context;

/// <summary>
/// One successful token refresh. All three values rotate together and must be adopted together:
/// keeping the new access token while dropping the new refresh cookie leaves the next refresh
/// presenting a spent token, which vox treats as replay and answers by revoking the device session.
/// </summary>
public sealed record RefreshedAuthTokens(
    string AccessToken,
    string RefreshToken,
    string XsrfToken
);
