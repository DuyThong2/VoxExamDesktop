using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VoxOralExam.Core.Interfaces;
using VoxOralExam.DesktopApp.Infrastructure;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.State;
using VoxOralExam.DesktopApp.ViewModels;

namespace VoxOralExam.DesktopApp;

public partial class App : Application
{
    private IServiceProvider _services = null!;
    private IConfiguration _configuration = null!;
    private bool _isShuttingDown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LocalFileLogger.Clear();
        LocalFileLogger.Info("app", "startup_begin");
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        DotEnvLoader.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env"));

        _configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        SessionEnding += App_SessionEnding;

        try
        {
            var loginView = _services.GetRequiredService<Views.LoginView>();
            LocalFileLogger.Info("app", "login_view_resolved");
            loginView.Show();
            LocalFileLogger.Info("app", "login_view_shown");
            LocalFileLogger.Info("app", "startup_complete");
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("app", "startup_show_login_failed", ex);
            throw;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LocalFileLogger.Info("app", "exit_begin");
        EnsureExamFlowStopped();
        SessionEnding -= App_SessionEnding;
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
        LocalFileLogger.Info("app", "exit_complete");
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        LocalFileLogger.Error("app", "dispatcher_unhandled_exception", e.Exception);
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LocalFileLogger.Error("app", "appdomain_unhandled_exception", ex, new
            {
                e.IsTerminating
            });
            return;
        }

        LocalFileLogger.Info("app", "appdomain_unhandled_non_exception", new
        {
            exceptionObject = e.ExceptionObject?.ToString(),
            e.IsTerminating
        });
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LocalFileLogger.Error("app", "task_unobserved_exception", e.Exception);
    }

    private void ConfigureServices(IServiceCollection services)
    {
        var settings = _configuration.GetSection("AppSettings").Get<AppSettings>() ?? new AppSettings();

        // appsettings.json is committed to git and now ships with no real values; .env (gitignored,
        // solution root) is the actual source of config/secrets, overriding every AppSettings field
        // whose UPPER_SNAKE_CASE env var is set. Anyone without a .env yet still gets AppSettings.cs's
        // own inline defaults.
        DotEnvLoader.ApplyOverrides(settings);

        services.AddSingleton(_configuration);
        services.AddSingleton(settings);
        services.AddSingleton(new ExamSessionState());

        services.AddHttpClient("WebRtcClient", client =>
        {
            client.BaseAddress = new Uri(settings.PythonBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton(sp =>
            new WebRtcClient(
                sp.GetRequiredService<IHttpClientFactory>(),
                settings.PythonBaseUrl));

        services.AddSingleton(_ => new CameraService(settings));
        services.AddSingleton<ScreenProctoringService>();
        services.AddSingleton<IDeviceContextProvider, DeviceContextProvider>();
        services.AddSingleton<MockExamDataFactory>();

        services.AddHttpClient<IAuthApiService, AuthApiService>(client =>
        {
            client.BaseAddress = new Uri(settings.JavaBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<IProctoringService>(sp => sp.GetRequiredService<ScreenProctoringService>());
        services.AddSingleton<TurnAudioUploader>();
        services.AddSingleton<TurnArchiveClient>();
        services.AddSingleton<RealtimeSessionClient>();
        services.AddSingleton<AvatarWebRtcClient>();
        services.AddSingleton<MicAudioStreamer>();
        services.AddSingleton<RealtimeExamFlowService>();
        services.AddSingleton<IExamFlowService>(sp => sp.GetRequiredService<RealtimeExamFlowService>());

        services.AddTransient<LoginViewModel>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<ExamViewModel>();

        services.AddTransient<MainWindow>();
        services.AddTransient<Views.LoginView>();
        services.AddTransient<Views.ExamWindow>();
    }

    private void App_SessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        LocalFileLogger.Info("app", "session_ending");
        EnsureExamFlowStopped();
    }

    private void EnsureExamFlowStopped()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;

        try
        {
            LocalFileLogger.Info("app", "ensure_exam_flow_stopped_begin");
            var examFlow = _services.GetService<IExamFlowService>();
            examFlow?.StopAsync().GetAwaiter().GetResult();

            var proctoring = _services.GetService<ScreenProctoringService>();
            proctoring?.StopAsync().GetAwaiter().GetResult();
            LocalFileLogger.Info("app", "ensure_exam_flow_stopped_complete");
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("app", "ensure_exam_flow_stopped_failed", ex);
        }
    }
}
