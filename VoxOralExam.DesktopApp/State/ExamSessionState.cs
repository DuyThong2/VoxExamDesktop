using System.Security.Cryptography;
using VoxOralExam.Core.Context;
using VoxOralExam.Core.Models;

namespace VoxOralExam.DesktopApp.State;

public class ExamSessionState
{
    public string SessionId { get; set; } = string.Empty;
    public string ScheduleId { get; set; } = string.Empty;
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
    public int DurationSeconds { get; set; } = 30 * 60;
    public int DurationMinutes { get; set; } = 30;
    public DateTimeOffset? ScheduleEndAt { get; set; }
    /// <summary>
    /// Attempt's real start time from the server. The countdown itself is restored from
    /// RemainingSeconds so offline time is not deducted.
    /// </summary>
    public DateTimeOffset? StartedAt { get; set; }
    public int? RemainingSeconds { get; set; }
    public int? ResumeTurnOrder { get; set; }
    public string? ResumeActivePromptText { get; set; }
    public double ResumeSpokenSeconds { get; set; }
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

    public string StreamJwt => EntryTicket?.StreamJwt ?? string.Empty;

    public void SetAuthenticatedUser(AuthenticatedUserContext userContext)
    {
        CurrentUser = userContext;
    }

    /// <summary>
    /// Ends the signed-in session and blanks its credentials.
    ///
    /// <para>Blanking rather than only dropping the reference, because the credentials outlived the
    /// reference: the app now holds a REFRESH token, not just a short-lived access token, and
    /// anything that captured the context object (a client mid-request, a queued upload) would go on
    /// holding a working one. Null-ing the property alone leaves those copies usable.</para>
    ///
    /// <para>Deliberately NOT called when an exam is submitted. Evidence upload outlives the exam --
    /// SegmentUploadWorker keeps draining buffered segments and UploadCredentialRefresher renews
    /// their credentials from Java, both of which need this token. Clearing at submit would recreate
    /// the stranded-evidence bug from the other direction.</para>
    ///
    /// <para>This is the LOCAL half only. Revoking the session server-side is a separate step, and
    /// the one that actually matters: without it the refresh token stays valid at vox for its full
    /// 72-hour TTL (AuthController.REFRESH_TOKEN_COOKIE_TTL_SECONDS) no matter what is cleared here.
    /// AuthSessionManager.SignOutAsync pairs the two -- POST /api/v1/auth/logout, then this -- and
    /// App.OnExit goes through it. Calling this directly is for the cases where there is nothing to
    /// revoke, or nothing that can be.</para>
    ///
    /// <para>Even a successful revoke leaves the ACCESS token usable until its own expiry (~15 min):
    /// JwtAuthenticationFilter carries no session claim and never re-checks revocation. That is why
    /// signing out cannot strand an upload still draining, and equally why it is not instant.</para>
    /// </summary>
    public void ClearAuthenticatedUser()
    {
        var user = CurrentUser;
        CurrentUser = null;
        if (user is null)
        {
            return;
        }

        user.AccessToken = string.Empty;
        user.RefreshToken = string.Empty;
        user.XsrfToken = string.Empty;
    }

    public void LoadExamPaper(ExamPaper examPaper, Guid? attemptId = null)
    {
        ExamId = examPaper.ExamId;
        ExamPaperId = examPaper.ExamPaperId;
        ExamAttemptId = attemptId
            ?? EntryTicket?.AttemptId
            ?? (examPaper.ExamAttemptId != Guid.Empty ? examPaper.ExamAttemptId : Guid.NewGuid());
        SessionId = !string.IsNullOrWhiteSpace(EntryTicket?.SessionId)
            ? EntryTicket.SessionId
            : ExamAttemptId.ToString();
        ExamTitle = examPaper.Title;
        DurationSeconds = examPaper.DurationSeconds > 0
            ? examPaper.DurationSeconds
            : Math.Max(1, examPaper.DurationMinutes) * 60;
        DurationMinutes = examPaper.DurationMinutes > 0
            ? examPaper.DurationMinutes
            : Math.Max(1, (int)Math.Ceiling(DurationSeconds / 60.0));
        ScheduleEndAt = examPaper.ScheduleEndAt ?? EntryTicket?.ScheduleEndAt;
        StartedAt = examPaper.StartedAt;
        RemainingSeconds = examPaper.RemainingSeconds;
        ResumeTurnOrder = null;
        ResumeActivePromptText = null;
        ResumeSpokenSeconds = 0;
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
                item => item.AttemptAnswerId != Guid.Empty
                    ? item.AttemptAnswerId
                    : CreateDeterministicAnswerId(
                        ExamAttemptId,
                        item.Id != Guid.Empty ? item.Id : item.Question.Id));
        PaperItemIdsByQuestionId = examPaper.PaperQuestions
            .ToDictionary(item => item.Question.Id, item => item.Id);
        EvaluationGuidesByQuestionId = examPaper.PaperQuestions
            .Where(item => item.EvaluationGuide is not null)
            .ToDictionary(item => item.Question.Id, item => item.EvaluationGuide!);
    }

    private static Guid CreateDeterministicAnswerId(Guid attemptId, Guid paperItemId)
    {
        Span<byte> source = stackalloc byte[32];
        attemptId.TryWriteBytes(source[..16]);
        paperItemId.TryWriteBytes(source[16..]);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(source, hash);
        Span<byte> guidBytes = hash[..16];
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }
}
