namespace VoxOralExam.DesktopApp.Models;

public class ProctoringEvent
{
    public string SessionId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;       // "PERSON_MISSING", "OBJECT_DETECTED", "MULTIPLE_PERSONS"
    public string Message { get; set; } = string.Empty;     // từ Python "message" field
    public float Confidence { get; set; }
    public string Timestamp { get; set; } = string.Empty;   // ISO 8601 từ Python
    public string? Object { get; set; }                     // "cell phone", "book"...
    public int? PersonCount { get; set; }                   // MULTIPLE_PERSONS
}

public class YoloDetection
{
    public string Label { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public BoundingBox BBox { get; set; } = new();
}

public class BoundingBox
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
}
