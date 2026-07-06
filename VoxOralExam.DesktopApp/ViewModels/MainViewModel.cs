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
            // Students only act on exams that are upcoming or currently in progress; completed ones
            // are hidden here. TODO(§F): let the server pre-filter this list per student.
            var visible = exams.Where(IsUpcomingOrInProgress);
            Exams = new ObservableCollection<Exam>(visible);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Khong the tai danh sach bai thi: {ex.Message}";
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

        // Carry the pick across the entry stages (the navigator resolves a fresh VM per stage).
        _sessionState.SelectedExam = exam;

        try
        {
            // TODO(§C): move exam-paper loading to AFTER OTP verification and take the attemptId from
            // the entry ticket instead of the client-minted Guid in ExamSessionState.LoadExamPaper.
            // Loading here (before OTP) keeps current behavior for slice 1-2.
            var paper = await _examApi.GetExamPaperAsync(exam.Id);
            _sessionState.LoadExamPaper(paper);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Khong the tai de thi: {ex.Message}";
            return;
        }

        // Enter the OTP stage; the navigator drives OtpEntry -> SystemCheck -> DevicePreflight ->
        // (RequestStartExam) inside the shell, then App opens the exam surface.
        _navigator.GoTo(ExamEntryStage.OtpEntry);
    }

    private static bool IsUpcomingOrInProgress(Exam exam) =>
        exam.Status.Equals("upcoming", StringComparison.OrdinalIgnoreCase)
        || exam.Status.Equals("in_progress", StringComparison.OrdinalIgnoreCase);
}
