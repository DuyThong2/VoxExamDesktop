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

    // NOTE: ExamAttemptId is still minted client-side here. That is a known gap fixed in §C of
    // docs/wpf-redesign-plan.md (server-issued attempt id via the OTP entry ticket); left as-is for
    // this security/de-mock pass so behavior does not change.
    public void LoadExamPaper(ExamPaper examPaper)
    {
        ExamId = examPaper.ExamId;
        ExamPaperId = examPaper.ExamPaperId;
        ExamAttemptId = Guid.NewGuid();
        SessionId = ExamAttemptId.ToString();
        ExamTitle = examPaper.Title;
        DurationMinutes = examPaper.DurationMinutes;
        QuestionIndex = 0;
        Questions = examPaper.PaperQuestions
            .OrderBy(item => item.OrderIndex)
            .Select(item => item.Question)
            .ToList();
        AttemptAnswerIdsByQuestionId = examPaper.PaperQuestions
            .ToDictionary(item => item.Question.Id, _ => Guid.NewGuid());
        EvaluationGuidesByQuestionId = examPaper.PaperQuestions
            .Where(item => item.EvaluationGuide is not null)
            .ToDictionary(item => item.Question.Id, item => item.EvaluationGuide!);
    }
}
