using System.Collections.Immutable;
using Annotator.Core;
using Annotator.Core.Tutorial;
using Annotator.Workspace.Shortcuts;

namespace Annotator.Workspace.FirstUse;

/// <summary>
/// The words of one tutorial step: its title, one line when the step has one goal, a line per goal when it has several, and a
/// hint. Keys are marked in braces, such as "Press {2}"; the card shows them as keycaps (<see cref="TutorialText.Segments"/>).
/// </summary>
public sealed record TutorialStepText(string Title, string? Detail, ImmutableArray<string> Goals, string Hint);

/// <summary>A row of the finish card: what a key does and its keycaps.</summary>
public sealed record TutorialKeyRow(string Label, ImmutableArray<string> Keys);

/// <summary>
/// One word or key of a line (Mac <c>TutorialLineToken</c>), whether a space comes before it, and for a key its place among
/// the line's keys.
/// </summary>
public readonly record struct TutorialLineToken(string Text, bool IsKey, bool SpaceBefore, int KeyIndex);

/// <summary>
/// The tutorial's words (Mac TutorialViews): short, casual and direct, with Windows key names and the platform terms of
/// specification 013: Ctrl for Control and Command, Esc, Enter for Return, and the tray icon for the menu bar icon. Keys
/// follow the person's shortcuts; a shortcut that is missing or that Windows refused uses the words without a key.
/// </summary>
public static class TutorialText
{
    public const string WelcomeTitle = "Welcome to Annotator";
    public const string WelcomeBody = "Let's learn the basics. It takes two minutes, and keys or mouse both work.";
    public const string Start = "Start tutorial";
    public const string NotNow = "Not now";
    public const string ShowOnLaunch = "Show on launch";
    public const string FindAgain = "Find this again in Settings > Help.";

    public const string EndTutorial = "End tutorial";
    public const string ShowMe = "Show me";
    public const string SkipStep = "Skip step";
    public const string Done = "Done";

    public const string FinishTitle = "You did it!";
    public const string FinishLead = "Keep these handy:";
    public const string ChangeShortcuts = "Change any shortcut in Settings > Toolbar & Shortcuts.";
    public const string OpenSettings = "Open Settings";
    public const string PracticeAgain = "Replay this any time from Settings > Help.";

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
    public const string InkGreenKey = "G";

    /// <summary>
    /// Whether the color step asks for green: when the drawing (or the tool) is blue already, the blue ink key would change
    /// nothing.
    /// </summary>
    public static bool AsksForGreen(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RGBA color = snapshot.Style.Color;
        return Math.Abs(color.Red - Palette.Blue.Red) < 0.002 && Math.Abs(color.Green - Palette.Blue.Green) < 0.002 &&
               Math.Abs(color.Blue - Palette.Blue.Blue) < 0.002;
    }

    /// <summary>The ink key the color step shows: B, or G when the drawing is blue already.</summary>
    public static string ColorKey(WorkspaceSnapshot snapshot) => AsksForGreen(snapshot) ? InkGreenKey : InkBlueKey;

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
        string Pick(string name, Tool tool) =>
            Local(snapshot, tool) is { } key ? $"Grab the {name}: press {{{key}}} or click it" : $"Grab the {name}: click it in the toolbar";
        return step switch
        {
            TutorialStep.Rectangle => new("Grab the Rectangle",
                Local(snapshot, Tool.Rectangle) is { } rectangle ? $"Press {{{rectangle}}}, or click it in the toolbar." : "Click the Rectangle in the toolbar.", [],
                "It's the square. Look for the blue ring."),
            TutorialStep.Shape => new("Draw a box", "Drag anywhere on your screen.", [], "Press, drag, let go."),
            TutorialStep.Color => new(AsksForGreen(snapshot) ? "Make it green" : "Make it blue",
                $"Press {{{ColorKey(snapshot)}}}, or pick any color in the toolbar.", [],
                "Any color works. New drawings use it too."),
            TutorialStep.Arrow => new("Draw a curved arrow", null,
                [Pick("Arrow", Tool.Arrow), "Turn on Curved in the properties", $"Click points, then double-click (or {{{ReturnKey}}})"],
                "Click, don't drag. Can't see Curved? Click the sliders button."),
            TutorialStep.Pen => new("Circle something", null,
                [Pick("Pen", Tool.Pen), "Draw a circle around anything"],
                "Hold the mouse button down as you draw."),
            TutorialStep.Move => new("Move it", null,
                [Local(snapshot, Tool.Select) is { } select ? $"Grab Select: press {{{select}}} or click it" : "Grab Select: click it in the toolbar",
                 "Drag, turn or resize a drawing"],
                "Grab the line, not the empty middle."),
            TutorialStep.Undo => new("Oops! Undo that", $"Press {{{UndoShortcut}}}, or click Undo.", [],
                "Changed your mind again? Redo is right next to it."),
            TutorialStep.UseApps => new("Back to your apps", $"Press {{{EscapeKey}}}, or click Use apps. Your drawings stay put.", [],
                $"Typing or mid-drawing? Press {{{EscapeKey}}} twice."),
            TutorialStep.DrawAgain => new("Draw again",
                resume is null ? "Click any tool in the toolbar." : $"Press {{{resume}}} from any app, or click a tool.", [],
                resume is null ? "Any tool in the toolbar starts drawing." : "That shortcut works everywhere, even in other apps."),
            TutorialStep.Hide => new("Now you see it\u2026", null,
                [
                    showHide is null ? "Hide it all: click \u00D7 in the toolbar" : $"Hide it all: press {{{showHide}}}",
                    showHide is null ? "Bring it back: use the tray icon" : $"Bring it back: press {{{showHide}}} again",
                ],
                "No keys? Use \u00D7 in the toolbar to hide, and the tray icon to bring it back."),
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
    }

