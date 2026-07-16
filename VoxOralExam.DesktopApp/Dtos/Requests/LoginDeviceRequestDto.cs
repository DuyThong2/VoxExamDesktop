using System.Text.Json.Serialization;

namespace VoxOralExam.DesktopApp.Dtos.Requests;

public class LoginDeviceRequestDto
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;
}
