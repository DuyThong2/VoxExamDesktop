using System.Text.Json.Serialization;

namespace VoxOralExam.Core.Dtos.Responses;

public class LoginResponseDto
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public LoginDataResponseDto Data { get; set; } = new();
}
