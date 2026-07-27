namespace VoxOralExam.Core.Models;

/// <summary>
/// One exam paper (the full question set for an attempt). Formerly named MockExamPaper -- it is the
/// shared shape returned by IExamApiService, whether the data comes from the real Java backend or
/// the dev-only mock factory, so it no longer carries "Mock" in its name.
/// </summary>
public class ExamPaper
{
    public Guid ExamId { get; set; }
    public Guid ExamPaperId { get; set; }
    public Guid ExamAttemptId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public int DurationMinutes { get; set; }
    public DateTime? ExamDate { get; set; }
    public DateTime? ScheduleEndAt { get; set; }
    /// <summary>
    /// When this attempt's session started server-side. Kept as session metadata;
    /// the countdown resumes from RemainingSeconds instead of a fixed deadline.
    /// </summary>
    public DateTime? StartedAt { get; set; }
    /// <summary>
    /// Server checkpoint of the countdown. Null means this attempt has never
    /// checkpointed and should start from DurationSeconds.
    /// </summary>
    public int? RemainingSeconds { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<ExamPaperQuestion> PaperQuestions { get; set; } = [];
}

