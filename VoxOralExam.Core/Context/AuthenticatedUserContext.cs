namespace VoxOralExam.Core.Context;

public class AuthenticatedUserContext
{
    public string Login { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public string DateOfBirth { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string AvatarUrl { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string TokenType { get; set; } = "Bearer";
    public List<string> Roles { get; set; } = [];
    public string RawResponseJson { get; set; } = string.Empty;
    public LoginDeviceContext Device { get; set; } = new();
}
