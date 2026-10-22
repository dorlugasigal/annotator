using Annotator.App.Reference;
using Microsoft.UI.Xaml;

namespace Annotator.App;

public partial class App : Application
{
    private AppHost? _host;

    public App()
    {
        UnhandledException += (_, e) => StartupLog.Note("unhandled", (e.Exception?.ToString() ?? e.Message).ReplaceLineEndings(" | "));
        // Every app window draws with its contrast resources in a Windows contrast theme (WIN-UI-07), so WinUI need not put a
        // backplate behind the text.
        HighContrastAdjustment = ApplicationHighContrastAdjustment.None;
        if (Program.Reference is { } reference)
        {
            RequestedTheme = reference.Theme == ReferenceTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark;
        }
        else if (Program.Motion is { } motion)
        {
            RequestedTheme = motion.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        }
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupLog.Mark("on-launched");
        // Annotator lives in the tray: closing its windows never ends the app; only Quit does.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        if (Program.Reference is { } reference)
        {
            ReferenceHost.Start(reference);
            return;
        }
        if (Program.LiveCheck is { } check)
        {
            _host = AppHost.StartLiveCheck(check);
            return;
        }
        if (Program.Motion is { } motion)
        {
            MotionReferenceHost.Start(motion);
            return;
        }
        _host = AppHost.Start(Program.Board, Program.QuietLaunch);
    }
}
