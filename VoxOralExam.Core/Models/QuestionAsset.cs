namespace VoxOralExam.Core.Models;

public class QuestionAsset
{
    public Guid Id { get; set; }
    public Guid QuestionId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? DurationSeconds { get; set; }
    public string AltText { get; set; } = string.Empty;
    public QuestionAssetType Type { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Transcript { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Order { get; set; }
}
