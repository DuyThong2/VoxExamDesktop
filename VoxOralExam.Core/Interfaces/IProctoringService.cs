using VoxOralExam.Core.Models;

namespace VoxOralExam.Core.Interfaces;

public interface IProctoringService
{
    event Action<string>? OnStatusChanged;
    event Action<ProctoringEvent>? OnProctoringEvent;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}
