using System.Text.Json.Serialization;

namespace VoxOralExam.DesktopApp.Dtos.Responses;

public class RefreshResponseDto
{
    [JsonPropertyName("data")]
    public RefreshDataResponseDto Data { get; set; } = new();
}

public class RefreshDataResponseDto
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    // Present in the contract but always null on the wire -- vox returns the rotated refresh token
    // as a Set-Cookie header instead (AuthController.refresh). Kept so the shape matches the API
    // rather than to be read.
    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }
}
