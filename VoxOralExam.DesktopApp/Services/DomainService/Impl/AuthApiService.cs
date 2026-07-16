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
            RefreshToken = loginResponse.Data.RefreshToken ?? string.Empty,
            TokenType = "Bearer",
            Roles = roles,
            RawResponseJson = responseJson,
            Device = deviceContext
        };
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

