using System.Text.Json.Serialization;

namespace VoxOralExam.Core.Dtos;

public class EvaluateTurnRequest
{
    public string AudioRef { get; set; } = string.Empty;
    public Guid AnswerId { get; set; }
    public Guid? PaperItemId { get; set; }
    public int TurnOrder { get; set; }
    public string PromptText { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public QuestionContextDto Question { get; set; } = new();
}

public class QuestionContextDto
{
    [JsonPropertyName("instruction_text")]
    public string InstructionText { get; set; } = string.Empty;

    [JsonPropertyName("question_text")]
    public string QuestionText { get; set; } = string.Empty;

    [JsonPropertyName("question_type")]
    public string QuestionType { get; set; } = string.Empty;

    [JsonPropertyName("difficulty_level")]
    public string DifficultyLevel { get; set; } = string.Empty;

    [JsonPropertyName("duration_seconds")]
    public int DurationSeconds { get; set; }

    [JsonPropertyName("min_response_seconds")]
    public int MinResponseSeconds { get; set; }

    [JsonPropertyName("max_response_seconds")]
    public int MaxResponseSeconds { get; set; }

    [JsonPropertyName("evaluation_guide")]
    public EvaluationGuideDto? EvaluationGuide { get; set; }
}

public class EvaluationGuideDto
{
    [JsonPropertyName("expected_content")]
    public string ExpectedContent { get; set; } = string.Empty;

    [JsonPropertyName("key_points")]
    public string KeyPoints { get; set; } = string.Empty;

    [JsonPropertyName("acceptable_responses")]
    public string AcceptableResponses { get; set; } = string.Empty;

    [JsonPropertyName("off_topic_examples")]
    public string OffTopicExamples { get; set; } = string.Empty;

    [JsonPropertyName("scoring_hints")]
    public string ScoringHints { get; set; } = string.Empty;

    [JsonPropertyName("common_mistakes")]
    public string CommonMistakes { get; set; } = string.Empty;
}
