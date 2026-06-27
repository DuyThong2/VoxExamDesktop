namespace VoxOralExam.DesktopApp.Models;

public class ExamPaperQuestion
{
    public Guid Id { get; set; }
    public int OrderIndex { get; set; }
    public Guid AttemptAnswerId { get; set; }
    public QuestionEvaluationGuide? EvaluationGuide { get; set; }
    public Question Question { get; set; } = new();
}
