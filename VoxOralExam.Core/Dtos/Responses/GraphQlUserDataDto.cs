using System.Text.Json.Serialization;

namespace VoxOralExam.Core.Dtos.Responses;

public class GraphQlUserDataDto
{
    [JsonPropertyName("user")]
    public UserProfileResponseDto? User { get; set; }
}
