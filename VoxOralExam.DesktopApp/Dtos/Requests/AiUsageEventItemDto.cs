using System.Text.Json.Serialization;

namespace VoxOralExam.DesktopApp.Dtos.Requests;

/// <summary>
/// type phải đúng "LLM_TOKEN" hoặc "DURATION" (khớp enum AiUsageType bên Java). unitPrice là JSON
/// tự do (vd { amount, unit, currency }) -- BE lưu nguyên văn, không parse cấu trúc cụ thể.
/// </summary>
public class AiUsageEventItemDto
{
    [JsonPropertyName("usageEventId")]
    public Guid UsageEventId { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("inputTokens")]
    public int? InputTokens { get; set; }

    [JsonPropertyName("outputTokens")]
    public int? OutputTokens { get; set; }

    [JsonPropertyName("cacheCreationInputTokens")]
    public int? CacheCreationInputTokens { get; set; }

    [JsonPropertyName("cacheReadInputTokens")]
    public int? CacheReadInputTokens { get; set; }

    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; set; }

    [JsonPropertyName("unitPrice")]
    public object? UnitPrice { get; set; }

    [JsonPropertyName("costUsd")]
    public decimal CostUsd { get; set; }

    [JsonPropertyName("occurredAt")]
    public DateTimeOffset? OccurredAt { get; set; }
}
