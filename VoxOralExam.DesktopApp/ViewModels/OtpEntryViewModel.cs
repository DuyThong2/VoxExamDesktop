using System.Windows.Input;
using System.Windows.Threading;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.ViewModels;

/// <summary>
/// Stage: OtpEntry. The student types the OTP shown on the proctor's web screen (it rotates every
/// <see cref="AppSettings.OtpRefreshSeconds"/> seconds); this screen submits it for verification and,
/// on success, stores the entry ticket and advances. The app never generates or fetches the code --
/// it only submits what the student typed. The real HTTP call lives behind
/// <see cref="IExamEntryApiService"/> (mock in dev, TODO Java impl for production).
/// </summary>
public class OtpEntryViewModel : BaseViewModel
{
    private readonly IExamEntryNavigator _navigator;
    private readonly ExamSessionState _sessionState;
    private readonly IExamEntryApiService _entryApi;
    private readonly AppSettings _settings;

    private readonly DispatcherTimer _refreshTimer;

    private string _otp = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _hasError;
    private bool _isVerifying;
    private int _secondsUntilRefresh;

    public OtpEntryViewModel(
        IExamEntryNavigator navigator,
        ExamSessionState sessionState,
        IExamEntryApiService entryApi,
        AppSettings settings)
    {
        _navigator = navigator;
        _sessionState = sessionState;
        _entryApi = entryApi;
        _settings = settings;

        _secondsUntilRefresh = RefreshSeconds;

        VerifyCommand = new RelayCommand(() => _ = VerifyAsync(), CanVerify);
        BackCommand = new RelayCommand(() => _navigator.Back());

        // Visualise the 60s rotation so the student knows the code they see will change. This is a
        // local countdown, not synced to the server's rotation boundary.
        // TODO(§C): sync the countdown to the server (e.g. a nextRotationAt timestamp) so it matches
        // the proctor screen exactly instead of starting fresh when this view opens.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();
    }

    public string ExamTitle => _sessionState.SelectedExam?.Title ?? "(chưa chọn bài thi)";

    public int OtpLength => _settings.OtpLength;

    public string Otp
    {
        get => _otp;
        set
        {
            if (SetProperty(ref _otp, value))
            {
                HasError = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
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

    public bool IsVerifying
    {
        get => _isVerifying;
        set => SetProperty(ref _isVerifying, value);
    }

    public int SecondsUntilRefresh
    {
        get => _secondsUntilRefresh;
        private set => SetProperty(ref _secondsUntilRefresh, value);
    }

    public int RefreshSeconds => _settings.OtpRefreshSeconds;

    public ICommand VerifyCommand { get; }
    public ICommand BackCommand { get; }

    /// <summary>Stop the countdown when the view leaves the screen (navigated away or verified).</summary>
    public void Cleanup()
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTick;
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        SecondsUntilRefresh = SecondsUntilRefresh <= 1 ? RefreshSeconds : SecondsUntilRefresh - 1;
    }

    private bool CanVerify()
        => !IsVerifying && _otp.Length == OtpLength;

    private async Task VerifyAsync()
    {
        if (!CanVerify())
        {
            return;
        }

        IsVerifying = true;
        HasError = false;
        ErrorMessage = string.Empty;
        CommandManager.InvalidateRequerySuggested();

        var examId = _sessionState.SelectedExam?.Id ?? string.Empty;
        LocalFileLogger.Info("otp", "verify_begin", new { examId });

        try
        {
            var ticket = await _entryApi.VerifyOtpAsync(examId, _otp);
            _sessionState.EntryTicket = ticket;
            LocalFileLogger.Info("otp", "verify_success", new { examId, ticket.TicketId });

            Cleanup();
            _navigator.GoTo(ExamEntryStage.SystemCheck);
        }
        catch (OtpVerificationException ex)
        {
            // Wrong or already-rotated code -- let the student read the current one and retry.
            ErrorMessage = ex.Message;
            HasError = true;
            Otp = string.Empty;
            LocalFileLogger.Info("otp", "verify_rejected", new { examId, reason = ex.Message });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không xác thực được OTP: {ex.Message}";
            HasError = true;
            LocalFileLogger.Error("otp", "verify_failed", ex, new { examId });
        }
        finally
        {
            IsVerifying = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
