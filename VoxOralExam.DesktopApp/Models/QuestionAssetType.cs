using System.Text.Json.Serialization;

namespace VoxOralExam.DesktopApp.Models;

public enum QuestionAssetType
{
    [JsonStringEnumMemberName("AUDIO")]
    Audio,

    [JsonStringEnumMemberName("IMAGE")]
    Image,

    [JsonStringEnumMemberName("VIDEO")]
    Video,

    [JsonStringEnumMemberName("TEXT_PASSAGE")]
    TextPassage
}
