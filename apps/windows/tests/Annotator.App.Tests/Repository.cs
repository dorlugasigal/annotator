namespace Annotator.App.Tests;

/// <summary>Paths in the repository that the checks read: the Mac UX reference, the app's sources and the built app.</summary>
internal static class Repository
{
    public static string Root { get; } = FindRoot();

    public static string Reference => Path.Combine(Root, "reference", "ux-reference", "macos");

    public static string AppSources => Path.Combine(Root, "apps", "windows", "src", "Annotator.App");

    /// <summary>
    /// Annotator.exe to start: ANNOTATOR_APP when set, else the Debug build, the Release build of the solution or the
    /// Native AOT publish, whichever exists. Every one is a development build without a package, which shows reference states.
    /// </summary>
    public static string App()
    {
        if (Environment.GetEnvironmentVariable("ANNOTATOR_APP") is { Length: > 0 } chosen)
        {
            return File.Exists(chosen) ? chosen : throw new FileNotFoundException("ANNOTATOR_APP names no file.", chosen);
        }
        string bin = Path.Combine(Root, "apps", "windows", "src", "Annotator.App", "bin", "x64");
        string[] candidates =
        [
            Path.Combine(bin, "Debug", "net10.0-windows10.0.26100.0", "win-x64", "Annotator.exe"),
            Path.Combine(bin, "Release", "net10.0-windows10.0.26100.0", "win-x64", "Annotator.exe"),
            Path.Combine(Root, "apps", "windows", "out", "app", "Annotator.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Annotator.exe was not found ({string.Join(", ", candidates)}). Build the app first.");
    }

    /// <summary>
    /// States that only Windows has, so the Mac reference has no file for them (the UX tool's ReferenceState.WindowsOnlyStates):
    /// the screen access explanation and Settings &gt; Permissions before Windows asked and after a refusal (WIN-CAP-04), and
    /// the stroke color popover while a shape label is edited, whose Mac capture is still missing.
    /// </summary>
    public static IReadOnlyList<string> WindowsOnlyStates { get; } =
        ["dialogs/screen-access", "dialogs/screen-access-asking", "pickers/color-stroke-label", "pickers/color-stroke-label-following",
         "settings/tab-permissions-not-asked", "settings/tab-permissions-refused"];

    /// <summary>Every reference state of the app windows, as the Mac reference names them ("toolbar/off"), and the Windows-only ones.</summary>
    public static IEnumerable<string> States()
    {
        string? filter = Environment.GetEnvironmentVariable("ANNOTATOR_A11Y_STATES");
        string[] patterns = filter is { Length: > 0 } ? filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : ["*"];
        foreach (string surface in new[] { "toolbar", "menu", "properties", "pickers", "tools-window", "settings", "tutorial", "dialogs", "region-sharing", "break-timer" })
        {
            // The break screen is always dark, so its states have only dark captures. A surface the Mac has not captured yet,
            // such as a new one before the reference is regenerated, has only its Windows-only states.
            IEnumerable<string> names = (Directory.Exists(Path.Combine(Reference, surface))
                    ? Directory.EnumerateFiles(Path.Combine(Reference, surface), "*.json") : [])
                .Select(Path.GetFileName)
                .Select(file => file!.EndsWith("-light.json", StringComparison.Ordinal) ? file[..^"-light.json".Length]
                    : file.EndsWith("-dark.json", StringComparison.Ordinal) ? file[..^"-dark.json".Length] : null)
                .OfType<string>()
                .Select(state => $"{surface}/{state}")
                .Concat(WindowsOnlyStates.Where(name => name.StartsWith(surface + "/", StringComparison.Ordinal)))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);
            foreach (string name in names)
            {
                if (patterns.Any(pattern => Matches(name, pattern)))
                {
                    yield return name;
                }
            }
        }
    }

    private static bool Matches(string name, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$");

    private static string FindRoot()
    {
        for (string? folder = AppContext.BaseDirectory; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (Directory.Exists(Path.Combine(folder, "reference", "ux-reference", "macos")) && Directory.Exists(Path.Combine(folder, "apps", "windows")))
            {
                return folder;
            }
        }
        throw new DirectoryNotFoundException("The repository root (with reference/ux-reference/macos) was not found above the test folder.");
    }
}
