using VoxOralExam.Core.Context;

namespace VoxOralExam.DesktopApp.Services.DomainService;

public interface IAuthApiService
{
    Task<AuthenticatedUserContext> LoginAsync(string login, string password, LoginDeviceContext deviceContext, CancellationToken cancellationToken = default);
}

