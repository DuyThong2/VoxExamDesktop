using System.Text.Json.Serialization;

namespace VoxOralExam.Core.Models;

public class Question
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string InstructionText { get; set; } = string.Empty;
    public string QuestionText { get; set; } = string.Empty;
    public string PromptText { get; set; } = string.Empty;
    public string PreparationText { get; set; } = string.Empty;
    public int PreparationTimeSeconds { get; set; }
    public int MinResponseSeconds { get; set; }
    public int MaxResponseSeconds { get; set; }
    public Guid? SectionId { get; set; }
    public string SectionTitle { get; set; } = string.Empty;
    public string SectionInstruction { get; set; } = string.Empty;
    public QuestionAsset? Asset { get; set; }

    // Mirrors Java's Question.type (QuestionType enum) â€” same entity, same position.
    public QuestionType Type { get; set; }

    // No Java equivalent (Question.java has no difficulty field; vox's only difficulty concept
    // is the unrelated, unused LevelDifficulty numeric value object). Kept here only because
    // Python's QuestionContext.difficulty_level wants an "easy"/"medium"/"hard" string.
    public string DifficultyLevel { get; set; } = string.Empty;
}

// Mirrors vox's domain.model.question.QuestionType exactly (same 5 members, same order).
// Java serializes enums as their UPPER_SNAKE_CASE name() (e.g. "SHORT_ANSWER"), not the
// PascalCase C# member name -- JsonStringEnumMemberName maps the wire string per member
// without renaming the C# identifiers used elsewhere in this codebase (MockExamDataFactory,
// RealtimeExamFlowService's separate lowercase snake_case mapping for Python).
public enum QuestionType
{
    [JsonStringEnumMemberName("READ_ALOUD")]
    ReadAloud,

    [JsonStringEnumMemberName("SHORT_ANSWER")]
    ShortAnswer,

    [JsonStringEnumMemberName("LONG_ANSWER")]
    LongAnswer,

    [JsonStringEnumMemberName("OPINION")]
    Opinion,

    [JsonStringEnumMemberName("DESCRIPTION")]
    Description
}

