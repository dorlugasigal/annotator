using System.Text;
using Annotator.Core;

namespace Annotator.Workspace.Settings;

/// <summary>
/// The app and system facts for support information that the workspace does not know (WIN-PRV-04): coarse values only,
/// never paths, user names or document names.
/// </summary>
public sealed record SupportFacts
{
    /// <summary>The version, a plain X.Y.Z such as "0.2.0" (WIN-DST-04).</summary>
    public required string Version { get; init; }

    /// <summary>The build stamp, or the Mac's "Unstamped development build".</summary>
    public required string BuildLabel { get; init; }

    /// <summary>How the app is installed, such as "Microsoft Store package" or "development build (not packaged)".</summary>
    public required string Installation { get; init; }

    /// <summary>The Windows version and build number, such as "11 (build 26100)".</summary>
    public required string Windows { get; init; }

    /// <summary>The app's and the computer's processor architecture, such as "x64 on x64".</summary>
    public required string Architecture { get; init; }

    public int Screens { get; init; }

    public bool UpdatesConfigured { get; init; }

    public bool AutomaticUpdateChecks { get; init; }

    public LaunchAtSignInState? LaunchAtSignIn { get; init; }

    public bool AutomatedTest { get; init; }

    /// <summary>Windows' consent to capture the screen: "not needed" unpackaged, else its answer, such as "allowed".</summary>
    public string CapturePermission { get; init; } = "not needed";

    /// <summary>The capture service's state in one word, such as "stopped" or "live".</summary>
    public required string Capture { get; init; }
}

/// <summary>
/// Port of WorkspaceModel.supportInfo (SupportInfo.swift) with the Windows terms: the app, Windows and the coarse state of
/// drawing, capture, shortcuts and scrolling. No drawings, screenshots or document names.
/// </summary>
public static class SupportInfo
{
    public static string Text(SupportFacts facts, WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(snapshot);
        var text = new StringBuilder();
        void Line(string name, string value) => text.Append(name).Append(": ").Append(value).Append('\n');
        static string Flag(bool value) => value ? "true" : "false";

        text.Append("Annotator support info\n");
        Line("Version", facts.Version);
        Line("Running build", facts.BuildLabel);
        Line("Edition", "free");
        Line("Updates configured", Flag(facts.UpdatesConfigured));
        Line("Automatic update checks", Flag(facts.AutomaticUpdateChecks));
        Line("Launch at sign-in", facts.LaunchAtSignIn?.RawValue ?? "not checked");
        Line("App", facts.Installation);
        Line("Automated test process", Flag(facts.AutomatedTest));
        Line("Windows", facts.Windows);
        Line("Architecture", facts.Architecture);
        Line("Screens", facts.Screens.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Line("Toolbar", (snapshot.Preferences.ToolbarOrientation ?? ToolbarOrientation.Horizontal).RawValue);
        Line("Mode", snapshot.Mode.RawValue);
        Line("Workspace", "live desktop");
        Line("Drawings hidden", Flag(snapshot.WorkspaceHidden));
        Line("Screen capture permission", facts.CapturePermission);
        Line("Screen capture", facts.Capture);
        Line("Screen capture has worked", Flag(snapshot.CaptureVerified));
        Line("Screen capture error present", Flag(snapshot.CaptureFailureMessage is not null));
        Line("Zoom mode", "off");
        foreach (GlobalShortcutState shortcut in snapshot.GlobalShortcuts)
        {
            Line($"Global {Name(shortcut.Action)} shortcut", shortcut.Status switch
            {
                GlobalShortcutStatus.Registered => "registered",
                GlobalShortcutStatus.AlreadyUsed => "used by another app",
                GlobalShortcutStatus.Refused => "refused",
                _ => "not registered",
            });
        }
        Line("Scroll shortcut", "Alt only");
        Line("Scroll permission", "not needed");
        Line("Global scroll monitor active", "false");
        text.Append("No drawings, screenshots or document names are included.");
        return text.ToString();
    }

    private static string Name(GlobalShortcutAction action) => action switch
    {
        GlobalShortcutAction.ShowHide => "show / hide",
        GlobalShortcutAction.ResumeDrawing => "resume drawing",
        GlobalShortcutAction.HideOnly => "hide only",
        GlobalShortcutAction.LiveZoom => "zoom",
        GlobalShortcutAction.BreakTimer => "break timer",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}
