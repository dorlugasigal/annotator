using System.Reflection;
using System.Runtime.InteropServices;
using Annotator.App.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.DataExchange;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.App;

/// <summary>
/// What kind of build is running: the Store package, the portable release (the ZIP that winget installs, built with
/// AnnotatorDistribution=portable) or a local development preview, which says so.
/// </summary>
internal static class BuildInfo
{
    private const int AppModelErrorNoPackage = 15700;

    /// <summary>The portable release: no package, and built as one. It is a release, not a development preview.</summary>
    public static bool IsPortable { get; } = !HasPackageIdentity() && Metadata("AnnotatorDistribution") == "portable";

    public static bool IsDevelopment { get; } = !HasPackageIdentity() && !IsPortable;

    /// <summary>
    /// The Mac's formatVersion from the shared version (WIN-DST-04): version, prerelease suffix and build, for example
    /// "0.1.0-beta.7 (25)". Development builds carry the same label and show the Local build badge in About, as on the Mac.
    /// </summary>
    public static string VersionLabel { get; } = FormatVersion();

    /// <summary>The per-user data folder (the package's local data once packaged, WIN-SYS-05).</summary>
    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        IsDevelopment ? "Annotator Development" : "Annotator");

    /// <summary>
    /// The optional support link of Buy me a coffee: the build's AnnotatorSupportURL (the Mac's ANNOTATOR_SUPPORT_URL),
    /// or null when the build has none or it fails the Mac's checks.
    /// </summary>
    public static Uri? SupportLink { get; } = SupportLinkFrom(Metadata("AnnotatorSupportURL"));

    /// <summary>Report a problem's GitHub form (the Mac's AnnotatorIssuesURL); Settings checks it before using it.</summary>
    public static string? IssuesUrl { get; } = Metadata("AnnotatorIssuesURL");

    /// <summary>The website of Help and About (the Mac's AnnotatorWebsiteURL); empty means the default website.</summary>
    public static string? Website { get; } = Metadata("AnnotatorWebsiteURL");

    /// <summary>The usage-count service's origin (the Mac's AnnotatorMetricsURL); source builds have none (specification 011).</summary>
    public static string? MetricsUrl { get; } = Metadata("AnnotatorMetricsURL");

    /// <summary>The Mac's buildLabel: the release build's stamp, or "Unstamped development build".</summary>
    public static string BuildLabel { get; } = Metadata("AnnotatorBuildStamp") is { Length: > 0 } stamp && !stamp.Contains("$(", StringComparison.Ordinal)
        ? stamp : "Unstamped development build";

    /// <summary>The build number of <see cref="VersionLabel"/>, or null when the build has none.</summary>
    public static int? BuildNumber { get; } = typeof(BuildInfo).Assembly.GetName().Version is { Revision: >= 0 } value ? value.Revision : null;

    /// <summary>The version that release notes are matched to: the version and its prerelease suffix, without the build.</summary>
    public static string NotesVersion { get; } = VersionLabel.Split(' ')[0];

    private static string? Metadata(string key) =>
        typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(item => item.Key == key)?.Value;

    /// <summary>Mac IssueReporting.supportURL: an https address with a host, and no user, password, port or fragment.</summary>
    public static Uri? SupportLinkFrom(string? value)
    {
        const string Scheme = "https://";
        if (string.IsNullOrEmpty(value) || value.Contains("$(", StringComparison.Ordinal) || value.Contains('#')
            || !value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string rest = value[Scheme.Length..];
        int end = rest.IndexOfAny(['/', '?']);
        string authority = end < 0 ? rest : rest[..end];
        return authority.Length > 0 && !authority.Contains('@') && !authority.Contains(':')
            && Uri.TryCreate(value, UriKind.Absolute, out Uri? link) && link.Host.Length > 0 ? link : null;
    }

    private static unsafe bool HasPackageIdentity()
    {
        uint length = 0;
        WIN32_ERROR result = PInvoke.GetCurrentPackageFullName(ref length, null);
        return (int)result != AppModelErrorNoPackage;
    }

    private static string FormatVersion()
    {
        Assembly assembly = typeof(BuildInfo).Assembly;
        string version = assembly.GetName().Version is { } value ? $"{value.Major}.{value.Minor}.{value.Build}" : "Version unavailable";
        string build = assembly.GetName().Version?.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        string? prerelease = Metadata("AnnotatorPrerelease") is { } suffix && !suffix.Contains("$(", StringComparison.Ordinal) ? suffix : null;
        return Annotator.Workspace.Packaging.SharedVersion.Format(version, prerelease, build);
    }
}

