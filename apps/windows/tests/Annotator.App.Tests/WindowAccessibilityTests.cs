using System.Text.Json.Nodes;

namespace Annotator.App.Tests;

/// <summary>
/// UI Automation and keyboard checks of every app window (WIN-A11Y-01 to WIN-A11Y-03). Each state starts the development build
/// of Annotator.exe in its UX reference mode, off screen and never activated, so nothing reaches the person's apps. The
/// checks are explicit because they need a desktop session that renders; each one goes through every state (about a second
/// a state) and lists every problem it finds:
/// <code>
/// dotnet test --project tests/Annotator.App.Tests -- --explicit only
/// </code>
/// ANNOTATOR_A11Y_STATES limits the states (comma-separated patterns with *), ANNOTATOR_APP picks the Annotator.exe.
/// The labels, values and frames themselves are compared with the Mac's by apps/windows/tools/Annotator.UXReference.
/// </summary>
public sealed class WindowAccessibilityTests(ITestOutputHelper output)
{
    /// <summary>
    /// The Mac's VoiceOver actions, which Windows offers as context menu items with the same names (WIN-A11Y-02): the tiles of
    /// Settings > Toolbar & Shortcuts (groups named after their item).
    /// </summary>
    private static readonly (Func<string, UiaElementKind, bool> Element, string[] Items)[] s_actions =
    [
        ((id, kind) => kind == UiaElementKind.ArrangerTile && id.StartsWith("toolbar-", StringComparison.Ordinal) && id != "toolbar-tool-select",
            ["Move left", "Move right", "Remove from toolbar"]),
        ((id, kind) => kind == UiaElementKind.ArrangerTile && id == "toolbar-tool-select", ["Move left", "Move right"]),
        ((id, kind) => kind == UiaElementKind.ArrangerTile && id.StartsWith("more-", StringComparison.Ordinal), ["Add to toolbar"]),
    ];

