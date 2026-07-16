using System.Text.Json.Serialization;

namespace VoxOralExam.Core.Models;

public enum ExamKind
{
    [JsonStringEnumMemberName("CENTRALIZED")]
    Centralized,

    [JsonStringEnumMemberName("CLASS_TEST")]
    ClassTest
}

