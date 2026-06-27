using System.Text.Json.Serialization;

namespace VoxOralExam.Core.Dtos.Requests;

public class GraphQlRequestDto
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("variables")]
    public object Variables { get; set; } = new();
}
