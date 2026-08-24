using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VoxOralExam.Core.Interfaces;
using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Infra.Clients.DomainService;
using VoxOralExam.DesktopApp.Infra.Clients.DomainService.Impl;
using VoxOralExam.DesktopApp.Infra.Devices;
using VoxOralExam.DesktopApp.Infra.Devices.Impl;
using VoxOralExam.DesktopApp.Infra.Clients.StreamService;
using VoxOralExam.DesktopApp.Infra.Media;
using VoxOralExam.DesktopApp.Infra.Recording;
using VoxOralExam.DesktopApp.Infra.Recording.Storage;
using VoxOralExam.DesktopApp.Mocks;
using VoxOralExam.DesktopApp.Services;
using VoxOralExam.DesktopApp.Services.DomainService;
using VoxOralExam.DesktopApp.Services.DomainService.Impl;
using VoxOralExam.DesktopApp.Services.EntryFlow;
using VoxOralExam.DesktopApp.Services.EntryFlow.Impl;
using VoxOralExam.DesktopApp.Services.ExamFlow;
using VoxOralExam.DesktopApp.Services.ExamFlow.Attempt;
using VoxOralExam.DesktopApp.Services.ExamFlow.Impl;
using VoxOralExam.DesktopApp.Services.ExamFlow.Question;
using VoxOralExam.DesktopApp.Services.Proctoring;
using VoxOralExam.DesktopApp.State;
using VoxOralExam.DesktopApp.ViewModels;
using VoxOralExam.DesktopApp.Workers;
using VoxOralExam.Core.Models;

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
        // .env.local (gitignored, optional) loads on top and wins on any key it sets -- lets
        // local dev point JAVA_BASE_URL/PYTHON_BASE_URL/etc at localhost without ever touching
        // the shared .env (which stays pointed at the live deployment), so switching between
        // "test against prod" and "run everything local" never requires editing .env back and
        // forth.
        DotEnvLoader.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env.local"));

        _configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();

        SessionEnding += App_SessionEnding;

        StartOrphanedUploadRecovery();

        var settings = _services.GetRequiredService<AppSettings>();
        if (settings.LaunchStreamingDemo)
        {
            try
            {
                var demoWindow = _services.GetRequiredService<Views.StreamingDemoWindow>();
                LocalFileLogger.Info("app", "streaming_demo_shown");
                demoWindow.Show();
                LocalFileLogger.Info("app", "startup_complete");
            }
            catch (Exception ex)
            {
                LocalFileLogger.Error("app", "startup_show_streaming_demo_failed", ex);
                throw;
            }
            return;
        }

        try
        {
            var navigator = _services.GetRequiredService<IExamEntryNavigator>();
            navigator.ExamStartRequested += OnExamStartRequested;
            navigator.Reset(ExamEntryStage.Login);

            var shell = _services.GetRequiredService<Views.ShellWindow>();
            LocalFileLogger.Info("app", "shell_resolved");
            shell.Show();
            LocalFileLogger.Info("app", "shell_shown");
            LocalFileLogger.Info("app", "startup_complete");
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("app", "startup_show_shell_failed", ex);
            throw;
        }
    }

    /// <summary>
    /// Finishes uploads a previous run left unfinished, in the background.
    ///
    /// Deliberately fire-and-forget and never awaited: a student waiting to sit an exam must not be
    /// held up by a previous attempt's leftovers, and the recovery service is best-effort by
    /// construction -- anything it cannot finish this launch is simply retried on the next one.
    /// </summary>
    private void StartOrphanedUploadRecovery()
    {
        var recovery = _services!.GetRequiredService<OrphanedUploadRecoveryService>();
        _ = Task.Run(async () =>
        {
            try
            {
                await recovery.RecoverAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                LocalFileLogger.Error("app", "orphaned_upload_recovery_failed", ex);
            }
        });
    }

    private void OnExamStartRequested(object? sender, EventArgs e)
    {
        // The entry flow finished (device pre-flight passed) and asked to start the exam. Hand off to
        // the exam surface, then close the shell so ShutdownMode=OnLastWindowClose still exits the app
        // when the exam window closes.
        // TODO(§A): fold InExam into the shell (single lockdown-controlled window) instead of opening
        // a separate ExamWindow here.
        LocalFileLogger.Info("app", "launch_exam_window");
        var examWindow = _services.GetRequiredService<Views.ExamWindow>();
        examWindow.Show();

        Application.Current.Windows
            .OfType<Views.ShellWindow>()
            .FirstOrDefault()
            ?.Close();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LocalFileLogger.Info("app", "exit_begin");
        EnsureExamFlowStopped(TimeSpan.FromSeconds(5));
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
                settings.PythonBaseUrl,
                settings));

        services.AddSingleton<RecordingClock>();
        services.AddSingleton<CameraService>();
        services.AddSingleton<CaptureReadinessProbe>();
        services.AddSingleton<CameraSignalGuard>();
        services.AddSingleton<ScreenProctoringService>();
        services.AddSingleton<IDeviceContextProvider, DeviceContextProvider>();
        services.AddSingleton<MockExamDataFactory>();

        if (settings.UseMockData)
        {
            services.AddSingleton<IExamApiService, MockExamApiService>();
            services.AddSingleton<IExamEntryApiService, MockExamEntryApiService>();
        }
        else
        {
            services.AddSingleton<IExamApiService, ExamApiService>();
            services.AddHttpClient<IExamEntryApiService, ExamEntryApiService>(client =>
            {
                client.BaseAddress = new Uri(settings.JavaBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(30);
            });
        }

        services.AddSingleton<RealtimeAttemptProgressClient>();
        services.AddHttpClient<StudentStreamAccessClient>(client =>
        {
            client.BaseAddress = new Uri(settings.JavaBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<DevStreamTokenClient>(client =>
        {
            client.BaseAddress = new Uri(settings.DevStreamTokenUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<IExamSessionBootstrapService, ExamSessionBootstrapService>();
        // Singleton: đệm nằm trên đĩa nên vào lại sau khi bị ngắt vẫn dùng chung đúng thư mục đó.
        services.AddSingleton<IQuestionAssetCache, QuestionAssetCache>();

        services.AddHttpClient<IAuthApiService, AuthApiService>(client =>
        {
            client.BaseAddress = new Uri(settings.JavaBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<IProctoringService>(sp => sp.GetRequiredService<ScreenProctoringService>());
        services.AddHttpClient<StreamSessionClient>(client =>
        {
            client.BaseAddress = new Uri(settings.StreamingBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(settings.RecordingUploadTimeoutSeconds);
        });
        services.AddHttpClient<SegmentUploadClient>(client =>
        {
            client.BaseAddress = new Uri(settings.StreamingBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(settings.RecordingUploadTimeoutSeconds);
        });
        services.AddSingleton<LocalSegmentStore>();
        services.AddSingleton<SegmentUploadWorker>();
        services.AddSingleton<UploadCredentialRefresher>();
        services.AddSingleton<OrphanedUploadRecoveryService>();
        services.AddSingleton<ScreenSegmentRecorder>();
        services.AddSingleton<CameraSegmentRecorder>();
        services.AddSingleton<LiveMonitorStreamService>();
        services.AddSingleton<ExamRecordingService>();
        services.AddSingleton<IExamRecordingService>(
            sp => sp.GetRequiredService<ExamRecordingService>());
        services.AddSingleton<ITurnUploadUrlProvider, TurnUploadUrlProvider>();
        services.AddSingleton<TurnAudioUploader>();
        services.AddSingleton<TurnArchiveClient>();
        services.AddSingleton<LocalAvatarSpeaker>();
        services.AddSingleton<RealtimeSessionClient>();
        services.AddSingleton<AvatarWebRtcClient>();
        services.AddSingleton<QuestionAssetPresentationCoordinator>();
        services.AddSingleton<ExamAttemptRunnerFactory>();
        services.AddSingleton<RealtimeExamFlowService>();
        services.AddSingleton<IExamFlowService>(sp => sp.GetRequiredService<RealtimeExamFlowService>());

        // Single owner of entry-stage transitions; the shell binds to it and view models drive it.
        services.AddSingleton<IExamEntryNavigator, ExamEntryNavigator>();

        services.AddTransient<LoginViewModel>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<OtpEntryViewModel>();
        services.AddTransient<DevicePreflightViewModel>();
        services.AddTransient<ExamViewModel>();
        services.AddTransient<StreamingDemoViewModel>();

        services.AddTransient<Views.ShellWindow>();
        services.AddTransient<Views.ExamWindow>();
        services.AddTransient<Views.StreamingDemoWindow>();
    }

    private void App_SessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        LocalFileLogger.Info("app", "session_ending");
        // Windows gives an app only a short, OS-controlled window to respond to session ending
        // before forcing termination -- tighter than OnExit's own safety-net budget.
        EnsureExamFlowStopped(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Safety net only, not the primary cleanup path. The primary path is Window.Closing
    /// (ExamWindow/StreamingDemoWindow) properly awaiting full cleanup -- including
    /// ExamRecordingService.ShutdownAsync() -- before ever letting the window actually close, which
    /// should make everything below a fast, idempotent no-op by the time OnExit/SessionEnding run.
    /// This only does real work if that path somehow didn't run (a window bypassed Closing, or the
    /// process is exiting via SessionEnding before any window had a chance to close normally).
    /// </summary>
    private void EnsureExamFlowStopped(TimeSpan timeout)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;

        try
        {
            LocalFileLogger.Info("app", "ensure_exam_flow_stopped_begin");

            // Task.Run, not a direct call: OnExit/SessionEnding run on the UI thread, and this
            // method's own caller may end up blocking that same thread on the result below --
            // calling these async methods directly here would capture the WPF SynchronizationContext
            // for every continuation inside them, then block waiting on that same, now-unresponsive,
            // UI thread -- the exact deadlock class already fixed (and re-found) elsewhere in this
            // app this session. Task.Run drops the ambient SynchronizationContext so nothing inside
            // needs the UI thread to resume. Wait(timeout), not GetAwaiter().GetResult(), keeps this
            // bounded instead of blocking shutdown indefinitely if something is still genuinely stuck.
            var completed = Task.Run(async () =>
            {
                var examFlow = _services.GetService<IExamFlowService>();
                if (examFlow is not null)
                {
                    await examFlow.StopAsync();
                }

                var proctoring = _services.GetService<IProctoringService>();
                if (proctoring is not null)
                {
                    await proctoring.StopAsync();
                }

                var recording = _services.GetService<ExamRecordingService>();
                if (recording is not null)
                {
                    await ((IAsyncDisposable)recording).DisposeAsync();
                }
            }).Wait(timeout);

            if (completed)
            {
                LocalFileLogger.Info("app", "ensure_exam_flow_stopped_complete");
            }
            else
            {
                LocalFileLogger.Error(
                    "app",
                    "ensure_exam_flow_stopped_timed_out",
                    new TimeoutException($"Shutdown cleanup did not finish within {timeout}."));
            }
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("app", "ensure_exam_flow_stopped_failed", ex);
        }
    }
}


