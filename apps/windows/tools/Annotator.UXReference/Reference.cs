using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Annotator.Workspace.Breaks;
using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Settings;

namespace Annotator.UXReference;

/// <summary>
/// One accessibility node in the reference JSON shape: role, label (UI Automation Name), identifier (AutomationId),
/// value, help, frame [x, y, width, height] in points or DIPs from the surface's top-left corner, and children.
/// </summary>
internal sealed class AxNode
{
    public required string Role { get; init; }

    public string? Label { get; init; }

    public string? Identifier { get; init; }

    /// <summary>A string or a number, as in the reference.</summary>
    public JsonValue? Value { get; init; }

    public string? Help { get; init; }

    /// <summary>The recorded title (AppKit's accessibility title), when the reference has one; not compared.</summary>
    public string? Title { get; init; }

    public double[]? Frame { get; init; }

    /// <summary>False for a disabled control. The reference never records it (see the README), so it is reported, not compared.</summary>
    public bool Enabled { get; init; } = true;

    public List<AxNode> Children { get; } = [];

    /// <summary>The words that identify the node for alignment: a static text is its text, other nodes their label.</summary>
    public string Key => $"{Role}|{Label ?? (Role == "AXStaticText" ? ValueText : string.Empty)}|{Identifier}";

    public string? ValueText => Value is null ? null
        : Value.TryGetValue(out double number) ? number.ToString("0.####", CultureInfo.InvariantCulture)
        : Value.TryGetValue(out bool flag) ? (flag ? "true" : "false")
        : Value.GetValue<string>();

    public string Describe()
    {
        var text = new StringBuilder(Role);
        if (Label is not null)
        {
            text.Append(" \"").Append(Label).Append('"');
        }
        else if (Role == "AXStaticText" && ValueText is { } value)
        {
            text.Append(" \"").Append(value).Append('"');
        }
        if (Identifier is not null)
        {
            text.Append(" #").Append(Identifier);
        }
        return text.ToString();
    }

    public JsonObject ToJson()
    {
        var json = new JsonObject();
        if (Frame is { } frame)
        {
            json["frame"] = new JsonArray([.. frame.Select(value => (JsonNode)JsonValue.Create(Math.Round(value, 2)))]);
        }
        if (!Enabled)
        {
            json["enabled"] = false;
        }
        if (Help is not null)
        {
            json["help"] = Help;
        }
        if (Identifier is not null)
        {
            json["identifier"] = Identifier;
        }
        if (Label is not null)
        {
            json["label"] = Label;
        }
        json["role"] = Role;
        if (Value is not null)
        {
            json["value"] = Value.DeepClone();
        }
        if (Children.Count > 0)
        {
            json["children"] = new JsonArray([.. Children.Select(child => (JsonNode)child.ToJson())]);
        }
        return json;
    }

    public static AxNode FromJson(JsonObject json)
    {
        var node = new AxNode
        {
            Role = json["role"]?.GetValue<string>() ?? "AXUnknown",
            Label = json["label"]?.GetValue<string>(),
            Identifier = json["identifier"]?.GetValue<string>(),
            Value = json["value"] is JsonValue value ? value.DeepClone().AsValue() : null,
            Help = json["help"]?.GetValue<string>(),
            Title = json["title"]?.GetValue<string>(),
            Frame = json["frame"] is JsonArray frame ? [.. frame.Select(item => item!.GetValue<double>())] : null,
        };
        if (json["children"] is JsonArray children)
        {
            node.Children.AddRange(children.Select(child => FromJson(child!.AsObject())));
        }
        return node;
    }
}

/// <summary>One object a canvas state shows (the reference's "elements"): tool, frame in points, rotation, lock and selection.</summary>
internal sealed record CanvasElement(string Tool, double[] Frame, double Rotation, bool Locked, bool Selected, bool Draft)
{
    public static CanvasElement FromJson(JsonObject json) => new(
        json["tool"]!.GetValue<string>(),
        [.. json["frame"]!.AsArray().Select(item => item!.GetValue<double>())],
        json["rotation"]?.GetValue<double>() ?? 0,
        json["locked"]?.GetValue<bool>() ?? false,
        json["selected"]?.GetValue<bool>() ?? false,
        json["draft"]?.GetValue<bool>() ?? false);

