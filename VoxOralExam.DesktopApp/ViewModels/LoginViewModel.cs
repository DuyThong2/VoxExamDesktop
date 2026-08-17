using System.Windows.Input;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.Services.DomainService;
using VoxOralExam.DesktopApp.Services.EntryFlow;
using VoxOralExam.DesktopApp.State;

using VoxOralExam.DesktopApp.Services;

namespace VoxOralExam.DesktopApp.ViewModels;

/// <summary>
/// Stage: Login. Authenticates the student against Java and advances to the exam list. Device tests
/// used to live here; they moved to DevicePreflight (after OTP) -- see docs/wpf-redesign-plan.md Â§A.
/// </summary>
public class LoginViewModel : BaseViewModel
{
    private readonly IAuthApiService _authApiService;
    private readonly IDeviceContextProvider _deviceContextProvider;
    private readonly ExamSessionState _sessionState;
    private readonly IExamEntryNavigator _navigator;

    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _hasError;
    private bool _isLoggingIn;

    public LoginViewModel(
        IAuthApiService authApiService,
        IDeviceContextProvider deviceContextProvider,
        ExamSessionState sessionState,
        IExamEntryNavigator navigator)
    {
        _authApiService = authApiService;
        _deviceContextProvider = deviceContextProvider;
        _sessionState = sessionState;
        _navigator = navigator;
        // Email để trống: mỗi thí sinh đăng nhập bằng tài khoản của chính mình, điền sẵn một
        // địa chỉ demo chỉ khiến người dùng thật phải xoá đi trước khi gõ.
        //
        // Mật khẩu vẫn điền sẵn theo yêu cầu -- tiện cho demo, vì toàn bộ tài khoản seed đều
        // dùng chung DEMO_DATA_PASSWORD. GỠ dòng dưới trước khi giao máy cho kỳ thi thật.
        Password = "Password@123";
        LoginCommand = new RelayCommand(ExecuteLogin, CanLogin);
    }

    public string Email
    {
        get => _email;
        set => SetProperty(ref _email, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public bool HasError
    {
        get => _hasError;
        set => SetProperty(ref _hasError, value);
    }

    public ICommand LoginCommand { get; }

    private bool CanLogin()
    {
        return !string.IsNullOrWhiteSpace(Email)
            && !string.IsNullOrWhiteSpace(Password)
            && !_isLoggingIn;
    }

    private async void ExecuteLogin()
    {
        _isLoggingIn = true;
        HasError = false;
        ErrorMessage = string.Empty;
        LocalFileLogger.Info("login", "login_begin", new { email = Email.Trim() });
        CommandManager.InvalidateRequerySuggested();

        try
        {
            var device = _deviceContextProvider.GetCurrentDevice();
            var userContext = await _authApiService.LoginAsync(Email.Trim(), Password, device);

            _sessionState.SetAuthenticatedUser(userContext);
            LocalFileLogger.Info("login", "login_success", new
            {
                userContext.UserId,
                userContext.Email,
                userContext.DisplayName
            });

            // Login only authenticates now; device checks happen later in DevicePreflight (after OTP).
            _navigator.GoTo(ExamEntryStage.ExamList);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Đăng nhập thất bại: {ex.Message}";
            HasError = true;
            LocalFileLogger.Error("login", "login_failed", ex, new
            {
                email = Email.Trim()
            });
        }
        finally
        {
            _isLoggingIn = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
}

public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke((T?)parameter) ?? true;
    public void Execute(object? parameter) => _execute((T?)parameter);
}

