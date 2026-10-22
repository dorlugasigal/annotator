using System.Globalization;
using System.Text.RegularExpressions;

namespace Annotator.Workspace.Packaging;

/// <summary>
/// The version and build that the Mac and Windows apps share (WIN-DST-04): MARKETING_VERSION, ANNOTATOR_PRERELEASE and
/// CURRENT_PROJECT_VERSION in the base settings of apps/macos/project.yml, read the way tools/release/release.py reads them.
/// The app's build gets the same values from SharedVersion.props; this type gives the rules to tests and tools.
/// </summary>
public sealed partial record SharedVersion
{
    private SharedVersion(int major, int minor, int patch, string prerelease, int build)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Build = build;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The prerelease suffix without its dash, such as "beta.7", or empty for a stable release.</summary>
    public string Prerelease { get; }

    /// <summary>The shared, always-increasing build number (CURRENT_PROJECT_VERSION).</summary>
    public int Build { get; }

    /// <summary>The numeric version, such as "0.1.0" (CFBundleShortVersionString on the Mac).</summary>
    public string Version => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>The release-notes version: the numeric version and its prerelease suffix, such as "0.1.0-beta.7".</summary>
    public string NotesVersion => Prerelease.Length == 0 ? Version : $"{Version}-{Prerelease}";

    /// <summary>What About shows, the Mac's formatVersion: "0.1.0-beta.7 (25)".</summary>
    public string Label => Format(Version, Prerelease, Build.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The MSIX package version (Major + 1).Minor.Build.0 (WIN-DST-04). The Store needs a first number of at least 1 and
    /// keeps the fourth for itself; the patch number is left out, so the always-increasing build keeps later releases
    /// sorting higher.
    /// </summary>
    public string MsixVersion => string.Create(CultureInfo.InvariantCulture, $"{Major + 1}.{Minor}.{Build}.0");

    /// <summary>The Mac's formatVersion: the version, "-" and the prerelease suffix when there is one, then the build in parentheses.</summary>
    public static string Format(string version, string? prerelease, string build) =>
        string.IsNullOrEmpty(prerelease) ? $"{version} ({build})" : $"{version}-{prerelease} ({build})";

    /// <summary>Checks the parts as tools/release/release.py does and against the Windows limits (16-bit version parts).</summary>
    /// <exception cref="FormatException">A part is out of range or the suffix is not alpha, beta or rc with a number.</exception>
    public static SharedVersion Create(int major, int minor, int patch, string prerelease, int build)
    {
        ArgumentNullException.ThrowIfNull(prerelease);
        if (major < 0 || minor < 0 || patch < 0)
        {
            throw new FormatException("Version numbers can't be negative.");
        }
        if (prerelease.Length > 0 && !PrereleasePattern().IsMatch(prerelease))
        {
            throw new FormatException($"The prerelease suffix \"{prerelease}\" must be alpha, beta or rc with a number, such as beta.7.");
        }
        if (build < 1)
        {
            throw new FormatException("Build must be a positive integer.");
        }
        // MSIX and assembly version parts are 16-bit, and an assembly version part may be at most 65534.
        if (major + 1 > ushort.MaxValue - 1 || minor > ushort.MaxValue - 1 || patch > ushort.MaxValue - 1 || build > ushort.MaxValue - 1)
        {
            throw new FormatException("The version or build is too large for a Windows version number.");
        }
        return new SharedVersion(major, minor, patch, prerelease, build);
    }

    /// <summary>
    /// Reads the base settings of apps/macos/project.yml as tools/release/release.py does: exactly one MARKETING_VERSION,
    /// ANNOTATOR_PRERELEASE and CURRENT_PROJECT_VERSION each, at four spaces of indentation, quotes removed.
    /// </summary>
    /// <exception cref="FormatException">A setting is missing, repeated or invalid; the message names it.</exception>
    public static SharedVersion Parse(string projectYml)
    {
        ArgumentNullException.ThrowIfNull(projectYml);
        string version = Setting(projectYml, "MARKETING_VERSION");
        string prerelease = Setting(projectYml, "ANNOTATOR_PRERELEASE");
        string build = Setting(projectYml, "CURRENT_PROJECT_VERSION");
        Match numbers = VersionPattern().Match(version);
        if (!numbers.Success)
        {
            throw new FormatException($"MARKETING_VERSION \"{version}\" must be three numbers, such as 0.1.0. Keep the prerelease suffix separate.");
        }
        if (!BuildPattern().IsMatch(build))
        {
            throw new FormatException("Build must be a positive integer.");
        }
        return Create(Number(numbers.Groups[1].Value), Number(numbers.Groups[2].Value), Number(numbers.Groups[3].Value), prerelease,
            Number(build));
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

    [GeneratedRegex("^(alpha|beta|rc)\\.[1-9][0-9]*$")]
    private static partial Regex PrereleasePattern();

    [GeneratedRegex("^[1-9][0-9]*$")]
    private static partial Regex BuildPattern();
}
