using System.Collections.Immutable;
using Annotator.Core;
using Annotator.Core.Tutorial;
using Annotator.Workspace.Shortcuts;

namespace Annotator.Workspace.FirstUse;

/// <summary>The words of one tutorial step: its title, one line of detail, a line per goal when it has several, and a hint.</summary>
public sealed record TutorialStepText(string Title, string Detail, ImmutableArray<string> Goals, string Hint);

/// <summary>A row of the finish card: what a key does and its keycaps.</summary>
public sealed record TutorialKeyRow(string Label, ImmutableArray<string> Keys);

/// <summary>
/// The tutorial's words (Mac TutorialViews), with Windows key names and the platform terms of specification 013: Ctrl for
/// Control and Command, Esc, Enter for Return, and the tray icon for the menu bar icon. Keys follow the person's shortcuts;
/// a shortcut that is missing or that Windows refused uses the words without a key.
/// </summary>
public static class TutorialText
{
    public const string WelcomeTitle = "Welcome to Annotator";
    public const string WelcomeBody = "Learn the basics in about two minutes. Use the keys or the mouse, whichever you like.";
    public const string Start = "Start tutorial";
    public const string NotNow = "Not now";
    public const string ShowOnLaunch = "Show on launch";
    public const string FindAgain = "Find this again in Settings > Help.";

    public const string EndTutorial = "End tutorial";
    public const string ShowMe = "Show me";
    public const string SkipStep = "Skip step";
    public const string Done = "Done";

    public const string FinishTitle = "You're ready.";
    public const string FinishLead = "That's the basics. Keep these keys handy.";
    public const string ChangeShortcuts = "Change any shortcut in Settings > Toolbar & Shortcuts.";
    public const string OpenSettings = "Open Settings";
    public const string PracticeAgain = "Practice again from Settings > Help.";

    public const string WelcomeIdentifier = "tutorial-welcome";
    public const string CoachIdentifier = "tutorial-coach";
    public const string FinishIdentifier = "tutorial-finish";
    public const string StartIdentifier = "tutorial-start";
    public const string NotNowIdentifier = "tutorial-not-now";
    public const string ShowOnLaunchIdentifier = "tutorial-show-on-launch";
    public const string EndIdentifier = "tutorial-end";
    public const string ShowMeIdentifier = "tutorial-show-me";
    public const string SkipIdentifier = "tutorial-skip";
    public const string OpenSettingsIdentifier = "tutorial-open-settings";
    public const string DoneIdentifier = "tutorial-done";
    public const string SharingIdentifier = "tutorial-sharing";

    /// <summary>The Escape key's keycap; the Mac's says "esc", the key on a Windows keyboard says Esc.</summary>
    public const string EscapeKey = "Esc";
    public const string ReturnKey = "Enter";
    public const string UndoShortcut = "Ctrl+Z";
    public const string InkBlueKey = "B";

    public const string SharingTitle = "Before you share";

    /// <summary>
    /// The Mac's disclosure with the Windows permission sentence for a development build, which asks for no permission for
    /// zoom, effects or region sharing (WIN-INP-08; Gate 0 S-1).
    /// </summary>
    public const string SharingBody =
        "App or window sharing may leave out covers. Ask someone to check. Hiding, quitting or a crash removes them. Blur can leave details readable.\n\n" +
        "Drawing needs no permissions, and neither do zoom, effects or region sharing.";

    /// <summary>The Mac's disclosure as it is, for a packaged build, whose zoom, effects and region sharing ask for screen access (WIN-CAP-04).</summary>
    public const string SharingBodyAsking =
        "App or window sharing may leave out covers. Ask someone to check. Hiding, quitting or a crash removes them. Blur can leave details readable.\n\n" +
        "Drawing needs no permissions. Zoom, effects and region sharing ask when needed.";

    /// <summary>The Before you share disclosure for this copy.</summary>
    public static string SharingBodyFor(Capture.ScreenAccess access) => access == Capture.ScreenAccess.NotNeeded ? SharingBody : SharingBodyAsking;

    public const string UsageCounts = "Share usage counts";

    /// <summary>The Mac's note and help beside Share usage counts, with Windows for macOS.</summary>
    public const string UsageCountsNote = "Optional. Never your drawings or text.";
    public const string UsageCountsHelp =
        "Optional daily counts: version, Windows, chip and feature categories. No drawings, screenshots, text or file names. Change this in General.";

    /// <summary>WIN-PRV-01: while Windows may not send usage counts, the check box is off and unavailable and says so.</summary>
    public const string UsageCountsUnavailableNote = "Not available on Windows yet.";
    public const string UsageCountsUnavailableHelp = "Annotator for Windows doesn't send usage counts yet, so nothing is shared.";

