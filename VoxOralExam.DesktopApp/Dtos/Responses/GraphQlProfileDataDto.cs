using System.Text.Json.Serialization;

namespace VoxOralExam.DesktopApp.Dtos.Responses;

public class GraphQlProfileDataDto
{
    [JsonPropertyName("profile")]
    public UserProfileResponseDto? Profile { get; set; }
}
