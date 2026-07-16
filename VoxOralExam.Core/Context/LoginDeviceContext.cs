namespace VoxOralExam.Core.Context;

public class LoginDeviceContext
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Platform { get; set; } = "WEB";
}
