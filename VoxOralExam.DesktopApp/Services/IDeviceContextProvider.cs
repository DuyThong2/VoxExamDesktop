using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services;

public interface IDeviceContextProvider
{
    LoginDeviceContext GetCurrentDevice();
}
