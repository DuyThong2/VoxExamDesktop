namespace VoxOralExam.DesktopApp.Models;

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

    // Mirrors Java's Question.type (QuestionType enum) — same entity, same position.
    public QuestionType Type { get; set; }

    // No Java equivalent (Question.java has no difficulty field; vox's only difficulty concept
    // is the unrelated, unused LevelDifficulty numeric value object). Kept here only because
    // Python's QuestionContext.difficulty_level wants an "easy"/"medium"/"hard" string.
    public string DifficultyLevel { get; set; } = string.Empty;
}

// Mirrors vox's domain.model.question.QuestionType exactly (same 5 members, same order).
public enum QuestionType
{
    ReadAloud,
    ShortAnswer,
    LongAnswer,
    Opinion,
    Description
}