    /// <summary>
    /// WIN-A11Y-01 and WIN-A11Y-02: every control that can be used has a name, the keyboard reaches each of them (Tab, or the
    /// arrow keys inside a group), and the Mac's VoiceOver actions are context menu items with the same names.
    /// </summary>
    [Fact(Explicit = true)]
    public void EveryControlHasANameAndTheKeyboardReachesIt() => EveryState([], (state, _, app, problems) =>
    {
        JsonObject report = app.Accessibility;
        Dictionary<string, string[]> menus = report["contextMenus"]!.AsArray()
            .GroupBy(menu => menu!["id"]!.GetValue<string>())
            .ToDictionary(group => group.Key, group => group.First()!["items"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray());
        foreach (UiaElement element in Uia.Read(app.Window))
        {
            if (element.Usable && element.Enabled && element.Name.Trim().Length == 0)
            {
                problems.Add($"no name: {element}");
            }
            UiaElementKind kind = state.StartsWith("settings/tab-toolbar", StringComparison.Ordinal) && element.Type == "Group"
                && element.Id.Contains("-tool-", StringComparison.Ordinal) | element.Id.Contains("-action-", StringComparison.Ordinal)
                ? UiaElementKind.ArrangerTile : UiaElementKind.Other;
            foreach ((Func<string, UiaElementKind, bool> matches, string[] expected) in s_actions)
            {
                if (!matches(element.Id, kind))
                {
                    continue;
                }
                if (!menus.TryGetValue(element.Id, out string[]? items))
                {
                    problems.Add($"no context menu: {element}");
                    continue;
                }
                problems.AddRange(expected.Where(item => !items.Contains(item)).Select(item => $"{element} has no \"{item}\" menu item"));
            }
        }
        foreach (JsonNode? element in report["unreachable"]!.AsArray())
        {
            problems.Add($"the keyboard cannot reach {Describe(element)}");
        }
        output.WriteLine($"{state}: {report["tabOrder"]!.AsArray().Count} tab stops");
    });

    /// <summary>WIN-UI-07 and WIN-A11Y-03: in a contrast theme every color of the window is the theme's, except parts with their own colors (OwnColors).</summary>
    [Fact(Explicit = true)]
    public void ContrastThemesColorTheWholeWindow() => EveryState(["--contrast", "nightsky"], (_, _, app, problems) =>
    {
        foreach (JsonNode? element in app.Accessibility["offPalette"]!.AsArray())
        {
            problems.Add($"not a contrast color: {Describe(element)}");
        }
        double foreign = ContrastPixels.ForeignShare(app, ContrastPixels.NightSky);
        if (foreign > 0.002)
        {
            problems.Add($"{foreign:P2} of the window's pixels are neither contrast colors nor blends of them at an edge");
        }
    });

    /// <summary>
    /// WIN-A11Y-03: with Windows' largest text (225%) no text is cut off that shows whole at 100%; parts that grow too tall
    /// scroll. The Mac's exact layouts let a few text frames reach past their stacks at 100% too, where the UX comparison
    /// checks them against the Mac, so only what the larger text adds is a problem.
    /// </summary>
    [Fact(Explicit = true)]
    public void TheLargestTextFits() => EveryState(["--text-scale", "2.25"], (state, variant, app, problems) =>
    {
        List<string> larger = Clipped(app);
        if (larger.Count == 0)
        {
            return;
        }
        using ReferenceApp regular = ReferenceApp.Start(state, variant);
        List<string> before = regular.Status == "shown" ? Clipped(regular) : [];
        problems.AddRange(larger.Where(item => !before.Remove(item)).Select(item => $"cut off: {item}"));
    });

    /// <summary>The text the report found cut off, without frames, which differ between text sizes.</summary>
    private static List<string> Clipped(ReferenceApp app) =>
        [.. app.Accessibility["clipped"]!.AsArray().Select(element => Describe(element, frame: false))];

    /// <summary>
    /// Runs <paramref name="check"/> on every state and fails with every problem found, state by state. The states that show
    /// Share usage counts run once more as Windows will show them once it may send usage counts (WIN-PRV-01): the switch and
    /// the tutorial's check box can be used then, so they need their names, keyboard reach, contrast colors and room for large text.
    /// </summary>
    private void EveryState(string[] options, Action<string, string[], ReferenceApp, List<string>> check)
    {
        var failures = new List<string>();
        int states = 0;
        foreach (string state in Repository.States())
        {
            foreach (string[] variant in Variants(state))
            {
                states++;
                string label = variant.Length == 0 ? state : $"{state} ({string.Join(' ', variant)})";
                var problems = new List<string>();
                try
                {
                    using ReferenceApp app = ReferenceApp.Start(state, [.. options, .. variant]);
                    if (app.Status != "shown")
                    {
                        problems.Add($"{app.Status}: {app.Reason}");
                    }
                    else if (app.Window != 0)
                    {
                        // The tray icon's states are a picture without a window; UI Automation reads the icon from the shell.
                        check(state, variant, app, problems);
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or TimeoutException or InvalidDataException
                                              or System.Windows.Automation.ElementNotAvailableException)
                {
                    problems.Add(error.Message);
                }
                if (problems.Count > 0)
                {
                    failures.Add($"{label}:\n  {string.Join("\n  ", problems)}");
                }
            }
        }
        output.WriteLine($"{states} states, {failures.Count} with problems.");
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    /// <summary>The state as this build shows it, and for General and the tutorial also with usage counts available.</summary>
    private static IEnumerable<string[]> Variants(string state)
    {
        yield return [];
        if (state is "settings/tab-general-full" or "tutorial/welcome")
        {
            yield return ["--usage-counts", "available"];
        }
    }

    private static string Describe(JsonNode? element, bool frame = true)
    {
        if (element is null)
        {
            return "nothing";
        }
        string name = element["name"]?.GetValue<string>() ?? string.Empty;
        string id = element["id"]?.GetValue<string>() ?? string.Empty;
        string place = frame && element["frame"] is JsonArray rect ? $" at [{string.Join(", ", rect.Select(value => value!.GetValue<double>()))}]" : string.Empty;
        string reason = element["reason"]?.GetValue<string>() is { } why ? $": {why}" : string.Empty;
        return $"{element["control"]?.GetValue<string>()} {element["type"]?.GetValue<string>()} \"{name}\"{(id.Length > 0 ? $" ({id})" : string.Empty)}{place}{reason}";
    }
}
