using System.Text.Json.Serialization;

namespace VoxOralExam.DesktopApp.Dtos.Responses;

public class LoginErrorResponseDto
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