    /// <summary>DV-4: Windows can move the tray icon into the overflow, and apps cannot pin themselves (WIN-UI-01).</summary>
    public const string TrayTitle = "Tray icon";
    public const string TrayDetail = "Windows can hide it under the arrow next to the clock. Keep it on the taskbar to open Annotator in one click.";
    public const string TrayAction = "Open taskbar settings";
    public const string TrayActionHelp = "Opens Windows Settings > Personalization > Taskbar. Under Other system tray icons, turn on Annotator.";

    /// <summary>DV-10 and WIN-INP-05: the default shortcuts replace other apps' keys, and a taken one stays empty.</summary>
    public const string ShortcutsTitle = "Shortcuts";
    public const string ShortcutsWorking = "Working";
    public const string ShortcutsWorkingLabel = "Shortcuts working";

    /// <summary>"Step 3 of 11", the progress track's accessibility value.</summary>
    public static string StepValue(TutorialStep step) => $"Step {step.Index + 1} of {TutorialSteps.All.Length}";

    /// <summary>The words of <paramref name="step"/> with the person's keys.</summary>
    public static TutorialStepText Step(TutorialStep step, WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? resume = Global(snapshot, GlobalShortcutAction.ResumeDrawing);
        string? showHide = Global(snapshot, GlobalShortcutAction.ShowHide);
        return step switch
        {
            TutorialStep.Draw => new("Start drawing",
                resume is null ? "Click any tool in the toolbar." : $"Press {resume}, or click any tool in the toolbar.", [],
                "No toolbar? Click Annotator's tray icon and choose Show drawings and tools."),
            TutorialStep.Rectangle => new("Pick the Rectangle",
                Local(snapshot, Tool.Rectangle) is { } rectangle ? $"Press {rectangle}, or click Rectangle in the toolbar." : "Click Rectangle in the toolbar.", [],
                "Look for the square in the toolbar. The ring shows where."),
            TutorialStep.Shape => new("Draw a rectangle", "Drag anywhere on the screen.", [], "Press, move the pointer, then let go."),
            TutorialStep.Color => new("Change the color", $"Press {InkBlueKey} for blue, or pick a color in the toolbar.", [],
                "Any color works. New drawings use it too."),
            TutorialStep.Arrow => new("Draw a curved arrow", "Arrows can bend around things.",
                [
                    Local(snapshot, Tool.Arrow) is { } arrow ? $"Pick the Arrow: press {arrow} or click it." : "Click the Arrow in the toolbar.",
                    "Choose Curved in the properties.",
                    $"Click a few points, then press {ReturnKey}.",
                ],
                $"Click, don't drag: each click adds a point. {ReturnKey} finishes the arrow."),
            TutorialStep.Pen => new("Circle something with the Pen", "Draw freely, like on paper.",
                [
                    Local(snapshot, Tool.Pen) is { } pen ? $"Pick the Pen: press {pen} or click it." : "Click the Pen in the toolbar.",
                    "Draw a circle around anything.",
                ],
                "Hold the mouse button down while you draw."),
            TutorialStep.Move => new("Move a drawing", "Select lets you change what you drew.",
                [
                    Local(snapshot, Tool.Select) is { } select ? $"Pick Select: press {select} or click it." : "Click Select in the toolbar.",
                    "Drag one of your drawings to a new place.",
                ],
                "Press on the drawing's line, not the empty space inside it."),
            TutorialStep.Undo => new("Undo", $"Press {UndoShortcut}, or click Undo in the toolbar.", [],
                "Undo takes back your last change. Redo brings it back."),
            TutorialStep.UseApps => new("Use your apps",
                $"Press {EscapeKey}, or click Use apps in the toolbar. Your drawings stay on the screen.", [],
                $"Typing text or drawing? The first {EscapeKey} finishes that. Press it again."),
            TutorialStep.DrawAgain => new("Draw again",
                resume is null ? "Click any tool in the toolbar." : $"Press {resume}, or click any tool in the toolbar.", [],
                resume is null ? "Any tool in the toolbar starts drawing." : $"{resume} works from any app."),
            TutorialStep.Hide => new("Hide and show", "Clear your screen in a moment. Everything comes back as you left it.",
                [
                    showHide is null ? "Click Hide drawings and tools in the toolbar." : $"Press {showHide}, or click Hide drawings and tools in the toolbar.",
                    showHide is null ? "Click Annotator's tray icon and choose Show drawings and tools." : $"Press {showHide} again to bring them back.",
                ],
                "You can also use Annotator's tray icon."),
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    /// <summary>The keycaps the card shows for <paramref name="goal"/>; empty when the goal has no key.</summary>
    public static ImmutableArray<string> Keys(TutorialGoal goal, WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? label = goal switch
        {
            TutorialGoal.Drawing => Global(snapshot, GlobalShortcutAction.ResumeDrawing),
            TutorialGoal.RectangleTool => Local(snapshot, Tool.Rectangle),
            TutorialGoal.ColorChanged => InkBlueKey,
            TutorialGoal.ArrowTool => Local(snapshot, Tool.Arrow),
            TutorialGoal.CurvedArrowDrawn => ReturnKey,
            TutorialGoal.PenTool => Local(snapshot, Tool.Pen),
            TutorialGoal.SelectTool => Local(snapshot, Tool.Select),
            TutorialGoal.Undone => UndoShortcut,
            TutorialGoal.UsingApps => EscapeKey,
            TutorialGoal.Hidden or TutorialGoal.Shown => Global(snapshot, GlobalShortcutAction.ShowHide),
            _ => null,
        };
        return Caps(label);
    }

    /// <summary>The finish card's keys: Draw, Use apps, Show / hide and Undo. A shortcut that does not work is left out.</summary>
    public static ImmutableArray<TutorialKeyRow> FinishRows(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ImmutableArray<TutorialKeyRow>.Builder rows = ImmutableArray.CreateBuilder<TutorialKeyRow>();
        if (Global(snapshot, GlobalShortcutAction.ResumeDrawing) is { } resume)
        {
            rows.Add(new TutorialKeyRow("Draw", Caps(resume)));
        }
        rows.Add(new TutorialKeyRow("Use apps", Caps(EscapeKey)));
        if (Global(snapshot, GlobalShortcutAction.ShowHide) is { } showHide)
        {
            rows.Add(new TutorialKeyRow("Show / hide", Caps(showHide)));
        }
        rows.Add(new TutorialKeyRow("Undo", Caps(UndoShortcut)));
        return rows.ToImmutable();
    }

    /// <summary>"Ctrl+Shift+2" as keycaps: Ctrl, Shift and 2.</summary>
    public static ImmutableArray<string> Caps(string? label) =>
        string.IsNullOrEmpty(label) ? [] : [.. label.Split('+', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>
    /// The shortcuts card's text (DV-10): which of the working shortcuts take over browsers' tab switching (Ctrl with a digit
    /// from 1 to 9), and where to change them.
    /// </summary>
    public static string ShortcutsDetail(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var labels = new List<string>();
        foreach (GlobalShortcutAction action in new[] { GlobalShortcutAction.LiveZoom, GlobalShortcutAction.ShowHide,
                                                        GlobalShortcutAction.ResumeDrawing, GlobalShortcutAction.HideOnly,
                                                        GlobalShortcutAction.BreakTimer })
        {
            if (snapshot.Shortcut(action) is { Status: GlobalShortcutStatus.Registered } &&
                WindowsShortcuts.Chord(action, snapshot.Preferences) is { Modifiers: ChordModifiers.Ctrl } chord &&
                chord.Key is "1" or "2" or "3" or "4" or "5" or "6" or "7" or "8" or "9")
            {
                labels.Add(chord.Label);
            }
        }
        return labels.Count switch
        {
            0 => "While Annotator runs, its shortcuts replace the same keys in other apps. Change them in Settings > Toolbar & Shortcuts.",
            1 => $"{labels[0]} replaces tab switching in browsers while Annotator runs. Change it in Settings > Toolbar & Shortcuts.",
            _ => $"{string.Join(", ", labels.Take(labels.Count - 1))} and {labels[^1]} replace tab switching in browsers while Annotator runs. " +
                 "Change them in Settings > Toolbar & Shortcuts.",
        };
    }

    /// <summary>The shortcuts Windows refused or another app took (WIN-INP-05), for the shortcuts card.</summary>
    public static IReadOnlyList<GlobalShortcutState> UnavailableShortcuts(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return [.. snapshot.GlobalShortcuts.Where(state => state.Status is GlobalShortcutStatus.AlreadyUsed or GlobalShortcutStatus.Refused)];
    }

    /// <summary>What the shortcuts card says about a shortcut Windows refused or another app took.</summary>
    public static string ShortcutUnavailable(GlobalShortcutState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string action = state.Action switch
        {
            GlobalShortcutAction.ShowHide => "Show / hide",
            GlobalShortcutAction.ResumeDrawing => "Draw",
            GlobalShortcutAction.HideOnly => "Hide only",
            GlobalShortcutAction.BreakTimer => "Break timer",
            _ => "Live zoom",
        };
        return state.Status == GlobalShortcutStatus.AlreadyUsed
            ? $"{state.Label} is used by another app, so {action} has no shortcut."
            : $"Windows doesn't allow {state.Label} as a shortcut, so {action} has no shortcut.";
    }

    /// <summary>A global shortcut's label, or null when another app took it or Windows refused it (WIN-INP-05).</summary>
    private static string? Global(WorkspaceSnapshot snapshot, GlobalShortcutAction action) =>
        snapshot.Shortcut(action) is { Status: GlobalShortcutStatus.AlreadyUsed or GlobalShortcutStatus.Refused }
            ? null
            : WindowsShortcuts.Label(action, snapshot.Preferences);

    /// <summary>A tool's key while drawing, or null when it has none.</summary>
    private static string? Local(WorkspaceSnapshot snapshot, Tool tool) =>
        snapshot.Preferences.ShortcutFor(ShortcutCommand.ForTool(tool)) is { } shortcut ? WindowsShortcuts.Label(shortcut) : null;
}
