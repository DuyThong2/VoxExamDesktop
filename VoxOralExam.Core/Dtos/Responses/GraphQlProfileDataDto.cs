using System.Text.Json.Serialization;

namespace VoxOralExam.Core.Dtos.Responses;

public class GraphQlProfileDataDto
{
    [JsonPropertyName("profile")]
    public UserProfileResponseDto? Profile { get; set; }
}