    public string Describe() =>
        $"{Tool} {Comparer.Frame(Frame)}{(Rotation != 0 ? $" rotated {Rotation:0.###}" : string.Empty)}{(Locked ? " locked" : string.Empty)}" +
        $"{(Selected ? " selected" : string.Empty)}{(Draft ? " (being drawn)" : string.Empty)}";
}

/// <summary>
/// A state of reference/ux-reference/macos: its size in points, notes, image, accessibility tree and, for the canvas, the
/// objects it shows. <see cref="DerivedAccessibility"/> names where an expected tree came from when the reference has none.
/// </summary>
internal sealed record ReferenceState(string Surface, string State, string Theme, double Width, double Height, string Notes,
                                      string ImagePath, IReadOnlyList<AxNode>? Accessibility)
{
    public string Name => $"{Surface}/{State}";

    public IReadOnlyList<CanvasElement>? Elements { get; init; }

    public string? DerivedAccessibility { get; init; }

    /// <summary>Expected words that come from the Mac source instead of the recorded tree, and why; reported with the result.</summary>
    public string? SourceNote { get; init; }

    /// <summary>A few words about <see cref="SourceNote"/> for the summary table.</summary>
    public string? SourceSummary { get; init; }

    public static ReferenceState Load(string root, string surface, string state, string theme)
    {
        string json = Path.Combine(root, surface, $"{state}-{theme}.json");
        JsonObject data = JsonNode.Parse(File.ReadAllText(json))!.AsObject();
        JsonArray size = data["size"]!.AsArray();
        List<AxNode>? nodes = data["accessibility"] is JsonArray tree ? [.. tree.Select(node => AxNode.FromJson(node!.AsObject()))] : null;
        var reference = new ReferenceState(surface, state, theme, size[0]!.GetValue<double>(), size[1]!.GetValue<double>(),
            data["notes"]?.GetValue<string>() ?? string.Empty, Path.Combine(root, surface, $"{state}-{theme}.png"), nodes)
        {
            Elements = data["elements"] is JsonArray elements ? [.. elements.Select(item => CanvasElement.FromJson(item!.AsObject()))] : null,
        };
        if (ShortcutExpectations.Applies(surface, state))
        {
            return ShortcutExpectations.Apply(reference);
        }
        if (surface != "canvas")
        {
            return reference;
        }
        return nodes is null ? CanvasExpectations.WithAccessibility(reference) : CanvasExpectations.WithSourceLabels(reference);
    }

    /// <summary>Every state of a surface folder, from its light or dark JSON files (the break screen is always dark).</summary>
    /// <remarks>A surface the Mac has not captured yet, such as a new one before the reference is regenerated, has no states.</remarks>
    public static IEnumerable<string> States(string root, string surface) =>
        !Directory.Exists(Path.Combine(root, surface)) ? [] : Directory.EnumerateFiles(Path.Combine(root, surface), "*.json")
            .Select(path => Path.GetFileName(path))
            .Select(file => file.EndsWith("-light.json", StringComparison.Ordinal) ? file[..^"-light.json".Length]
                : file.EndsWith("-dark.json", StringComparison.Ordinal) ? file[..^"-dark.json".Length] : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// Whether the Mac captured <paramref name="state"/> in <paramref name="theme"/>, or it is a Windows-only state; the
    /// break screen has only dark captures.
    /// </summary>
    public static bool Captured(string root, string surface, string state, string theme) =>
        File.Exists(Path.Combine(root, surface, $"{state}-{theme}.json")) || WindowsOnlyStates.Contains($"{surface}/{state}");

    /// <summary>
    /// States that only Windows has, so no macOS capture exists: the screen access explanation and Settings &gt; Permissions
    /// while Windows has not allowed screen access (WIN-CAP-04), and the stroke color popover while a shape label is edited
    /// (its Match outline), which the Mac reference does not capture yet. The tool records their trees and pictures for review.
    /// </summary>
    public static IReadOnlyList<string> WindowsOnlyStates { get; } =
        ["dialogs/screen-access", "dialogs/screen-access-asking", "pickers/color-stroke-label", "pickers/color-stroke-label-following",
         "settings/tab-permissions-not-asked", "settings/tab-permissions-refused"];

    /// <summary>True for a state without a macOS capture (<see cref="WindowsOnlyStates"/>).</summary>
    public bool WindowsOnly { get; init; }

    /// <summary>The macOS capture of a state, or a Windows-only state without one.</summary>
    public static ReferenceState LoadOrWindowsOnly(string root, string surface, string state, string theme) =>
        File.Exists(Path.Combine(root, surface, $"{state}-{theme}.json")) || !WindowsOnlyStates.Contains($"{surface}/{state}")
            ? Load(root, surface, state, theme)
            : new ReferenceState(surface, state, theme, 0, 0, "Windows only, no macOS capture", string.Empty, null) { WindowsOnly = true };
}

/// <summary>
/// The platform terms of specification 013 applied to the Mac's words before an exact comparison: key symbols become
/// Windows keycap text (Command and Control become Ctrl, Option becomes Alt), and versions compare by shape because
/// Windows uses its own version.
/// </summary>
internal static partial class PlatformTerms
{
    /// <summary>
    /// The Mac's Settings sentences that Windows words with its own terms (Annotator.Workspace's SettingsCopy and the launch
    /// at sign-in messages, specification 013), compared whole. "Mac" is the display symbol's spoken name.
    /// </summary>
    private static readonly Dictionary<string, string> Sentences = new(StringComparer.Ordinal)
    {
        ["Launch at login"] = SettingsCopy.LaunchTitle,
        ["Start Annotator for this Mac account without opening the drawing toolbar."] = SettingsCopy.LaunchHelp,
        ["Open Login Items..."] = SettingsCopy.OpenStartupApps,
        ["Managed by macOS for this account. Not included in exported profiles."] = SettingsCopy.LaunchManaged,
        ["Annotator will start quietly when you log in. Use the menu-bar icon or your drawing shortcut."] = LaunchAtSignInState.On.Message,
        ["Approval is needed. Allow Annotator in macOS Login Items to finish enabling it."] = LaunchAtSignInState.DisabledByUser.Message,
        ["macOS could not find a login item for this app. Turn this on to try setting it up."] = LaunchAtSignInState.NotPackaged.Message,
        ["Show menu-bar icon"] = SettingsCopy.ShowTrayIcon,
        ["If the menu bar is full, macOS may hide the icon. You can still show the tools with your Show / hide shortcut."] = SettingsCopy.ShowTrayIconHelp,
        ["Choose how drawings and tools hide and return. Off turns animation off. When macOS Reduce Motion is on, a short fade replaces any movement."] =
            SettingsCopy.WorkspaceAnimationHelp,
        ["Mac"] = "Display",
        ["Screen Recording"] = SettingsCopy.CaptureTitle,
        ["Open Screen Recording settings"] = SettingsCopy.OpenCaptureSettings,
        ["Allowed. Start zoom to check."] = SettingsCopy.CaptureAllowed,
        ["Accessibility"] = SettingsCopy.InputTitle,
        ["For Option-scroll over other apps. Zoom buttons don't need this."] = SettingsCopy.InputDetail,
        ["Allowed"] = SettingsCopy.InputStatus,
        // The break timer (specification 014): "Mac" becomes "computer", and Control-Command-Q becomes Win+L.
        ["This Mac locks if anyone touches it."] = BreakCopy.LocksWhenTouched,
        ["Your Mac locks if someone touches it."] = BreakCopy.MenuCaption(locks: true),
        ["macOS didn't confirm the lock, so nothing is locked. Press Control-Command-Q to lock your Mac. The lock screen hides the timer."] =
            BreakCopy.LockNotConfirmed,
        ["macOS doesn't let apps show anything on the lock screen. So the timer stays on screen, and from 3 seconds after it starts, your Mac locks as soon as someone presses a key, clicks or moves the mouse. Until then, your Mac stays signed in behind the timer. While your Mac is locked, the timer keeps counting and plays its sound at zero. It comes back when you sign in. If Annotator quits, the timer goes away."] =
            BreakCopy.LockingExplanation,
        ["Annotator can't lock this Mac. Turn this off to use the timer without locking."] = BreakCopy.CannotLock,
        // An update found while the person was busy (WIN-SYS-03, DV-11): Sparkle installs it on the Mac; a portable Windows
        // copy says where to download it.
        ["Annotator 0.1.0-beta.13 is ready to install."] =
            Annotator.Workspace.Updates.UpdateText.PortableAvailable("0.1.0-beta.13"),
    };

    /// <summary>
    /// Share usage counts in Settings and the tutorial (WIN-PRV-01): the unavailable words this build shows, or with
    /// <c>--usage-counts available</c> the Mac's words with Windows for macOS, as Windows shows them once it may send.
    /// </summary>
    private static readonly Dictionary<string, (string Unavailable, string Available)> UsageCounts = new(StringComparer.Ordinal)
    {
        ["Usage counts are off."] = (SettingsCopy.UsageCountsUnavailable, Annotator.Workspace.Metrics.UsageMetricsText.Off),
        ["Optional. Never your drawings or text."] = (TutorialText.UsageCountsUnavailableNote, TutorialText.UsageCountsNote),
        ["Optional daily counts: version, macOS, chip and feature categories. No drawings, screenshots, text or file names. Change this in General."] =
            (TutorialText.UsageCountsUnavailableHelp, TutorialText.UsageCountsHelp),
    };

    /// <summary>The app shows usage counts as available (<c>--usage-counts available</c>).</summary>
    public static bool UsageCountsAvailable { get; set; }

    public static string? Windows(string? mac)
    {
        if (mac is null)
        {
            return null;
        }
        if (UsageCounts.TryGetValue(mac, out (string Unavailable, string Available) usage))
        {
            return UsageCountsAvailable ? usage.Available : usage.Unavailable;
        }
        if (Sentences.TryGetValue(mac, out string? sentence))
        {
            return sentence;
        }
        string text = KeySymbols().Replace(mac, match =>
        {
            string modifiers = match.Groups["modifiers"].Value;
            var parts = new List<string>();
            if (modifiers.Contains('⌃') || modifiers.Contains('⌘'))
            {
                parts.Add("Ctrl");
            }
            if (modifiers.Contains('⌥'))
            {
                parts.Add("Alt");
            }
            if (modifiers.Contains('⇧'))
            {
                parts.Add("Shift");
            }
            return string.Join('+', parts) + "+" + match.Groups["key"].Value;
        });
        text = NamedKeys().Replace(text, match => "Ctrl+" + match.Groups["key"].Value);
        text = text.Replace("Option-", "Alt+", StringComparison.Ordinal);
        // Spoken shortcuts ("Control + Shift + 2") and the words of the global shortcuts, where Command is the Windows key.
        text = text.Replace("Control + ", "Ctrl + ", StringComparison.Ordinal)
            .Replace("Option + ", "Alt + ", StringComparison.Ordinal)
            .Replace("Command + ", "Win + ", StringComparison.Ordinal)
            .Replace("Control, Option or Command", "Ctrl, Alt or Win", StringComparison.Ordinal);
        // Keys that stay the same: "⌥ + scroll", "⌫" and "add ⇧ for 10 px" name keys on their own.
        text = text.Replace("⌥ + ", "Alt + ", StringComparison.Ordinal)
            .Replace("⌫", "Delete", StringComparison.Ordinal)
            .Replace(" ⇧ ", " Shift ", StringComparison.Ordinal);
        // The shortcut table's "Commit text": the text editor's label names Command-Return in words.
        text = text.Replace("Command Return", "Ctrl+Enter", StringComparison.Ordinal);
        return Version(text);
    }

    public static string? Version(string? text) => text is null ? null : Versions().Replace(text, "Version <version>");

    [GeneratedRegex(@"(?<modifiers>[⌃⌥⇧⌘]+)(?<key>[^\s⌃⌥⇧⌘.])")]
    private static partial Regex KeySymbols();

    [GeneratedRegex(@"\b(?:Control|Command)-(?<key>\S)")]
    private static partial Regex NamedKeys();

    [GeneratedRegex(@"^Version \S+( \(\S+\))?$")]
    private static partial Regex Versions();
}

internal static class Json
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
