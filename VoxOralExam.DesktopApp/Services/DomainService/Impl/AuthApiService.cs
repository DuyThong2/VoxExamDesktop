using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VoxOralExam.Core.Context;
using VoxOralExam.DesktopApp.Dtos.Requests;
using VoxOralExam.DesktopApp.Dtos.Responses;
using VoxOralExam.DesktopApp.Services.DomainService;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services.DomainService.Impl;

public class AuthApiService : IAuthApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // `profile` resolves the CURRENTLY AUTHENTICATED user and only requires isAuthenticated()
    // -- unlike the top-level `user(id: ...)` query, which is SYSTEM_ADMIN-only (for admins
    // looking up an arbitrary user by id) and was wrongly being used here for self-lookup,
    // causing every non-system-admin login (student/teacher/school-admin -- i.e. everyone who
    // actually uses this app) to silently fail to load their own profile.
    private const string GetProfileQuery = """
                                        query GetProfile {
                                          profile {
                                            id
                                            email
                                            phone
                                            fullName
                                            gender
                                            dateOfBirth
                                            address
                                            avatarUrl
                                            createdAt
                                            updatedAt
                                          }
                                        }
                                        """;

    private readonly HttpClient _httpClient;

    public AuthApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AuthenticatedUserContext> LoginAsync(
        string login,
        string password,
        LoginDeviceContext deviceContext,
        CancellationToken cancellationToken = default)
    {
        var request = new LoginRequestDto
        {
            Login = login,
            Password = password,
            Device = new LoginDeviceRequestDto
            {
                DeviceId = deviceContext.DeviceId,
                DeviceName = deviceContext.DeviceName,
                Platform = deviceContext.Platform
            }
        };

        using var response = await _httpClient.PostAsJsonAsync("/api/v1/auth/login", request, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        // Read BEFORE the status check bails: on a failed login there is nothing to keep, but on a
        // successful one these are the only place the refresh and CSRF tokens ever appear.
        var refreshToken = ReadSetCookie(response, RefreshTokenCookie) ?? string.Empty;
        var xsrfToken = ReadSetCookie(response, XsrfTokenCookie) ?? string.Empty;

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ExtractErrorMessage(responseJson, response.ReasonPhrase));
        }

        var loginResponse = JsonSerializer.Deserialize<LoginResponseDto>(responseJson, JsonOptions)
            ?? throw new InvalidOperationException("Login response is empty.");

        var accessToken = loginResponse.Data.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Login response does not contain accessToken.");
        }

        var tokenPayload = DecodeJwtPayload(accessToken);
        var userId = string.IsNullOrWhiteSpace(tokenPayload.UserId)
            ? tokenPayload.Subject
            : tokenPayload.UserId;
        var email = string.IsNullOrWhiteSpace(tokenPayload.Email)
            ? login
            : tokenPayload.Email;
        var roles = loginResponse.Data.Roles.Count > 0
            ? loginResponse.Data.Roles
            : tokenPayload.Roles;

        var userProfile = await GetUserProfileAsync(accessToken, cancellationToken);

        return new AuthenticatedUserContext
        {
            Login = email,
            UserId = userId,
            Email = userProfile?.Email ?? email,
            Phone = userProfile?.Phone ?? string.Empty,
            DisplayName = userProfile?.FullName
                ?? userProfile?.Email
                ?? email,
            Gender = userProfile?.Gender ?? string.Empty,
            DateOfBirth = userProfile?.DateOfBirth ?? string.Empty,
            Address = userProfile?.Address ?? string.Empty,
            AvatarUrl = userProfile?.AvatarUrl ?? string.Empty,
            AccessToken = accessToken,
            // Cookie first, body second. The body field has been null since the server started
            // issuing the refresh token as a cookie, so reading it alone left RefreshToken empty for
            // every session -- which is why nothing could refresh and long exams silently lost the
            // ability to renew upload credentials at all.
            RefreshToken = !string.IsNullOrWhiteSpace(refreshToken)
                ? refreshToken
                : loginResponse.Data.RefreshToken ?? string.Empty,
            XsrfToken = xsrfToken,
            TokenType = "Bearer",
            Roles = roles,
            RawResponseJson = responseJson,
            Device = deviceContext
        };
    }

    private const string RefreshTokenCookie = "refresh_token";
    private const string XsrfTokenCookie = "XSRF-TOKEN";

    /// <summary>
    /// Exchanges the rotating refresh cookie for a new access token.
    ///
    /// <para>Three things all have to be present or this is a 403/401 rather than a refresh, and
    /// none of them are obvious from the method signature alone:</para>
    /// <list type="bullet">
    /// <item>the refresh token travels as a COOKIE, because AuthController reads it with
    /// <c>@CookieValue</c> -- there is no body field for it;</item>
    /// <item>the XSRF token has to go out twice, as a cookie AND as the X-XSRF-TOKEN header, which
    /// is what Spring's CookieCsrfTokenRepository compares. This is the only CSRF-protected endpoint
    /// on the API chain (SecurityConfig.CSRF_PROTECTED_API_PATH) and it is protected precisely
    /// because it authenticates from a cookie rather than from the Authorization header;</item>
    /// <item>the deviceId must match the one the session was opened with -- a mismatch does not just
    /// fail, it REVOKES the device session (RefreshUseCase.validateValidRequest).</item>
    /// </list>
    ///
    /// <para>Sends no Authorization header on purpose: the access token is expired by the time
    /// anyone calls this, and this endpoint does not read it anyway.</para>
    /// </summary>
    public async Task<RefreshedAuthTokens> RefreshAsync(
        string refreshToken,
        string xsrfToken,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Cannot refresh without a refresh token.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");

        // Built by hand rather than left to a CookieContainer: these clients come from
        // IHttpClientFactory, whose handlers are pooled and recycled on a timer, so a container's
        // contents are not something a multi-hour exam can rely on still being there.
        var cookies = string.IsNullOrWhiteSpace(xsrfToken)
            ? $"{RefreshTokenCookie}={refreshToken}"
            : $"{RefreshTokenCookie}={refreshToken}; {XsrfTokenCookie}={xsrfToken}";
        request.Headers.Add("Cookie", cookies);
        if (!string.IsNullOrWhiteSpace(xsrfToken))
        {
            request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
        }

        request.Content = JsonContent.Create(new { deviceId });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Token refresh failed ({(int)response.StatusCode}): {ExtractErrorMessage(responseJson, response.ReasonPhrase)}");
        }

        var refreshResponse = JsonSerializer.Deserialize<RefreshResponseDto>(responseJson, JsonOptions)
            ?? throw new InvalidOperationException("Refresh response is empty.");

        var accessToken = refreshResponse.Data.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Refresh response does not contain accessToken.");
        }

        // The rotated cookies. Falling back to the old values matters: the server always rotates the
        // refresh token, so an empty read here would mean discarding a good credential and being
        // unable to refresh again -- but a response that genuinely omitted one leaves the previous
        // value as the only candidate worth keeping.
        return new RefreshedAuthTokens(
            accessToken,
            ReadSetCookie(response, RefreshTokenCookie) ?? refreshToken,
            ReadSetCookie(response, XsrfTokenCookie) ?? xsrfToken);
    }

    /// <summary>
    /// Revokes the device session server-side. Best-effort by contract: the endpoint always answers
    /// 200 and the caller clears its local tokens regardless of what happens here.
    /// </summary>
    /// <param name="accessToken">
    /// Sent when one is still valid, and worth the trouble of passing: the endpoint is deliberately
    /// unauthenticated (no @PreAuthorize, because the session most in need of revoking is exactly
    /// the one whose access token already expired), but LogoutUseCase.findLiveSessionsOnDevice only
    /// runs when it can resolve a current user. Without it, only the single session the refresh
    /// cookie points at is revoked; with it, every live session this user has on this machine goes
    /// too -- which on a shared exam machine is the part that actually matters.
    /// </param>
    public async Task LogoutAsync(
        string refreshToken,
        string xsrfToken,
        string deviceId,
        string? accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");

        // The refresh cookie is optional server-side (@CookieValue required = false) -- logging out
        // without one is still a valid logout, it just has less to revoke.
        var cookies = new List<string>();
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            cookies.Add($"{RefreshTokenCookie}={refreshToken}");
        }
        if (!string.IsNullOrWhiteSpace(xsrfToken))
        {
            cookies.Add($"{XsrfTokenCookie}={xsrfToken}");
            // Same CSRF requirement as /auth/refresh, and for the same reason: this endpoint reads a
            // cookie, so it is in SecurityConfig's CSRF matcher. No header, no logout -- 403.
            request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
        }
        if (cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies));
        }

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        request.Content = JsonContent.Create(new { deviceId });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Logout failed ({(int)response.StatusCode}): {ExtractErrorMessage(body, response.ReasonPhrase)}");
        }
    }

    /// <summary>
    /// Pulls one cookie's value out of a response's Set-Cookie headers. Deliberately does not use a
    /// CookieContainer: this has to work identically whichever pooled handler the request happened
    /// to go out on.
    /// </summary>
    private static string? ReadSetCookie(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
        {
            return null;
        }

        var prefix = name + "=";
        foreach (var header in headers)
        {
            if (!header.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var value = header[prefix.Length..];
            var end = value.IndexOf(';');
            if (end >= 0)
            {
                value = value[..end];
            }

            // An expiring cookie is written as an empty value; treating that as a credential would
            // replace a working token with nothing.
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private async Task<UserProfileResponseDto?> GetUserProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/graphql");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new GraphQlRequestDto
        {
            Query = GetProfileQuery
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var graphQlResponse = await response.Content.ReadFromJsonAsync<GraphQlResponseDto<GraphQlProfileDataDto>>(JsonOptions, cancellationToken);
        return graphQlResponse?.Data?.Profile;
    }

    private static string ExtractErrorMessage(string responseJson, string? reasonPhrase)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return reasonPhrase ?? "Login request failed.";
        }

        try
        {
            var errorResponse = JsonSerializer.Deserialize<LoginErrorResponseDto>(responseJson, JsonOptions);
            if (errorResponse is not null && !string.IsNullOrWhiteSpace(errorResponse.Message))
            {
                return errorResponse.Message;
            }
        }
        catch
        {
        }

        return responseJson;
    }

    private static DecodedJwtPayloadDto DecodeJwtPayload(string jwt)
    {
        var handler = new JwtSecurityTokenHandler();
        if (!handler.CanReadToken(jwt))
        {
            throw new InvalidOperationException("Invalid JWT access token.");
        }

        var token = handler.ReadJwtToken(jwt);

        return new DecodedJwtPayloadDto
        {
            UserId = GetClaim(token, "userId"),
            Email = GetClaim(token, "email"),
            Subject = token.Subject ?? string.Empty,
            Roles = token.Claims
                .Where(claim => claim.Type is "roles" or "role")
                .Select(claim => claim.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList()
        };
    }

    private static string GetClaim(JwtSecurityToken token, string claimType)
    {
        return token.Claims.FirstOrDefault(claim => claim.Type == claimType)?.Value ?? string.Empty;
    }
}

