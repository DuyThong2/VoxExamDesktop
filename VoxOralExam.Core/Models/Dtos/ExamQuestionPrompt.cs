namespace VoxOralExam.Core.Models.Dtos;

public class ExamQuestionPrompt
{
    public Guid QuestionId { get; set; }
    public string InstructionText { get; set; } = string.Empty;
    public string QuestionText { get; set; } = string.Empty;
    public int MinResponseSeconds { get; set; }
    public int MaxResponseSeconds { get; set; }
    public int QuestionNumber { get; set; }
    public int TotalQuestions { get; set; }
}
