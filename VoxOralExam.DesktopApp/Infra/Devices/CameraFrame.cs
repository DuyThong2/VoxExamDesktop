namespace VoxOralExam.DesktopApp.Infra.Devices;

public sealed record CameraFrame(
    byte[] Data,
    int Width,
    int Height,
    int Stride,
    TimeSpan Timestamp);