    /// <summary>Whether the card shows the keyboard for <paramref name="goal"/>: a shortcut with Ctrl, Shift or Alt, or Esc.</summary>
    public static bool ShowsKeyboard(TutorialGoal goal, WorkspaceSnapshot snapshot) => TutorialKeyboard.Shows(Keys(goal, snapshot), mac: false);

    /// <summary>
    /// A line split into its words and its keys: "Press {Ctrl+Z}, or click Undo." gives ("Press ", no keys), ("", Ctrl and Z)
    /// and (", or click Undo.", no keys).
    /// </summary>
    public static ImmutableArray<(string Text, ImmutableArray<string> Keys)> Segments(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        ImmutableArray<(string, ImmutableArray<string>)>.Builder segments = ImmutableArray.CreateBuilder<(string, ImmutableArray<string>)>();
        int index = 0;
        while (index < line.Length)
        {
            int open = line.IndexOf('{', index);
            int close = open < 0 ? -1 : line.IndexOf('}', open + 1);
            if (open < 0 || close < 0)
            {
                segments.Add((line[index..], []));
                break;
            }
            if (open > index)
            {
                segments.Add((line[index..open], []));
            }
            segments.Add((string.Empty, Caps(line[(open + 1)..close])));
            index = close + 1;
        }
        return segments.ToImmutable();
    }

    /// <summary>
    /// The line's words and keys in order (Mac TutorialText.tokens). Words and keys written together, such as "{2},", stay
    /// together: only a token with <see cref="TutorialLineToken.SpaceBefore"/> may start a new row.
    /// </summary>
    public static ImmutableArray<TutorialLineToken> Tokens(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        ImmutableArray<TutorialLineToken>.Builder tokens = ImmutableArray.CreateBuilder<TutorialLineToken>();
        bool space = false;
        int keys = 0;
        foreach ((string text, ImmutableArray<string> caps) in Segments(line))
        {
            if (caps.IsEmpty)
            {
                string[] words = text.Split(' ');
                for (int index = 0; index < words.Length; index++)
                {
                    space |= index > 0;
                    if (words[index].Length == 0)
                    {
                        continue;
                    }
                    tokens.Add(new TutorialLineToken(words[index], false, space && tokens.Count > 0, 0));
                    space = false;
                }
                continue;
            }
            tokens.Add(new TutorialLineToken(string.Join('+', caps), true, space && tokens.Count > 0, keys++));
            space = false;
        }
        return tokens.ToImmutable();
    }

    /// <summary>
    /// What screen readers read for keys (Mac TutorialText.spoken): "Ctrl", "Shift" and "2" are "Ctrl + Shift + 2", and Esc
    /// is "Escape".
    /// </summary>
    public static string Spoken(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return string.Join(" + ", keys.Select(key => key == EscapeKey ? "Escape" : key));
    }

    /// <summary>The line as screen readers and tests read it: keys without their braces.</summary>
    public static string Plain(string line) => line?.Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal) ?? string.Empty;

    /// <summary>The keycaps the card shows for <paramref name="goal"/>; empty when the goal has no key.</summary>
    public static ImmutableArray<string> Keys(TutorialGoal goal, WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? label = goal switch
        {
            TutorialGoal.Drawing => Global(snapshot, GlobalShortcutAction.ResumeDrawing),
            TutorialGoal.RectangleTool => Local(snapshot, Tool.Rectangle),
            TutorialGoal.ColorChanged => ColorKey(snapshot),
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
            rows.Add(new TutorialKeyRow("Show or hide", Caps(showHide)));
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
