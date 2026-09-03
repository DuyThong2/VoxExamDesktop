using System.Windows.Input;
using VoxOralExam.Core.Context;
using VoxOralExam.DesktopApp.Infra.Clients.Google;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.Services.DomainService;
using VoxOralExam.DesktopApp.Services.EntryFlow;
using VoxOralExam.DesktopApp.State;

using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.Services.ExamFlow;

namespace VoxOralExam.DesktopApp.ViewModels;

/// <summary>
/// Stage: Login. Authenticates the student against Java and advances to the exam list. Device tests
/// used to live here; they moved to DevicePreflight (after OTP) -- see docs/wpf-redesign-plan.md §A.
/// </summary>
public class LoginViewModel : BaseViewModel
{
    private readonly IAuthApiService _authApiService;
    private readonly IDeviceContextProvider _deviceContextProvider;
    private readonly ExamSessionState _sessionState;
    private readonly IExamEntryNavigator _navigator;
    private readonly PendingSubmissionStore _pendingSubmissions;
    private readonly IGoogleSignInClient _googleSignInClient;
    private readonly AppSettings _settings;

    private string _email = string.Empty;
    private string _password = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _hasError;
    private bool _isLoggingIn;

    public LoginViewModel(
        IAuthApiService authApiService,
        IDeviceContextProvider deviceContextProvider,
        ExamSessionState sessionState,
        IExamEntryNavigator navigator,
        PendingSubmissionStore pendingSubmissions,
        IGoogleSignInClient googleSignInClient,
        AppSettings settings)
    {
        _authApiService = authApiService;
        _deviceContextProvider = deviceContextProvider;
        _sessionState = sessionState;
        _navigator = navigator;
        _pendingSubmissions = pendingSubmissions;
        _googleSignInClient = googleSignInClient;
        _settings = settings;
        // Email để trống: mỗi thí sinh đăng nhập bằng tài khoản của chính mình, điền sẵn một
        // địa chỉ demo chỉ khiến người dùng thật phải xoá đi trước khi gõ.
        //
        // Mật khẩu vẫn điền sẵn theo yêu cầu -- tiện cho demo, vì toàn bộ tài khoản seed đều
        // dùng chung DEMO_DATA_PASSWORD. GỠ dòng dưới trước khi giao máy cho kỳ thi thật.
        Password = "Password@123";
        LoginCommand = new RelayCommand(ExecuteLogin, CanLogin);
        GoogleLoginCommand = new RelayCommand(ExecuteGoogleLogin, () => !_isLoggingIn);
    }

    /// <summary>
    /// Hides the Google button when no client id is configured, rather than showing one that always
    /// fails. A build pointed at an environment without Google set up still logs in by password.
    /// </summary>
    public bool IsGoogleSignInAvailable => !string.IsNullOrWhiteSpace(_settings.GoogleClientId);

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

    public ICommand GoogleLoginCommand { get; }

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

            CompleteSignIn(userContext);
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

    /// <summary>
    /// Google sign-in: the browser half runs locally, then the resulting ID token is exchanged for a
    /// vox session server-side.
    ///
    /// <para>A null token means the student closed the browser or pressed Cancel. That is a decision,
    /// not a failure, so it leaves the form exactly as it was -- painting a red error under a button
    /// somebody deliberately backed out of only makes them think they broke something.</para>
    /// </summary>
    private async void ExecuteGoogleLogin()
    {
        _isLoggingIn = true;
        HasError = false;
        ErrorMessage = string.Empty;
        LocalFileLogger.Info("login", "google_login_begin");
        CommandManager.InvalidateRequerySuggested();

        try
        {
            var idToken = await _googleSignInClient.AcquireIdTokenAsync();
            if (idToken is null)
            {
                LocalFileLogger.Info("login", "google_login_cancelled");
                return;
            }

            var device = _deviceContextProvider.GetCurrentDevice();
            var userContext = await _authApiService.LoginWithGoogleAsync(idToken, device);

            CompleteSignIn(userContext);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Đăng nhập Google thất bại: {ex.Message}";
            HasError = true;
            // No email to log: with Google the app never sees one until the exchange succeeds, and
            // the failure being diagnosed is usually that it did not.
            LocalFileLogger.Error("login", "google_login_failed", ex);
        }
        finally
        {
            _isLoggingIn = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Everything that happens once a session exists, whichever way it was obtained.
    ///
    /// <para>Shared rather than duplicated per sign-in method: the pending-submission replay below is
    /// easy to leave out of a second copy, and leaving it out is invisible -- the app works, and a
    /// previous run's unsent result simply stays unsent forever.</para>
    /// </summary>
    private void CompleteSignIn(AuthenticatedUserContext userContext)
    {
        _sessionState.SetAuthenticatedUser(userContext);

        // The first moment this app has a token, and therefore the first moment a status left
        // owing by a previous run can actually be sent. Deliberately NOT in App's startup sweep
        // alongside OrphanedUploadRecovery: nobody is signed in there, so the PATCH could only
        // fail. Fire-and-forget -- a student waiting to sit an exam must never queue behind the
        // bookkeeping of an earlier one.
        _ = Task.Run(() => _pendingSubmissions.ReplayAsync(CancellationToken.None));

        LocalFileLogger.Info("login", "login_success", new
        {
            userContext.UserId,
            userContext.Email,
            userContext.DisplayName
        });

        // Login only authenticates now; device checks happen later in DevicePreflight (after OTP).
        _navigator.GoTo(ExamEntryStage.ExamList);
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

