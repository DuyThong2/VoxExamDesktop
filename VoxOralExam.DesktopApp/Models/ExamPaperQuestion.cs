namespace VoxOralExam.DesktopApp.Models;

public class ExamPaperQuestion
{
    public Guid Id { get; set; }
    public int OrderIndex { get; set; }
    public Guid? SectionId { get; set; }
    public string SectionTitle { get; set; } = string.Empty;
    public string SectionInstruction { get; set; } = string.Empty;
    public Guid AttemptAnswerId { get; set; }
    public QuestionEvaluationGuide? EvaluationGuide { get; set; }
    public Question Question { get; set; } = new();
}
