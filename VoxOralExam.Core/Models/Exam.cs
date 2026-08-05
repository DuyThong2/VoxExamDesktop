namespace VoxOralExam.Core.Models;

public class Exam
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Duration { get; set; }
    public DateTimeOffset? ExamDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public ExamKind Kind { get; set; } = ExamKind.Centralized;
    public bool RequiresOtp { get; set; } = true;
    public int? MaxAttempt { get; set; }
    public int AttemptsUsed { get; set; }
    public bool CanEnter { get; set; } = true;
    public string EntryMessage { get; set; } = string.Empty;
}

