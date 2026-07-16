using VoxOralExam.Core.Context;

namespace VoxOralExam.DesktopApp.Infra.Devices;

public interface IDeviceContextProvider
{
    LoginDeviceContext GetCurrentDevice();
}

