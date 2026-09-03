using System.Text.Json.Serialization;

namespace VoxOralExam.DesktopApp.Dtos.Requests;

/// <summary>
/// Body of POST /api/v1/auth/oauth2/google/token -- mirrors the backend's GoogleTokenLoginRequest.
/// </summary>
public class GoogleTokenLoginRequestDto
{
    [JsonPropertyName("idToken")]
    public string IdToken { get; set; } = string.Empty;

    [JsonPropertyName("device")]
    public LoginDeviceRequestDto Device { get; set; } = new();
}
