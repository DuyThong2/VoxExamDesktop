using Microsoft.Win32;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.Services;

public class DeviceContextProvider : IDeviceContextProvider
{
    private readonly AppSettings _settings;

    public DeviceContextProvider(AppSettings settings)
    {
        _settings = settings;
    }

    public LoginDeviceContext GetCurrentDevice()
    {
        var machineName = Environment.MachineName;
        var userName = Environment.UserName;
        var osVersion = Environment.OSVersion.VersionString;
        var machineGuid = GetMachineGuid();

        return new LoginDeviceContext
        {
            DeviceId = string.IsNullOrWhiteSpace(machineGuid)
                ? $"{machineName}-{userName}".ToLowerInvariant()
                : machineGuid,
            DeviceName = $"{machineName} - Windows ({userName})",
            Platform = string.IsNullOrWhiteSpace(_settings.LoginPlatform) ? "DESKTOP" : _settings.LoginPlatform
        };
    }

    private static string? GetMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
