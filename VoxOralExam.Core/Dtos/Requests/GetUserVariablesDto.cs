using System.Text.Json.Serialization;

namespace VoxOralExam.Core.Dtos.Requests;

public class GetUserVariablesDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}
