using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services;

public interface IExamSessionBootstrapService
{
    Task EnterWithTicketAsync(ExamEntryTicket ticket, CancellationToken ct = default);
}
