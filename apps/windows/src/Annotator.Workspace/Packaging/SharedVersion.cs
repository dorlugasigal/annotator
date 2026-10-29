using System.Globalization;
using System.Text.RegularExpressions;

namespace Annotator.Workspace.Packaging;

/// <summary>
/// The version that the Mac and Windows apps share (WIN-DST-04): MARKETING_VERSION in the base settings of
/// apps/macos/project.yml, a plain X.Y.Z read the way tools/release/release.py reads it. Its build is derived from it and
/// never counted (<see cref="Build"/>); CURRENT_PROJECT_VERSION, the Mac's build, must hold the same number. The app's
/// build gets the same values from SharedVersion.props; this type gives the rules to the app, tests and tools.
/// </summary>
public sealed partial record SharedVersion
{
    /// <summary>The largest minor or patch number. Above it, the derived build would stop growing with the version.</summary>
    public const int MaximumMinorOrPatch = 99;

    private SharedVersion(int major, int minor, int patch)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>
    /// The build, derived and never counted: Major * 10000 + Minor * 100 + Patch, so 0.2.0 is 200. It grows with every
    /// version and is above the build of every version before 0.2.0 (the last was 35), so release notes, What's changed
    /// and a portable copy's update check keep comparing builds.
    /// </summary>
    public int Build => (Major * 10_000) + (Minor * 100) + Patch;

    /// <summary>The version, such as "0.2.0": what the app shows (CFBundleShortVersionString on the Mac) and its notes' version.</summary>
    public string Version => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>The assembly and file version, Major.Minor.Patch.0, such as 0.2.0.0.</summary>
    public string AssemblyVersion => $"{Version}.0";

    /// <summary>
    /// The MSIX package version (Major + 1).Minor.Patch.0 (WIN-DST-04), such as 1.2.0.0 for 0.2.0. The Store needs a first
    /// number of at least 1 and keeps the fourth for itself. Every later version sorts higher, and 1.2.0.0 is above
    /// 1.1.35.0, the last package before 0.2.0.
    /// </summary>
    public string MsixVersion => string.Create(CultureInfo.InvariantCulture, $"{Major + 1}.{Minor}.{Patch}.0");

    /// <summary>
    /// Checks the numbers as tools/release/release.py does and against the Windows limits: minor and patch at most 99, so
    /// the derived build keeps growing, a major number that fits a Windows version number, and a positive build.
    /// </summary>
    /// <exception cref="FormatException">A number is out of range; the message says which.</exception>
    public static SharedVersion Create(int major, int minor, int patch)
    {
        if (major < 0 || minor < 0 || patch < 0)
        {
            throw new FormatException("Version numbers can't be negative.");
        }
        if (minor > MaximumMinorOrPatch || patch > MaximumMinorOrPatch)
        {
            throw new FormatException("Keep minor and patch at 99 or lower, so the derived build keeps growing.");
        }
        // MSIX and assembly version parts are 16-bit, and an assembly version part may be at most 65534.
        if (major + 1 > ushort.MaxValue - 1)
        {
            throw new FormatException("The version is too large for a Windows version number.");
        }
        if (major == 0 && minor == 0 && patch == 0)
        {
            throw new FormatException("The version must be above 0.0.0, so that its build is positive.");
        }
        return new SharedVersion(major, minor, patch);
    }

    /// <summary>
    /// Reads the base settings of apps/macos/project.yml as tools/release/release.py does: exactly one MARKETING_VERSION
    /// and one CURRENT_PROJECT_VERSION, at four spaces of indentation, quotes removed. MARKETING_VERSION is three numbers
    /// with nothing after them, and CURRENT_PROJECT_VERSION must be its derived build, so the Mac and Windows stay in step.
    /// </summary>
    /// <exception cref="FormatException">A setting is missing, repeated or invalid; the message names it.</exception>
    public static SharedVersion Parse(string projectYml)
    {
        ArgumentNullException.ThrowIfNull(projectYml);
        string version = Setting(projectYml, "MARKETING_VERSION");
        string build = Setting(projectYml, "CURRENT_PROJECT_VERSION");
        Match numbers = VersionPattern().Match(version);
        if (!numbers.Success)
        {
            throw new FormatException($"MARKETING_VERSION \"{version}\" must be three numbers, such as 0.2.0, with nothing after them.");
        }
        SharedVersion shared = Create(Number(numbers.Groups[1].Value), Number(numbers.Groups[2].Value), Number(numbers.Groups[3].Value));
        string derived = shared.Build.ToString(CultureInfo.InvariantCulture);
        if (build != derived)
        {
            throw new FormatException($"CURRENT_PROJECT_VERSION must be {derived}, the build derived from {shared.Version} " +
                $"(Major * 10000 + Minor * 100 + Patch), not \"{build}\". Change the version with python3 tools/release/release.py prepare --version X.Y.Z.");
        }
        return shared;
    }

    private static string Setting(string text, string name)
    {
        MatchCollection matches = Regex.Matches(text, $"(?m)^    {name}: *(.*?) *\r?$");
        if (matches.Count != 1)
        {
            throw new FormatException($"Expected one base {name} setting in project.yml.");
        }
        return matches[0].Groups[1].Value.Trim('"', '\'');
    }

    private static int Number(string digits) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new FormatException($"\"{digits}\" is too large for a version number.");

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$")]
    private static partial Regex VersionPattern();
}
