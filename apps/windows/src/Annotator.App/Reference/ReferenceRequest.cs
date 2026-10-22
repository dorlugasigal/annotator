using System.Globalization;

namespace Annotator.App.Reference;

/// <summary>The theme a reference state is shown in.</summary>
internal enum ReferenceTheme
{
    Light,
    Dark,
}

/// <summary>
/// A development-only request to show one state of the macOS UX reference (WIN-PAR-06), for
/// apps/windows/tools/Annotator.UXReference: <c>--ux-reference toolbar/off --theme dark --out &lt;folder&gt;</c>.
/// <c>--usage-counts available</c> shows Share usage counts as it will be once Windows may send them (WIN-PRV-01).
/// <c>--contrast nightsky</c> (aquatic, desert, dusk or nightsky) and <c>--text-scale 2.25</c> show it as in a Windows contrast
/// theme or with Windows' Text size setting, through WinUI's test hooks (WIN-A11Y-03), for the accessibility checks.
/// </summary>
internal sealed record ReferenceRequest(string Surface, string State, ReferenceTheme Theme, string Output, int TimeoutSeconds,
                                       bool Docked = false, bool UsageCountsAvailable = false)
{
    public const string Switch = "--ux-reference";

    /// <summary>The contrast theme to simulate, or null for the light or dark theme.</summary>
    public ContrastPalette? Contrast { get; init; }

    /// <summary>The text size to simulate, from 1 (Windows' default) to 2.25 (its largest).</summary>
    public double TextScale { get; init; } = 1;

    /// <summary><c>--accessibility</c>: ready.json also reports the keyboard path, context menus, cut-off text and colors.</summary>
    public bool Accessibility { get; init; }

    /// <summary>The state's name as the reference folder spells it, for example "toolbar/off".</summary>
    public string Name => $"{Surface}/{State}";

    /// <summary>Null when the switch is absent. Throws <see cref="FormatException"/> for a malformed request.</summary>
    public static ReferenceRequest? Parse(IReadOnlyList<string> args)
    {
        int index = IndexOf(args, Switch);
        if (index < 0)
        {
            return null;
        }
        string name = Value(args, index);
        int slash = name.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == name.Length - 1)
        {
            throw new FormatException($"{Switch} needs <surface>/<state>, for example toolbar/off.");
        }
        string theme = IndexOf(args, "--theme") is var themeIndex and >= 0 ? Value(args, themeIndex) : "light";
        string output = IndexOf(args, "--out") is var outIndex and >= 0 ? Value(args, outIndex)
            : throw new FormatException("--out <folder> is required.");
        int timeout = IndexOf(args, "--timeout") is var timeoutIndex and >= 0
            ? int.Parse(Value(args, timeoutIndex), NumberStyles.None, CultureInfo.InvariantCulture) : 30;
        bool docked = IndexOf(args, "--placement") is var placementIndex and >= 0 && Value(args, placementIndex) switch
        {
            "docked" => true,
            "offscreen" => false,
            _ => throw new FormatException("--placement is offscreen or docked."),
        };
        bool usageCounts = IndexOf(args, "--usage-counts") is var usageIndex and >= 0 && Value(args, usageIndex) switch
        {
            "available" => true,
            "unavailable" => false,
            _ => throw new FormatException("--usage-counts is available or unavailable."),
        };
        ContrastPalette? contrast = IndexOf(args, "--contrast") is var contrastIndex and >= 0
            ? ContrastPalette.Named(Value(args, contrastIndex)) ?? throw new FormatException("--contrast is aquatic, desert, dusk or nightsky.")
            : null;
        double textScale = IndexOf(args, "--text-scale") is var scaleIndex and >= 0
            ? double.Parse(Value(args, scaleIndex), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) : 1;
        if (textScale is < 1 or > 2.25)
        {
            throw new FormatException("--text-scale is from 1 to 2.25, as Windows' Text size setting.");
        }
        return new ReferenceRequest(name[..slash], name[(slash + 1)..], theme switch
        {
            "light" => ReferenceTheme.Light,
            "dark" => ReferenceTheme.Dark,
            _ => throw new FormatException("--theme is light or dark."),
        }, Path.GetFullPath(output), Math.Clamp(timeout, 5, 300), docked, usageCounts)
        {
            Contrast = contrast,
            TextScale = textScale,
            Accessibility = IndexOf(args, "--accessibility") >= 0,
        };
    }

    /// <summary>True when the state is shown with a contrast theme or larger text (the accessibility checks).</summary>
    public bool SimulatesAccessibility => Contrast is not null || TextScale > 1;

    private static int IndexOf(IReadOnlyList<string> args, string option)
    {
        for (int index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], option, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private static string Value(IReadOnlyList<string> args, int index) =>
        index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[index + 1]
            : throw new FormatException($"{args[index]} needs a value.");
}
