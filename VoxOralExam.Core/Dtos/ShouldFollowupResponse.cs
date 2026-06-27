using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoxOralExam.Core.Dtos;

public class ShouldFollowupResponse
{
    public int TurnOrder { get; set; }
    public string Transcript { get; set; } = string.Empty;
    public string? PromptText { get; set; }
    public EvaluatedTurnDto? CurrentTurn { get; set; }
    public bool ShouldContinue { get; set; }
    public string? NextPromptText { get; set; }
    public string Reason { get; set; } = string.Empty;
    public bool ReachedMaxTurns { get; set; }
}

public class EvaluatedTurnDto
{
    public Guid AnswerId { get; set; }
    public int TurnOrder { get; set; }
    public TurnType TurnType { get; set; }
    public string? PromptText { get; set; }
    public string AudioUrl { get; set; } = string.Empty;
    public string Transcript { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public int WordCount { get; set; }
    public DateTimeOffset AnsweredAt { get; set; }
}

[JsonConverter(typeof(TurnTypeJsonConverter))]
public enum TurnType
{
    Main,
    Followup
}

public sealed class TurnTypeJsonConverter : JsonConverter<TurnType>
{
    public override TurnType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value?.ToUpperInvariant() switch
        {
            "MAIN" => TurnType.Main,
            "FOLLOWUP" => TurnType.Followup,
            _ => throw new JsonException($"Unsupported turn type '{value}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, TurnType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            TurnType.Main => "MAIN",
            TurnType.Followup => "FOLLOWUP",
            _ => throw new JsonException($"Unsupported turn type '{value}'.")
        });
    }
}
