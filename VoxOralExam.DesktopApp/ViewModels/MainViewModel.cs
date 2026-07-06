using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using VoxOralExam.DesktopApp.Models;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;

namespace VoxOralExam.DesktopApp.ViewModels;

public class MainViewModel : BaseViewModel
{
    private readonly IServiceProvider _serviceProvider;
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

    public MainViewModel(IServiceProvider serviceProvider, ExamSessionState sessionState, IExamApiService examApi)
    {
        _serviceProvider = serviceProvider;
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
            Exams = new ObservableCollection<Exam>(exams);
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

        try
        {
            var paper = await _examApi.GetExamPaperAsync(exam.Id);
            _sessionState.LoadExamPaper(paper);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Khong the tai de thi: {ex.Message}";
            return;
        }

        var examWindow = _serviceProvider.GetRequiredService<Views.ExamWindow>();
        examWindow.Show();

        Application.Current.Windows
            .OfType<MainWindow>()
            .FirstOrDefault()
            ?.Close();
    }
}
