using VoxOralExam.Core.Context;
using VoxOralExam.Core.Interfaces;
using VoxOralExam.DesktopApp.State;

using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Infra.Clients.StreamService;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.Services.DomainService;

namespace VoxOralExam.DesktopApp.Services.ExamFlow.Impl;

public class ExamSessionBootstrapService : IExamSessionBootstrapService
{
    private readonly IExamApiService _examApi;
    private readonly ExamSessionState _sessionState;
    private readonly RealtimeAttemptProgressClient _attemptProgressClient;
    private readonly StudentStreamAccessClient _streamAccessClient;
    private readonly DevStreamTokenClient _devStreamTokenClient;
    private readonly AppSettings _settings;

    public ExamSessionBootstrapService(
        IExamApiService examApi,
        ExamSessionState sessionState,
        RealtimeAttemptProgressClient attemptProgressClient,
        StudentStreamAccessClient streamAccessClient,
        DevStreamTokenClient devStreamTokenClient,
        AppSettings settings)
    {
        _examApi = examApi;
        _sessionState = sessionState;
        _attemptProgressClient = attemptProgressClient;
        _streamAccessClient = streamAccessClient;
        _devStreamTokenClient = devStreamTokenClient;
        _settings = settings;
    }

    public async Task EnterWithTicketAsync(ExamEntryTicket ticket, CancellationToken ct = default)
    {
        if (!_settings.UseMockData)
        {
            var access = await _streamAccessClient.IssueAsync(ticket.AttemptId, preferredStreamType: null, ct);
            ApplyStreamAccess(ticket, access.Token, access.ScheduleId, access.SessionId, access.StreamTypes, access.ExpiresAt);
        }
        else if (_settings.UseDevStreamToken)
        {
            // Mock exam content, but a real signed JWT from vox-streaming/demo/devserver -- see
            // AppSettings.UseDevStreamToken's doc comment. Reuses the mock ticket's own
            // schedule/session/attempt ids so LocalSegmentStore's identity check (and a resumed
            // run) stay consistent across the whole flow.
            var access = await _devStreamTokenClient.IssueAsync(
                ticket.ScheduleId,
                ticket.SessionId,
                ticket.AttemptId.ToString("D"),
                ticket.StreamTypes,
                TimeSpan.FromHours(2),
                ct);
            ApplyStreamAccess(ticket, access.Token, access.ScheduleId, access.SessionId, access.StreamTypes, access.ExpiresAt);
        }
        _sessionState.EntryTicket = ticket;
        _sessionState.ExamAttemptId = ticket.AttemptId;
        _sessionState.SessionId = string.IsNullOrWhiteSpace(ticket.SessionId) ? ticket.AttemptId.ToString("D") : ticket.SessionId;
        _sessionState.ScheduleId = ticket.ScheduleId;
        var paper = await _examApi.GetExamPaperAsync(ticket.AttemptId.ToString(), ct);
        _sessionState.LoadExamPaper(paper, ticket.AttemptId);
        await ResumeQuestionIndexIfNeededAsync(ct);
    }

    private static void ApplyStreamAccess(
        ExamEntryTicket ticket,
        string token,
        string scheduleId,
        string sessionId,
        IReadOnlyList<string> streamTypes,
        DateTimeOffset expiresAt)
    {
        ticket.StreamJwt = token;
        ticket.ScheduleId = scheduleId;
        ticket.SessionId = sessionId;
        ticket.StreamTypes = streamTypes;
        ticket.StreamTokenExpiresAt = expiresAt;
    }

    /// <summary>
    /// LoadExamPaper always resets QuestionIndex to 0 -- correct for a brand-new attempt, wrong
    /// for re-entering one that already started (app fully closed and reopened, not just a WS
    /// reconnect within the same process, which RealtimeExamFlowService/RealtimeSessionClient
    /// already resume correctly on their own). Asks Python which answer_id this attempt was last
    /// on and, if found, maps it back to a question index via AttemptAnswerIdsByQuestionId so the
    /// exam flow starts at the right question instead of silently restarting from the first one.
    /// No-op (stays at index 0) if this is a genuinely fresh attempt, the lookup fails, or the
    /// returned answer_id doesn't match any question in this paper (defensive -- should not
    /// happen, but a bad resume-into-wrong-question is worse than just restarting from 0).
    /// </summary>
    private async Task ResumeQuestionIndexIfNeededAsync(CancellationToken ct)
    {
        var currentAnswerId = await _attemptProgressClient.GetCurrentAnswerIdAsync(_sessionState.ExamAttemptId, ct);
        if (currentAnswerId is null)
        {
            return;
        }

        var matchingQuestionId = _sessionState.AttemptAnswerIdsByQuestionId
            .Where(pair => pair.Value == currentAnswerId.Value)
            .Select(pair => (Guid?)pair.Key)
            .FirstOrDefault();
        if (matchingQuestionId is null)
        {
            LocalFileLogger.Info("exam_bootstrap", "current_answer_not_in_paper", new
            {
                _sessionState.ExamAttemptId,
                currentAnswerId
            });
            return;
        }

        var index = _sessionState.Questions.FindIndex(q => q.Id == matchingQuestionId.Value);
        if (index < 0)
        {
            return;
        }

        LocalFileLogger.Info("exam_bootstrap", "resuming_at_question_index", new
        {
            _sessionState.ExamAttemptId,
            currentAnswerId,
            questionIndex = index
        });
        _sessionState.QuestionIndex = index;
    }
}

