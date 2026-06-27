using System.Text.Json.Serialization;

namespace VoxOralExam.Core.Dtos.Responses;

public class GraphQlResponseDto<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }
}
