using System.Windows.Input;
using System.Windows.Threading;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.ViewModels;

public class OtpEntryViewModel : BaseViewModel
{
    private readonly IExamEntryNavigator _navigator;
    private readonly ExamSessionState _sessionState;
    private readonly IExamEntryApiService _entryApi;
    private readonly IExamSessionBootstrapService _sessionBootstrapService;
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
        IExamSessionBootstrapService sessionBootstrapService,
        AppSettings settings)
    {
        _navigator = navigator;
        _sessionState = sessionState;
        _entryApi = entryApi;
        _sessionBootstrapService = sessionBootstrapService;
        _settings = settings;

        _secondsUntilRefresh = RefreshSeconds;

        VerifyCommand = new RelayCommand(() => _ = VerifyAsync(), CanVerify);
        BackCommand = new RelayCommand(() => _navigator.Back());

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();
    }

    public string ExamTitle => _sessionState.SelectedExam?.Title ?? "(chua chon bai thi)";

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
            await _sessionBootstrapService.EnterWithTicketAsync(ticket);
            LocalFileLogger.Info("otp", "verify_success", new { examId, ticket.TicketId });

            Cleanup();
            _navigator.GoTo(ExamEntryStage.SystemCheck);
        }
        catch (OtpVerificationException ex)
        {
            Otp = string.Empty;
            ErrorMessage = ex.Message;
            HasError = true;
            LocalFileLogger.Info("otp", "verify_rejected", new { examId, reason = ex.Message });
        }
        catch (ExamEntryRejectedException ex)
        {
            ErrorMessage = ex.Message;
            HasError = true;
            _sessionState.EntryTicket = null;
            LocalFileLogger.Info("otp", "entry_rejected", new { examId, reason = ex.Message });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Khong xac thuc duoc OTP: {ex.Message}";
            HasError = true;
            _sessionState.EntryTicket = null;
            LocalFileLogger.Error("otp", "verify_failed", ex, new { examId });
        }
        finally
        {
            IsVerifying = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
