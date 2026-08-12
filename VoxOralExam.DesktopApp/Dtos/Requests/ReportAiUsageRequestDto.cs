using System.Text.Json.Serialization;

namespace VoxOralExam.DesktopApp.Dtos.Requests;

/// <summary>
/// Body cho POST /api/v1/exam-sessions/{id}/ai-usage -- đường REST song song với Kafka topic
/// ai-usage-recorded bên BE (nguồn từ Agentic AI). Dùng khi AI usage phát sinh ngay trên máy học
/// viên (vd LocalAvatarSpeaker.cs) chứ không qua Agentic AI, nên không có kết nối Kafka trực tiếp.
/// Cùng schema với AiUsageRecordedEventDto bên BE.
/// </summary>
public class ReportAiUsageRequestDto
{
    [JsonPropertyName("turnId")]
    public Guid TurnId { get; set; }

    [JsonPropertyName("usageEvents")]
    public List<AiUsageEventItemDto> UsageEvents { get; set; } = [];
}