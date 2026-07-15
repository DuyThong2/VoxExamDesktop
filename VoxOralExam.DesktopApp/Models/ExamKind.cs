using System.Text.Json.Serialization;

namespace VoxOralExam.DesktopApp.Models;

public enum ExamKind
{
    [JsonStringEnumMemberName("CENTRALIZED")]
    Centralized,

    [JsonStringEnumMemberName("CLASS_TEST")]
    ClassTest
}
