namespace Annotator.Workspace.FirstUse;

/// <summary>Port of the Mac's IntroductionPreferences: whether the tour shows on launch (the "Show on launch" switch).</summary>
public sealed class IntroductionPreferences
{
    /// <summary>The Mac's UserDefaults key.</summary>
    public const string SuppressedKey = "introduction-suppressed";

    private readonly AppDefaults _defaults;

    public IntroductionPreferences(AppDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        _defaults = defaults;
    }

    public event Action? Changed;

    /// <summary>The person turned off Show on launch. Closing the tour never changes it.</summary>
    public bool Suppressed => _defaults.Bool(SuppressedKey);

    public void SetSuppressed(bool value)
    {
        _defaults.Set(SuppressedKey, value);
        Changed?.Invoke();
    }
}

/// <summary>When the tour may show (Mac IntroductionController.isEligible and showAutomatically).</summary>
public static class IntroductionRules
{
    /// <summary>Mac showManually's status when the tour cannot open now.</summary>
    public const string NotNowMessage = "Finish drawing and stop zoom, then open the introduction from Settings > Help.";

    /// <summary>
    /// Mac isEligible: not while drawing, zooming, animating the workspace, showing an error or a capture failure, or while
    /// any display has an edit in progress. An open tour closes as soon as this turns false.
    /// </summary>
    public static bool IsEligible(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Mode != WorkspaceMode.Draw && !snapshot.ZoomActive && snapshot.Visibility is null && snapshot.ErrorMessage is null
            && snapshot.CaptureFailureMessage is null && !SupportReminderRules.HasActiveEdit(snapshot);
    }

    /// <summary>
    /// Mac showAutomatically: once per launch, never for a launch at sign-in (WIN-SYS-01: a quiet startup-task launch), never
    /// after the person turned off Show on launch, and only at a safe moment.
    /// </summary>
    public static bool ShowsAutomatically(bool startupLaunch, bool suppressed, bool eligible) => !startupLaunch && !suppressed && eligible;
}

/// <summary>
/// The tour's words (Mac IntroductionController), with Windows key names and the platform terms of specification 013. The
/// first page stays under 120 words.
/// </summary>
public static class IntroductionText
{
    public const string WindowTitle = "Welcome to Annotator";

    public const string DrawTitle = "Let's make a point.";
    public const string DrawLead = "Pick a tool and draw over your screen. Try these three shortcuts.";
    public const string DrawRow = "Draw";
    public const string DrawRowDetail = "Start or resume drawing.";
    public const string UseAppsRow = "Use apps";
    public const string UseAppsRowDetail = "Your drawings stay visible.";
    public const string ShowHideRow = "Show / hide";
    public const string ShowHideRowDetail = "Covers hide too.";

    /// <summary>The Escape key's keycap; the Mac's says "esc", the key on a Windows keyboard says Esc.</summary>
    public const string EscapeKeycap = "Esc";
    public const string EscapeName = "Escape";
    public const string EscapeLine = "Esc finishes text or cancels a drawing first. Press again to use apps. It also exits zoom.";
    public const string SharingTitle = "Before you share";
    public const string SharingIdentifier = "introduction-sharing";

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

    public const string ColorTitle = "A little color goes a long way.";
    public const string ColorLead = "Keep the outline bold and the fill light. Their opacity controls are separate.";
    public const string ColorDetail = "Pick a swatch, try a gradient, or open the color wheel. New shape labels follow the outline's color.";
    public const string ColorUndo = "Select a mark to change it. Undo is always close by.";

    public const string FocusTitle = "Right here. See it?";
    public const string FocusLead = "Give your pointer a ring. Light up a detail with Spotlight. Zoom in when the small print matters.";
    public const string FocusDetail = "Find these in Tools > Focus. In Settings > Pointer, choose the highlight's shape, color and size\u2014or show it only when you move or click.";
    public const string FocusBoards = "Your drawings stay editable. Save a board to return to it, or keep a useful diagram in My shapes.";

    public const string SetupTitle = "You're ready to draw.";

    /// <summary>The Mac's sentences about permissions, with the Windows fact that a development build needs none (WIN-INP-08).</summary>
    public const string SetupLead = "No permissions needed for that, or for anything else. These two are optional; you can set them up later.";

    /// <summary>The Mac's sentences as they are, for a packaged build, which asks for screen access when needed (WIN-CAP-04).</summary>
    public const string SetupLeadAsking = "No permissions needed for that. These two are optional; you can set them up later.";

    /// <summary>Page 4's lead for this copy: a packaged build asks for screen access, a development build needs none.</summary>
    public static string SetupLeadFor(Capture.ScreenAccess access) => access == Capture.ScreenAccess.NotNeeded ? SetupLead : SetupLeadAsking;

    /// <summary>The Before you share disclosure for this copy.</summary>
    public static string SharingBodyFor(Capture.ScreenAccess access) => access == Capture.ScreenAccess.NotNeeded ? SharingBody : SharingBodyAsking;

    /// <summary>DV-4: Windows can move the tray icon into the overflow, and apps cannot pin themselves (WIN-UI-01).</summary>
    public const string TrayTitle = "Tray icon";
    public const string TrayDetail = "Windows can hide it under the arrow next to the clock. Keep it on the taskbar to open Annotator in one click.";
    public const string TrayAction = "Open taskbar settings";
    public const string TrayActionHelp = "Opens Windows Settings > Personalization > Taskbar. Under Other system tray icons, turn on Annotator.";

    /// <summary>DV-10 and WIN-INP-05: the default shortcuts replace other apps' keys, and a taken one stays empty.</summary>
    public const string ShortcutsTitle = "Shortcuts";
    public const string ShortcutsWorking = "Working";
    public const string ShortcutsWorkingLabel = "Shortcuts working";

    public const string SharingNote =
        "Sharing your screen? Check the other person's view. Window sharing can miss overlays, and blur isn't secure redaction. " +
        "Stop sharing before quitting or restarting: covers disappear too.";

    public const string UsageCounts = "Share usage counts";

    /// <summary>The Mac's note and help beside Share usage counts, with Windows for macOS.</summary>
    public const string UsageCountsNote = "Optional. Never your drawings or text.";
    public const string UsageCountsHelp =
        "Optional daily counts: version, Windows, chip and feature categories. No drawings, screenshots, text or file names. Change this in General.";

    /// <summary>WIN-PRV-01: while Windows may not send usage counts, the check box is off and unavailable and says so.</summary>
    public const string UsageCountsUnavailableNote = "Not available on Windows yet.";
    public const string UsageCountsUnavailableHelp = "Annotator for Windows doesn't send usage counts yet, so nothing is shared.";

    public const string ShowOnLaunch = "Show on launch";
    public const string Back = "Back";
    public const string Next = "Next";
    public const string GotIt = "Got it";
    public const string FindAgain = "Find this again in Settings > Help.";

    public const string IllustrationIdentifier = "introduction-illustration";
    public const string ReplayHelp = "Click to play the animation again.";
    public const string ReplayAction = "Replay animation";

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
                Shortcuts.WindowsShortcuts.Chord(action, snapshot.Preferences) is { Modifiers: Shortcuts.ChordModifiers.Ctrl } chord &&
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
}
