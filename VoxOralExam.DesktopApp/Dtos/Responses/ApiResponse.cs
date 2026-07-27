namespace VoxOralExam.DesktopApp.Dtos.Responses;

public sealed record ApiResponse<T>(
    string Message,
    T Data
);
