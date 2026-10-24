namespace Annotator.App.Settings;

/// <summary>
/// The widths the Mac gives Settings controls whose titles are the same on Windows (reference/ux-reference/macos/settings and
/// dialogs frames), so the controls after them keep the Mac's positions although Segoe UI's words are narrower (D-4).
/// Titles that Windows words differently take their own width.
/// </summary>
internal static class SettingsWidths
{
    private static readonly Dictionary<string, double> s_buttons = new(StringComparer.Ordinal)
    {
        ["Check for updates..."] = 147,
        ["Buy me a coffee"] = 122,
        ["Refresh status"] = 108,
        ["Online guide"] = 126,
        ["Shortcuts"] = 110,
        ["Report a problem..."] = 140.5,
        ["Share feedback..."] = 130,
        ["Copy support info"] = 132.5,
        ["Open Downloads page..."] = 148.5,
        ["Privacy details"] = 88,
        ["Copy feedback"] = 115.5,
        ["Open GitHub draft"] = 135.5,
        ["Close"] = 58.5,
        ["Profiles"] = 93.5,
        ["Support info"] = 122.5,
        // The Mac's "Open Screen Recording settings", worded with Windows' lower-case page name: the same control.
        ["Open screen recording settings"] = 220,
        // The Mac's Allow screen access (its tour card's frame; Settings shows it while access is not allowed).
        ["Allow screen access"] = 147,
        // Settings > Break timer's Start button at the default length (BreakTimerPage widens other lengths alike).
        ["Start a 10-minute break"] = 197,
    };

    /// <summary>The disclosure groups' accessibility frames: from 22.5 points before the content's edge to the title's end.</summary>
    private static readonly Dictionary<string, double> s_disclosures = new(StringComparer.Ordinal)
    {
        ["What an update check sends"] = 210,
        ["What's shared?"] = 128,
        ["Screen-sharing limits"] = 154.5,
        ["Save and share"] = 120.5,
        ["Use an AI assistant"] = 142,
        ["What's changed"] = 132.5,
        ["Build & support details"] = 171.5,
        ["If access isn't working"] = 168.5,
    };

    public static double? Button(string title) => s_buttons.TryGetValue(title, out double width) ? width : null;

    public static double? Disclosure(string title) => s_disclosures.TryGetValue(title, out double width) ? width : null;
}
