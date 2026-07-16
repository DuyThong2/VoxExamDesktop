using VoxOralExam.Core.Context;

namespace VoxOralExam.Core.Interfaces;

public interface IExamSessionBootstrapService
{
    Task EnterWithTicketAsync(ExamEntryTicket ticket, CancellationToken ct = default);
}



