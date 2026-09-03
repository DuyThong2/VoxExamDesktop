using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VoxOralExam.Core.Interfaces;
using VoxOralExam.DesktopApp.Infra.Clients.AIService;
using VoxOralExam.DesktopApp.Infra.Clients.DomainService;
using VoxOralExam.DesktopApp.Infra.Clients.DomainService.Impl;
using VoxOralExam.DesktopApp.Infra.Clients.Google;
using VoxOralExam.DesktopApp.Infra.Clients.Google.Impl;
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
using VoxOralExam.DesktopApp.Services.Auth;

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
        // AFTER the exam flow has been given its window to stop, never before: the upload workers
        // are still authenticating to Java with this token while they flush whatever segments are
        // left on disk, and pulling it out from under them would strand exactly the evidence the
        // refresh work above exists to protect.
        SignOut(TimeSpan.FromSeconds(3));
        SessionEnding -= App_SessionEnding;
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
        LocalFileLogger.Info("app", "exit_complete");
        base.OnExit(e);
    }

    /// <summary>
    /// Revokes the session server-side and drops the student's credentials on the way out.
    ///
    /// <para>The server-side half is the part that matters. Clearing memory on a process that is
    /// about to exit buys little on its own, but the refresh token stays valid at vox for its full
    /// 72-hour TTL unless something revokes it -- and on a shared exam machine an abandoned session
    /// living three days past the exam is the actual exposure. AuthSessionManager.SignOutAsync
    /// clears locally whether or not the revoke succeeds.</para>
    ///
    /// <para>Bounded and best-effort: a slow or unreachable server must not hold up shutdown, and
    /// nothing here should change how the app exits.</para>
    /// </summary>
    private void SignOut(TimeSpan timeout)
    {
        try
        {
            var auth = _services?.GetService<AuthSessionManager>();
            if (auth is null)
            {
                return;
            }

            // Task.Run + Wait(timeout) for the reason spelled out in EnsureExamFlowStopped: OnExit
            // runs on the UI thread, and awaiting inline would capture the WPF
            // SynchronizationContext and then block the very thread the continuations need.
            var completed = Task.Run(() => auth.SignOutAsync(CancellationToken.None)).Wait(timeout);
            if (!completed)
            {
                LocalFileLogger.Error(
                    "app",
                    "sign_out_timed_out",
                    new TimeoutException($"Sign-out did not finish within {timeout}."));
            }
        }
        catch (Exception ex)
        {
            LocalFileLogger.Error("app", "sign_out_failed", ex);
        }
    }

    /// <summary>
    /// How many dispatcher exceptions may be swallowed inside <see cref="DispatcherExceptionWindow"/>
    /// before the app is allowed to die after all.
    ///
    /// <para>Without a ceiling, "keep running" turns into an unkillable zombie: a fault that recurs
    /// on every layout pass would be caught forever, burning CPU and filling the log while the
    /// student stares at a broken window. Past this rate the exception is clearly not incidental,
    /// and dying is the more honest outcome.</para>
    /// </summary>
    private const int MaxHandledDispatcherExceptions = 20;

    private static readonly TimeSpan DispatcherExceptionWindow = TimeSpan.FromMinutes(1);

    /// <summary>UI thread only, so no synchronisation: the dispatcher raises this on one thread.</summary>
    private readonly Queue<DateTime> _recentDispatcherExceptions = new();

    /// <summary>
    /// Keeps the app alive through an unexpected UI-thread exception instead of letting it terminate.
    ///
    /// <para>This handler used to log and return, which leaves <c>e.Handled</c> false and ends the
    /// process. For an exam client that trade is backwards. Terminating costs the SUBMITTED PATCH,
    /// any segment still draining, and the student's remaining questions -- certain, unrecoverable
    /// losses. Continuing costs an app in a state nobody reasoned about, which is bad but bounded:
    /// each answer is archived server-side as its turn ends, so the evidence does not live in this
    /// process's memory.</para>
    ///
    /// <para>Not a substitute for handling faults where they happen. The device-loss crash that
    /// motivated this was fixed at its source in TurnAudioRecorder; this only stops the NEXT unknown
    /// one from being fatal, and every catch here is a bug that still wants finding -- which is why
    /// it is logged at Error rather than quietly absorbed.</para>
    /// </summary>
    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        LocalFileLogger.Error("app", "dispatcher_unhandled_exception", e.Exception);

        var now = DateTime.UtcNow;
        _recentDispatcherExceptions.Enqueue(now);
        while (_recentDispatcherExceptions.Count > 0
            && now - _recentDispatcherExceptions.Peek() > DispatcherExceptionWindow)
        {
            _recentDispatcherExceptions.Dequeue();
        }

        if (_recentDispatcherExceptions.Count > MaxHandledDispatcherExceptions)
        {
            LocalFileLogger.Error(
                "app",
                "dispatcher_exception_storm",
                new InvalidOperationException(
                    $"More than {MaxHandledDispatcherExceptions} dispatcher exceptions within "
                    + $"{DispatcherExceptionWindow.TotalSeconds:F0}s; letting the process terminate."));
            return;
        }

        e.Handled = true;
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
        })
        // Cookies handled by hand in AuthApiService, not by a CookieContainer. The refresh and CSRF
        // tokens both arrive as Set-Cookie and both have to be replayed on /auth/refresh, but
        // IHttpClientFactory recycles its handlers on a timer -- so a container's contents are not
        // something an exam lasting hours can depend on still holding.
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false });

        // Talks to accounts.google.com / oauth2.googleapis.com, NOT to vox -- hence no BaseAddress,
        // and deliberately a separate client from IAuthApiService's: that one is configured with
        // JavaBaseUrl and UseCookies=false for vox's refresh cookie handling, neither of which has
        // anything to do with Google's token endpoint.
        services.AddHttpClient<IGoogleSignInClient, GoogleSignInClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Singleton because the refresh gate inside it only serialises callers that share an
        // instance, and vox revokes the whole device session if two refreshes race (see
        // AuthSessionManager._refreshGate).
        services.AddSingleton(sp => new AuthSessionManager(
            sp.GetRequiredService<IAuthApiService>,
            sp.GetRequiredService<ExamSessionState>()));

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
        services.AddSingleton<PendingSubmissionStore>();
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


