using System.Text.Json.Serialization;

namespace VoxOralExam.DesktopApp.Dtos.Responses;

public class LoginDataResponseDto
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = [];
}
