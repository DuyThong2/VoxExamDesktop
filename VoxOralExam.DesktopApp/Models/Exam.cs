namespace VoxOralExam.DesktopApp.Models;

public class Exam
{
    public string   Id          { get; set; } = string.Empty;
    public string   Title       { get; set; } = string.Empty;
    public string   Subject     { get; set; } = string.Empty;
    public string   Description { get; set; } = string.Empty;
    public int       Duration    { get; set; }          // phút
    public DateTime? ExamDate    { get; set; }          // co the null (vd: thi sinh chua duoc gan ca thi)
    public string   Status      { get; set; } = string.Empty; // "upcoming", "in_progress", "completed"
}