/// <summary>
/// One Annotator per Windows session (WIN-SYS-02): a named mutex marks the running instance, and a second
/// launch sends it WM_COPYDATA through a message-only window; the running instance shows the toolbar, or opens the board
/// file the second launch names (WIN-SYS-04).
/// </summary>
internal static class SingleInstance
{
    private const nuint Magic = 0x414E4E4F;
    private const nuint OpenBoardMagic = 0x414E4E42;
    private const string ShowToolbarCommand = "show-toolbar";

    /// <summary>The longest path a second launch forwards, in UTF-16 units (Windows' extended path limit).</summary>
    private const int MaximumPath = 32_767;

    public enum ForwardResult
    {
        Delivered,

        /// <summary>The first instance is still starting or ended; nothing was delivered.</summary>
        NotFound,

        /// <summary>The first instance did not answer in time.</summary>
        NoAnswer,
    }

    public static string MutexName { get; } = BuildInfo.IsDevelopment ? @"Local\Annotator.App.Development" : @"Local\Annotator.App";

    private static string WindowClass { get; } = BuildInfo.IsDevelopment ? "Annotator.App.Instance.Development" : "Annotator.App.Instance";

    /// <summary>
    /// Asks the running instance to show the toolbar, or to open <paramref name="board"/> (a full path) when the launch named
    /// a board file, waiting up to five seconds for it to start listening.
    /// </summary>
    public static unsafe ForwardResult Forward(string? board = null)
    {
        string payload = board ?? ShowToolbarCommand;
        if (payload.Length > MaximumPath)
        {
            return ForwardResult.NoAnswer;
        }
        for (int attempt = 0; attempt < 50; attempt++)
        {
            HWND target = PInvoke.FindWindowEx(HWND.HWND_MESSAGE, HWND.Null, WindowClass, null);
            if (!target.IsNull)
            {
                PInvoke.GetWindowThreadProcessId(target, out uint processId);
                PInvoke.AllowSetForegroundWindow(processId);
                fixed (char* text = payload)
                {
                    var data = new COPYDATASTRUCT
                    {
                        dwData = board is null ? Magic : OpenBoardMagic,
                        cbData = (uint)(payload.Length * sizeof(char)),
                        lpData = text,
                    };
                    nuint answer;
                    LRESULT sent = PInvoke.SendMessageTimeout(target, PInvoke.WM_COPYDATA, default, (nint)(&data),
                        SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, 3000, &answer);
                    return sent != default && answer == 1 ? ForwardResult.Delivered : ForwardResult.NoAnswer;
                }
            }
            Thread.Sleep(100);
        }
        return ForwardResult.NotFound;
    }

    /// <summary>Receives forwarded launches on the app thread.</summary>
    /// <param name="onOpenBoard">Opens the board file a second launch named.</param>
    public sealed class Listener(Action onShowToolbar, Action<string> onOpenBoard) : HiddenWindow(WindowClass, messageOnly: true)
    {
        protected override unsafe LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam)
        {
            if (message != PInvoke.WM_COPYDATA)
            {
                return base.WndProc(message, wParam, lParam);
            }
            var data = (COPYDATASTRUCT*)lParam.Value;
            if (data is null)
            {
                return default;
            }
            if (data->dwData == OpenBoardMagic && data->cbData > 0 && data->cbData % sizeof(char) == 0
                && data->cbData <= MaximumPath * sizeof(char) && data->lpData is not null)
            {
                string path = new((char*)data->lpData, 0, (int)(data->cbData / sizeof(char)));
                if (path.Contains('\0', StringComparison.Ordinal))
                {
                    return default;
                }
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() => onOpenBoard(path));
                return (LRESULT)1;
            }
            if (data->dwData != Magic || data->cbData != ShowToolbarCommand.Length * sizeof(char) ||
                !new ReadOnlySpan<char>(data->lpData, ShowToolbarCommand.Length).SequenceEqual(ShowToolbarCommand))
            {
                return default;
            }
            // Answer at once; the toolbar is shown after the sender's call returns.
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(() => onShowToolbar());
            return (LRESULT)1;
        }
    }
}
