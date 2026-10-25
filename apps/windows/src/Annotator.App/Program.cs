using System.Diagnostics;
using Annotator.App.Reference;
using Annotator.Workspace.Packaging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Annotator.App;

/// <summary>Custom entry point: a second launch forwards to the running instance before WinUI starts (WIN-SYS-02).</summary>
public static class Program
{
    private static Mutex? s_instance;

    /// <summary>The UX reference state to show instead of the app (development builds only), or null.</summary>
    internal static ReferenceRequest? Reference { get; private set; }

    /// <summary>The live check to run instead of the app (development builds only), or null.</summary>
    internal static LiveCheckRequest? LiveCheck { get; private set; }

    /// <summary>The motion run to play instead of the app (development builds only), or null.</summary>
    internal static MotionReferenceRequest? Motion { get; private set; }

    /// <summary>The board file the launch named (WIN-SYS-04), opened once the toolbar shows, or null.</summary>
    internal static string? Board { get; private set; }

    /// <summary>
    /// True when the package's startup task started Annotator at sign-in (WIN-SYS-01): a quiet launch with the tray icon and
    /// global shortcuts only, like the Mac's login item.
    /// </summary>
    internal static bool QuietLaunch { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        StartupLog.Mark("main");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => StartupLog.Note("crash", (e.ExceptionObject.ToString() ?? "unknown").ReplaceLineEndings(" | "));
        if (Array.IndexOf(args, ReferenceRequest.Switch) >= 0)
        {
            return RunReference(args);
        }
        if (Array.IndexOf(args, LiveCheckRequest.Switch) >= 0)
        {
            return RunLiveCheck(args);
        }
        if (Array.IndexOf(args, MotionReferenceRequest.Switch) >= 0)
        {
            return RunMotion(args);
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // "Annotator.exe <board>" opens the board, in the running instance when there is one (WIN-SYS-04); the package's
        // file association launches the app the same way. The startup task passes --startup-task (WIN-SYS-01); Windows
        // also reports the activation kind to a packaged app, once, so it is read here before anything else could.
        var launch = LaunchArguments.Parse(args);
        Board = launch.Board;
        QuietLaunch = launch.Quiet || Board is null && Platform.Startup.StartupTaskService.ActivatedByStartupTask();
        StartupLog.Note("launch", QuietLaunch ? launch.AfterUpdate ? "quiet (after update)" : "quiet (startup task)" : Board is null ? "ordinary" : "board");
        s_instance = new Mutex(initiallyOwned: false, SingleInstance.MutexName, out bool created);
        if (!created)
        {
            if (QuietLaunch)
            {
                // Annotator already runs; a sign-in start must not show its toolbar.
                return 0;
            }
            SingleInstance.ForwardResult result = SingleInstance.Forward(Board);
            StartupLog.Note("forwarded", result.ToString());
            return result == SingleInstance.ForwardResult.Delivered ? 0 : 1;
        }

        RegisterUpdateRestart(LaunchArguments.AfterUpdateSwitch);
        EnablePointerMessages();
        Application.Start(parameters =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        GC.KeepAlive(s_instance);
        return 0;
    }

    /// <summary>
    /// Asks Windows to start Annotator again when it closes it to install an update (WIN-SYS-03), with
    /// <paramref name="arguments"/>. Only an installed package is updated that way; a refusal leaves the update as it was.
    /// </summary>
    internal static void RegisterUpdateRestart(string arguments)
    {
        if (!Platform.Startup.StartupTaskService.HasPackageIdentity)
        {
            return;
        }
        try
        {
            Platform.Updates.UpdateRestart.Register(arguments);
            StartupLog.Note("update-restart", arguments.Length == 0 ? "ordinary" : arguments);
        }
        catch (Platform.NativeCallException error)
        {
            StartupLog.Note("update-restart", error.Message);
        }
    }

    /// <summary>
    /// Routes the mouse through pointer messages like pen and touch, so canvases get every coalesced sample
    /// (WIN-INP-01). Windows allows it once per process, before windows exist. If it is refused, the mouse keeps
    /// working through mouse messages with one sample per message.
    /// </summary>
    private static void EnablePointerMessages()
    {
        try
        {
            StartupLog.Note("mouse-in-pointer", Platform.Input.MouseInPointer.Enable().ToString());
        }
        catch (Platform.NativeCallException error)
        {
            StartupLog.Note("mouse-in-pointer", error.Message);
        }
    }

    /// <summary>
    /// Shows one UX reference state for the comparison tool (WIN-PAR-06) and quits. Only development builds accept
    /// it; it runs beside a running Annotator without the single-instance check, the tray icon or global shortcuts.
    /// </summary>
    private static int RunReference(string[] args)
    {
        if (!BuildInfo.IsDevelopment)
        {
            return 2;
        }
        try
        {
            Reference = ReferenceRequest.Parse(args);
        }
        catch (FormatException)
        {
            return 3;
        }
        if (Reference is { Docked: true })
        {
            // The docked toolbar takes real input in on-screen checks (tools/windows/dock-check.ps1), so the mouse arrives
            // as pointer messages, as in the app.
            EnablePointerMessages();
        }
        return StartApplication();
    }

    /// <summary>
    /// Plays one show and hide motion for measurements and contact sheets (development builds only), beside a running
    /// Annotator, like <see cref="RunReference"/>.
    /// </summary>
    private static int RunMotion(string[] args)
    {
        if (!BuildInfo.IsDevelopment)
        {
            return 2;
        }
        try
        {
            Motion = MotionReferenceRequest.Parse(args);
        }
        catch (FormatException error)
        {
            Console.Error.WriteLine(error.Message);
            return 3;
        }
        return StartApplication();
    }

    private static int StartApplication()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>
    /// Runs the live check (tools/windows/live-check.ps1) and quits. Only development builds accept it; like the reference
    /// mode it runs beside a running Annotator, with pointer messages on as in the app.
    /// </summary>
    private static int RunLiveCheck(string[] args)
    {
        if (!BuildInfo.IsDevelopment)
        {
            return 2;
        }
        try
        {
            LiveCheck = LiveCheckRequest.Parse(args);
        }
        catch (FormatException)
        {
            return 3;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        EnablePointerMessages();
        Application.Start(parameters =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}

/// <summary>
/// Startup milestones in milliseconds since the process was created, written only when the
/// ANNOTATOR_STARTUP_LOG environment variable names a file (measurement runs). Otherwise it does nothing.
/// </summary>
internal static class StartupLog
{
    private static readonly string? s_path = Environment.GetEnvironmentVariable("ANNOTATOR_STARTUP_LOG");
    private static readonly Lock s_gate = new();
    private static readonly List<string> s_lines = [];

    public static void Mark(string name)
    {
        if (s_path is null)
        {
            return;
        }
        double elapsed = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        Write($"{name}={elapsed:F1}");
    }

    public static void Note(string key, string value)
    {
        if (s_path is not null)
        {
            Write($"{key}={value}");
        }
    }

    private static void Write(string line)
    {
        lock (s_gate)
        {
            s_lines.Add($"{line} thread={Environment.CurrentManagedThreadId}");
            File.WriteAllLines(s_path!, s_lines);
        }
    }
}
