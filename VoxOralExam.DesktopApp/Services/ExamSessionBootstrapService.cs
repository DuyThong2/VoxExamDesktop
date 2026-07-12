using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services;

public class ExamSessionBootstrapService : IExamSessionBootstrapService
{
    private readonly IExamApiService _examApi;
    private readonly ExamSessionState _sessionState;

    public ExamSessionBootstrapService(IExamApiService examApi, ExamSessionState sessionState)
    {
        _examApi = examApi;
        _sessionState = sessionState;
    }

    public async Task EnterWithTicketAsync(ExamEntryTicket ticket, CancellationToken ct = default)
    {
        _sessionState.EntryTicket = ticket;
        var paper = await _examApi.GetExamPaperAsync(ticket.AttemptId.ToString(), ct);
        _sessionState.LoadExamPaper(paper, ticket.AttemptId);
    }
}
