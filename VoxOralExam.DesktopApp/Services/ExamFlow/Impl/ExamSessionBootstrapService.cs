using VoxOralExam.Core.Context;
using VoxOralExam.Core.Interfaces;
using VoxOralExam.DesktopApp.State;

using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.Services.DomainService;

namespace VoxOralExam.DesktopApp.Services.ExamFlow.Impl;

public class ExamSessionBootstrapService : IExamSessionBootstrapService
{
    private readonly IExamApiService _examApi;
    private readonly ExamSessionState _sessionState;
    private readonly RealtimeAttemptProgressClient _attemptProgressClient;

    public ExamSessionBootstrapService(
        IExamApiService examApi,
        ExamSessionState sessionState,
        RealtimeAttemptProgressClient attemptProgressClient)
    {
        _examApi = examApi;
        _sessionState = sessionState;
        _attemptProgressClient = attemptProgressClient;
    }

    public async Task EnterWithTicketAsync(ExamEntryTicket ticket, CancellationToken ct = default)
    {
        _sessionState.EntryTicket = ticket;
        _sessionState.ExamAttemptId = ticket.AttemptId;
        _sessionState.SessionId = string.IsNullOrWhiteSpace(ticket.SessionId) ? ticket.AttemptId.ToString("D") : ticket.SessionId;
        _sessionState.ScheduleId = ticket.ScheduleId;
        var paper = await _examApi.GetExamPaperAsync(ticket.AttemptId.ToString(), ct);
        _sessionState.LoadExamPaper(paper, ticket.AttemptId);
        await ResumeQuestionIndexIfNeededAsync(ct);
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

