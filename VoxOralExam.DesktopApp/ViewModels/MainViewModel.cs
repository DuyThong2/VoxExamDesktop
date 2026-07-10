using System.Collections.ObjectModel;
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

    private ObservableCollection<Exam> _exams = new();
    private bool _isLoading;
    private string _errorMessage = string.Empty;

    public ObservableCollection<Exam> Exams
    {
        get => _exams;
        set => SetProperty(ref _exams, value);
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

    public MainViewModel(IExamEntryNavigator navigator, ExamSessionState sessionState, IExamApiService examApi)
    {
        _navigator = navigator;
        _sessionState = sessionState;
        _examApi = examApi;
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
            Exams = new ObservableCollection<Exam>(exams.Where(IsUpcomingOrInProgress));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Khong the tai danh sach bai thi: {ex.Message}";
            LocalFileLogger.Error("exam_list", "load_failed", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private Task StartExamAsync(Exam? exam)
    {
        if (exam == null || !_sessionState.IsAuthenticated)
        {
            return Task.CompletedTask;
        }

        _sessionState.SelectedExam = exam;
        _sessionState.EntryTicket = null;
        _sessionState.ExamAttemptId = Guid.Empty;
        _sessionState.SessionId = string.Empty;
        _sessionState.Questions = [];
        _sessionState.AttemptAnswerIdsByQuestionId = [];
        _sessionState.PaperItemIdsByQuestionId = [];
        _sessionState.EvaluationGuidesByQuestionId = [];

        _navigator.GoTo(ExamEntryStage.OtpEntry);
        return Task.CompletedTask;
    }

    private static bool IsUpcomingOrInProgress(Exam exam) =>
        exam.Status.Equals("upcoming", StringComparison.OrdinalIgnoreCase)
        || exam.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase);
}
