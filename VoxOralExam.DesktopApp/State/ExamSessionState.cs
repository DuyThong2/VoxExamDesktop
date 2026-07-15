using VoxOralExam.DesktopApp.Models;

namespace VoxOralExam.DesktopApp.State;

public class ExamSessionState
{
    public string SessionId { get; set; } = string.Empty;
    public int SelectedAudioInputDeviceIndex { get; set; }
    public string SelectedAudioInputDeviceName { get; set; } = string.Empty;
    public int SelectedAudioOutputDeviceIndex { get; set; }
    public string SelectedAudioOutputDeviceName { get; set; } = string.Empty;
    public Exam? SelectedExam { get; set; }
    public ExamEntryTicket? EntryTicket { get; set; }

    public Guid ExamId { get; set; }
    public Guid ExamPaperId { get; set; }
    public Guid ExamAttemptId { get; set; }
    public string ExamTitle { get; set; } = string.Empty;
    public int DurationMinutes { get; set; } = 30;
    public int QuestionIndex { get; set; }
    public List<Question> Questions { get; set; } = [];
    public Dictionary<Guid, Guid> AttemptAnswerIdsByQuestionId { get; set; } = [];
    public Dictionary<Guid, Guid> PaperItemIdsByQuestionId { get; set; } = [];
    public Dictionary<Guid, QuestionEvaluationGuide> EvaluationGuidesByQuestionId { get; set; } = [];

    public AuthenticatedUserContext? CurrentUser { get; private set; }
    public bool IsAuthenticated => CurrentUser is not null;

    public Question? CurrentQuestion =>
        Questions.Count == 0 || QuestionIndex >= Questions.Count
            ? null
            : Questions[QuestionIndex];

    public bool HasNextQuestion => Questions.Count > 0 && QuestionIndex < Questions.Count - 1;

    public void SetAuthenticatedUser(AuthenticatedUserContext userContext)
    {
        CurrentUser = userContext;
    }

    public void ClearAuthenticatedUser()
    {
        CurrentUser = null;
    }

    public void LoadExamPaper(ExamPaper examPaper, Guid? attemptId = null)
    {
        ExamId = examPaper.ExamId;
        ExamPaperId = examPaper.ExamPaperId;
        ExamAttemptId = attemptId
            ?? EntryTicket?.AttemptId
            ?? (examPaper.ExamAttemptId != Guid.Empty ? examPaper.ExamAttemptId : Guid.NewGuid());
        SessionId = ExamAttemptId.ToString();
        ExamTitle = examPaper.Title;
        DurationMinutes = examPaper.DurationMinutes;
        QuestionIndex = 0;
        Questions = examPaper.PaperQuestions
            .OrderBy(item => item.OrderIndex)
            .Select(item =>
            {
                item.Question.SectionId = item.SectionId;
                item.Question.SectionTitle = item.SectionTitle;
                item.Question.SectionInstruction = item.SectionInstruction;
                return item.Question;
            })
            .ToList();
        AttemptAnswerIdsByQuestionId = examPaper.PaperQuestions
            .ToDictionary(
                item => item.Question.Id,
                item => item.AttemptAnswerId != Guid.Empty ? item.AttemptAnswerId : Guid.NewGuid());
        PaperItemIdsByQuestionId = examPaper.PaperQuestions
            .ToDictionary(item => item.Question.Id, item => item.Id);
        EvaluationGuidesByQuestionId = examPaper.PaperQuestions
            .Where(item => item.EvaluationGuide is not null)
            .ToDictionary(item => item.Question.Id, item => item.EvaluationGuide!);
    }
}
