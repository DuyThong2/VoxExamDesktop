using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using VoxOralExam.DesktopApp.Models;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.ViewModels;

public class MainViewModel : BaseViewModel
{
    private readonly IExamEntryNavigator _navigator;
    private readonly ExamSessionState _sessionState;
    private readonly IExamApiService _examApi;
    private readonly IExamEntryApiService _examEntryApi;
    private readonly IExamSessionBootstrapService _sessionBootstrapService;

    private ObservableCollection<Exam> _centralizedExams = [];
    private ObservableCollection<Exam> _classTestExams = [];
    private bool _isLoading;
    private string _errorMessage = string.Empty;

    public ObservableCollection<Exam> CentralizedExams
    {
        get => _centralizedExams;
        set => SetProperty(ref _centralizedExams, value);
    }

    public ObservableCollection<Exam> ClassTestExams
    {
        get => _classTestExams;
        set => SetProperty(ref _classTestExams, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public ICommand StartExamCommand { get; }
    public ICommand RefreshCommand { get; }

    public MainViewModel(
        IExamEntryNavigator navigator,
        ExamSessionState sessionState,
        IExamApiService examApi,
        IExamEntryApiService examEntryApi,
        IExamSessionBootstrapService sessionBootstrapService)
    {
        _navigator = navigator;
        _sessionState = sessionState;
        _examApi = examApi;
        _examEntryApi = examEntryApi;
        _sessionBootstrapService = sessionBootstrapService;
        StartExamCommand = new RelayCommand<Exam>(exam => _ = StartExamAsync(exam));
        RefreshCommand = new RelayCommand(async () => await LoadExamsAsync());
        _ = LoadExamsAsync();
    }

    private async Task LoadExamsAsync()
    {
        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            var exams = await _examApi.GetAvailableExamsAsync();
            LocalFileLogger.Info("exam_list", "loaded", new { count = exams.Count });
            var upcoming = exams.Where(IsUpcomingOrInProgress).ToList();
            CentralizedExams = new ObservableCollection<Exam>(upcoming.Where(exam => exam.Kind == ExamKind.Centralized));
            ClassTestExams = new ObservableCollection<Exam>(upcoming.Where(exam => exam.Kind == ExamKind.ClassTest));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Không thể tải danh sách bài thi: {ex.Message}";
            LocalFileLogger.Error("exam_list", "load_failed", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task StartExamAsync(Exam? exam)
    {
        if (exam == null || !_sessionState.IsAuthenticated)
        {
            return;
        }

        ErrorMessage = string.Empty;

        if (!exam.CanEnter)
        {
            ShowEntryError(string.IsNullOrWhiteSpace(exam.EntryMessage)
                ? "Bài thi hiện chưa đủ điều kiện để vào thi."
                : exam.EntryMessage);
            return;
        }

        // A class test always skips OTP (no schedule concept). A centralized exam only skips it
        // when the teacher/admin has explicitly disabled OTP for it (Exam.requiresOtp); otherwise
        // it goes through the normal OTP entry + schedule-window flow below.
        var skipsOtp = exam.Kind == ExamKind.ClassTest || !exam.RequiresOtp;

        if (skipsOtp)
        {
            if (!exam.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase))
            {
                ShowEntryError("Bài kiểm tra chưa được giáo viên mở, vui lòng đợi.");
                return;
            }
        }
        else if (!IsUpcomingOrInProgress(exam))
        {
            ShowEntryError("Bài thi không còn trong thời gian cho phép vào thi.");
            return;
        }

        ResetExamSession(exam);

        if (skipsOtp)
        {
            try
            {
                var ticket = await _examEntryApi.StartClassTestAsync(Guid.Parse(exam.Id));
                await _sessionBootstrapService.EnterWithTicketAsync(ticket);
                LocalFileLogger.Info("class_test", "start_success", new { examId = exam.Id, ticket.TicketId });
                _navigator.GoTo(ExamEntryStage.SystemCheck);
            }
            catch (ExamEntryRejectedException ex)
            {
                _sessionState.EntryTicket = null;
                ShowEntryError(ex.Message);
                LocalFileLogger.Info("class_test", "start_rejected", new { examId = exam.Id, reason = ex.Message });
            }
            catch (Exception ex)
            {
                _sessionState.EntryTicket = null;
                ShowEntryError($"Không thể bắt đầu bài kiểm tra: {ex.Message}");
                LocalFileLogger.Error("class_test", "start_failed", ex, new { examId = exam.Id });
            }
            return;
        }

        _navigator.GoTo(ExamEntryStage.OtpEntry);
    }

    private void ShowEntryError(string message)
    {
        ErrorMessage = message;
        MessageBox.Show(message, "Không thể vào thi", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ResetExamSession(Exam exam)
    {
        _sessionState.SelectedExam = exam;
        _sessionState.EntryTicket = null;
        _sessionState.ExamAttemptId = Guid.Empty;
        _sessionState.SessionId = string.Empty;
        _sessionState.Questions = [];
        _sessionState.AttemptAnswerIdsByQuestionId = [];
        _sessionState.PaperItemIdsByQuestionId = [];
        _sessionState.EvaluationGuidesByQuestionId = [];
    }

    private static bool IsUpcomingOrInProgress(Exam exam) =>
        exam.Status.Equals("upcoming", StringComparison.OrdinalIgnoreCase)
        || exam.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase);
}
