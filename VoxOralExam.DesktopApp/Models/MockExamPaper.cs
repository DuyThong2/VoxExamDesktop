namespace VoxOralExam.DesktopApp.Models;

public class MockExamPaper
{
    public Guid ExamId { get; set; }
    public Guid ExamPaperId { get; set; }
    public Guid ExamAttemptId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int DurationMinutes { get; set; }
    public DateTime ExamDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<ExamPaperQuestion> PaperQuestions { get; set; } = [];
}
