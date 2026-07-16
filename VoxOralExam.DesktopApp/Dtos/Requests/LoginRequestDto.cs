using System.Text.Json.Serialization;

namespace VoxOralExam.DesktopApp.Dtos.Requests;

public class LoginRequestDto
{
    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("device")]
    public LoginDeviceRequestDto Device { get; set; } = new();
}
