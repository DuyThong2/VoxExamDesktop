using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services;

public interface IAuthApiService
{
    Task<AuthenticatedUserContext> LoginAsync(string login, string password, LoginDeviceContext deviceContext, CancellationToken cancellationToken = default);
}
