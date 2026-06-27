namespace VoxOralExam.DesktopApp.Models;

public class QuestionEvaluationGuide
{
    public Guid Id { get; set; }
    public Guid QuestionId { get; set; }
    public string ExpectedContent { get; set; } = string.Empty;
    public string KeyPoints { get; set; } = string.Empty;
    public string AcceptableResponses { get; set; } = string.Empty;
    public string OffTopicExamples { get; set; } = string.Empty;
    public string ScoringHints { get; set; } = string.Empty;
    public string CommonMistakes { get; set; } = string.Empty;
}
