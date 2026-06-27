using VoxOralExam.DesktopApp.Models;

namespace VoxOralExam.DesktopApp.State;

public class ExamSessionState
{
    public string SessionId { get; set; } = string.Empty;
    public int SelectedAudioInputDeviceIndex { get; set; }
    public string SelectedAudioInputDeviceName { get; set; } = string.Empty;
    public Guid ExamId { get; set; }
    public Guid ExamPaperId { get; set; }
    public Guid ExamAttemptId { get; set; }
    public string ExamTitle { get; set; } = string.Empty;
    public int DurationMinutes { get; set; } = 30;
    public int QuestionIndex { get; set; }
    public List<Question> Questions { get; set; } = [];
    public Dictionary<Guid, Guid> AttemptAnswerIdsByQuestionId { get; set; } = [];
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

    public void LoadMockExam(MockExamPaper mockExamPaper)
    {
        ExamId = mockExamPaper.ExamId;
        ExamPaperId = mockExamPaper.ExamPaperId;
        ExamAttemptId = Guid.NewGuid();
        SessionId = ExamAttemptId.ToString();
        ExamTitle = mockExamPaper.Title;
        DurationMinutes = mockExamPaper.DurationMinutes;
        QuestionIndex = 0;
        Questions = mockExamPaper.PaperQuestions
            .OrderBy(item => item.OrderIndex)
            .Select(item => item.Question)
            .ToList();
        AttemptAnswerIdsByQuestionId = mockExamPaper.PaperQuestions
            .ToDictionary(item => item.Question.Id, _ => Guid.NewGuid());
        EvaluationGuidesByQuestionId = mockExamPaper.PaperQuestions
            .Where(item => item.EvaluationGuide is not null)
            .ToDictionary(item => item.Question.Id, item => item.EvaluationGuide!);
    }
}
