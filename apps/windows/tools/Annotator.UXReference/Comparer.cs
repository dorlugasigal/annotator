using System.Globalization;
using System.Text;

namespace Annotator.UXReference;

internal enum NodeOutcome
{
    /// <summary>Same role, label, identifier, value and help, and the frame within the tolerance.</summary>
    Match,

    /// <summary>The same node with different words or a frame beyond the tolerance.</summary>
    Differs,

    /// <summary>In the reference only.</summary>
    Missing,

    /// <summary>On Windows only.</summary>
    Extra,

    /// <summary>
    /// A static text or heading whose only difference is its width (and the start of centered or trailing text): Windows
    /// draws app text in Segoe UI where the Mac uses SF Pro or Avenir Next (D-4, DV-7).
    /// </summary>
    TextWidth,
}

internal sealed record NodeResult(NodeOutcome Outcome, AxNode? Reference, AxNode? Windows, IReadOnlyList<string> Differences);

/// <summary>Results of one state and theme.</summary>
internal sealed record Comparison(IReadOnlyList<NodeResult> Nodes, IReadOnlyList<string> SurfaceDifferences)
{
    public int Count(NodeOutcome outcome) => Nodes.Count(node => node.Outcome == outcome);

    public bool Matches => SurfaceDifferences.Count == 0 && Nodes.All(node => node.Outcome == NodeOutcome.Match);

    /// <summary>Everything matches apart from text widths that come from the fonts (D-4).</summary>
    public bool MatchesButTextWidths => SurfaceDifferences.Count == 0 && Nodes.All(node => node.Outcome is NodeOutcome.Match or NodeOutcome.TextWidth);
}

/// <summary>
/// Compares a Windows dump with the reference: nodes in tree order are aligned by role and words (longest common
/// subsequence, so order matters), then labels, identifiers, values and help must be equal after the platform terms,
/// and frames must be within <see cref="Tolerance"/> DIPs.
/// </summary>
internal static class Comparer
{
    public const double Tolerance = 1;

    public static Comparison Compare(ReferenceState reference, IReadOnlyList<AxNode> windows, double width, double height)
    {
        var surface = new List<string>();
        if (Math.Abs(reference.Width - width) > Tolerance || Math.Abs(reference.Height - height) > Tolerance)
        {
            surface.Add($"size {F(width)} x {F(height)} DIPs; reference {F(reference.Width)} x {F(reference.Height)} points");
        }
        List<(AxNode Node, int Depth)> expected = Flatten(reference.Accessibility ?? [], 0, normalize: true);
        List<(AxNode Node, int Depth)> actual = Flatten(windows, 0, normalize: false);
        var results = new List<NodeResult>();
        int[,] lengths = new int[expected.Count + 1, actual.Count + 1];
        for (int i = expected.Count - 1; i >= 0; i--)
        {
            for (int j = actual.Count - 1; j >= 0; j--)
            {
                lengths[i, j] = Aligns(expected[i], actual[j]) ? lengths[i + 1, j + 1] + 1 : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }
        int a = 0, b = 0;
        while (a < expected.Count || b < actual.Count)
        {
            if (a < expected.Count && b < actual.Count && Aligns(expected[a], actual[b]))
            {
                List<string> differences = Differences(expected[a].Node, actual[b].Node);
                NodeOutcome outcome = differences.Count == 0 ? NodeOutcome.Match
                    : TextWidthOnly(expected[a].Node, actual[b].Node, differences) ? NodeOutcome.TextWidth : NodeOutcome.Differs;
                results.Add(new NodeResult(outcome, expected[a].Node, actual[b].Node, differences));
                a++;
                b++;
            }
            else if (b < actual.Count && (a == expected.Count || lengths[a, b + 1] >= lengths[a + 1, b]))
            {
                results.Add(new NodeResult(NodeOutcome.Extra, null, actual[b].Node, []));
                b++;
            }
            else
            {
                results.Add(new NodeResult(NodeOutcome.Missing, expected[a].Node, null, []));
                a++;
            }
        }
        return new Comparison(results, surface);
    }

    public static string Report(ReferenceState reference, Comparison comparison)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{reference.Name} ({reference.Theme}): {comparison.Count(NodeOutcome.Match)} match, " +
            $"{comparison.Count(NodeOutcome.TextWidth)} text width only, {comparison.Count(NodeOutcome.Differs)} differ, " +
            $"{comparison.Count(NodeOutcome.Missing)} missing, {comparison.Count(NodeOutcome.Extra)} extra");
        foreach (string difference in comparison.SurfaceDifferences)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  ! surface {difference}");
        }
        foreach (NodeResult node in comparison.Nodes)
        {
            switch (node.Outcome)
            {
                case NodeOutcome.Match:
                    text.AppendLine(CultureInfo.InvariantCulture, $"  = {node.Reference!.Describe()} {Frame(node.Windows!.Frame)}{(node.Windows.Enabled ? string.Empty : " (disabled on Windows)")}");
                    break;
                case NodeOutcome.Differs:
                    text.AppendLine(CultureInfo.InvariantCulture, $"  ~ {node.Reference!.Describe()}: {string.Join("; ", node.Differences)}");
                    break;
                case NodeOutcome.TextWidth:
                    text.AppendLine(CultureInfo.InvariantCulture, $"  f {node.Reference!.Describe()}: text width (font, D-4): {string.Join("; ", node.Differences)}");
                    break;
                case NodeOutcome.Missing:
                    text.AppendLine(CultureInfo.InvariantCulture, $"  - {node.Reference!.Describe()} {Frame(node.Reference.Frame)} (missing on Windows)");
                    break;
                case NodeOutcome.Extra:
                    string extra = $"  + {node.Windows!.Describe()} {Frame(node.Windows.Frame)} (Windows only)" +
                        (node.Windows.Value is not null && node.Windows.Role != "AXStaticText" ? $" value \"{node.Windows.ValueText}\"" : string.Empty) +
                        (node.Windows.Help is { } help ? $" help \"{help}\"" : string.Empty);
                    text.AppendLine(extra);
                    break;
            }
        }
        return text.ToString();
    }

    private static bool Aligns((AxNode Node, int Depth) expected, (AxNode Node, int Depth) actual) => expected.Node.Key == actual.Node.Key;

    /// <summary>Only the frame of a static text or heading differs, its height and vertical position match, and it is narrower or wider.</summary>
    private static bool TextWidthOnly(AxNode expected, AxNode actual, List<string> differences) =>
        expected.Role is "AXStaticText" or "AXHeading" && differences.Count == 1 && differences[0].StartsWith("frame ", StringComparison.Ordinal) &&
        expected.Frame is { } want && actual.Frame is { } have &&
        Math.Abs(have[1] - want[1]) <= Tolerance && Math.Abs(have[3] - want[3]) <= Tolerance &&
        Math.Abs(have[2] - want[2]) > Tolerance;

    private static List<string> Differences(AxNode expected, AxNode actual)
    {
        var differences = new List<string>();
        Words("label", expected.Label, actual.Label, differences);
        Words("identifier", expected.Identifier, actual.Identifier, differences);
        Words("help", expected.Help, actual.Help, differences);
        if (expected.Role != "AXStaticText" || expected.Label is not null)
        {
            if (!SameValue(expected, actual))
            {
                differences.Add($"value \"{actual.ValueText}\", reference \"{expected.ValueText}\"");
            }
        }
        if (expected.Frame is { } want && actual.Frame is { } have)
        {
            double[] delta = [have[0] - want[0], have[1] - want[1], have[2] - want[2], have[3] - want[3]];
            if (delta.Any(value => Math.Abs(value) > Tolerance))
            {
                differences.Add($"frame {Frame(have)}, reference {Frame(want)} (Δ {string.Join(", ", delta.Select(F))})");
            }
        }
        else if (expected.Role == "AXTab" && expected.Frame is null)
        {
            // The reference has no frame for a tab; there is nothing to compare.
        }
        else if (expected.Frame is not null || actual.Frame is not null)
        {
            differences.Add("frame missing on one side");
        }
        return differences;
    }

    private static void Words(string name, string? expected, string? actual, List<string> differences)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            differences.Add($"{name} \"{actual}\", reference \"{expected}\"");
        }
    }

    private static bool SameValue(AxNode expected, AxNode actual)
    {
        if (expected.Value is null || actual.Value is null)
        {
            return expected.Value is null && actual.Value is null;
        }
        if (expected.Value.TryGetValue(out double want) && actual.Value.TryGetValue(out double have))
        {
            return Math.Abs(want - have) < 1e-6;
        }
        return string.Equals(expected.ValueText, actual.ValueText, StringComparison.Ordinal);
    }

    private static List<(AxNode Node, int Depth)> Flatten(IReadOnlyList<AxNode> nodes, int depth, bool normalize)
    {
        var list = new List<(AxNode, int)>();
        foreach (AxNode node in nodes)
        {
            AxNode shown = normalize ? Normalize(node) : WindowsVersion(node);
            list.Add((shown, depth));
            list.AddRange(Flatten(node.Children, depth + 1, normalize));
        }
        if (normalize && depth == 0)
        {
            Title(list);
        }
        return list;
    }

    /// <summary>
    /// The Mac records a Form's switch, pop-up or segmented picker without a label: AppKit names it by the text before it (its
    /// title element), which UI Automation puts in the control's name. A menu button without a label takes the title its Menu
    /// has in the Mac source, found by its help; a control the Mac source labels takes that label (<see cref="SourceLabels"/>).
    /// </summary>
    private static void Title(List<(AxNode Node, int Depth)> list)
    {
        for (int index = 0; index < list.Count; index++)
        {
            AxNode node = list[index].Node;
            string? label = node switch
            {
                { Label: null, Identifier: { } identifier } when SourceLabels.TryGetValue(identifier, out string? source) => source,
                { Label: null, Role: "AXCheckBox" or "AXPopUpButton" or "AXRadioGroup" } when index > 0 && list[index - 1].Node is { Role: "AXStaticText" } title => title.ValueText,
                { Label: null, Role: "AXMenuButton", Help: { } help } => MenuTitles.GetValueOrDefault(help),
                _ => null,
            };
            if (label is not null)
            {
                list[index] = (new AxNode
                {
                    Role = node.Role,
                    Label = label,
                    Identifier = node.Identifier,
                    Value = node.Value,
                    Help = node.Help,
                    Frame = node.Frame,
                }, list[index].Depth);
            }
        }
    }

    /// <summary>Menu titles from the Mac source (CustomizationView's Profiles, SettingsView's Support info), by their help.</summary>
    private static readonly Dictionary<string, string> MenuTitles = new(StringComparer.Ordinal)
    {
        ["Profiles save settings and shortcuts. Import replaces these, not boards."] = "Profiles",
        ["App and system details, not drawings or screenshots. Review before sharing."] = "Support info",
    };

    /// <summary>
    /// Labels the Mac source gives that the recorder writes without, by identifier: CoverPropertiesView's NativeEffectPicker
    /// calls setAccessibilityLabel("Effect") on its NSPopUpButton, which its cell's accessibility element does not report.
    /// </summary>
    private static readonly Dictionary<string, string> SourceLabels = new(StringComparer.Ordinal)
    {
        ["cover-mode"] = "Effect",
        // BreakTimerSettingsView: TextField("Title", text:, prompt:), whose title AppKit shows as the row's label.
        ["break-title"] = "Title",
    };

    private static AxNode Normalize(AxNode node) => new()
    {
        // UI Automation has one Edit control type for one-line and multi-line text, so the Mac's text area reads as a field.
        // The Mac's recorder keeps a Settings tab's title and identifier with no role or frame: the Windows tab item.
        Role = node.Role == "AXTextArea" ? "AXTextField"
            : node.Role == "AXUnknown" && node.Frame is null && node.Label is not null && node.Identifier is not null ? "AXTab"
            : node.Role,
        Label = PlatformTerms.Windows(node.Label),
        Identifier = node.Identifier,
        Value = node.Value is { } value && !value.TryGetValue(out double _) ? System.Text.Json.Nodes.JsonValue.Create(PlatformTerms.Windows(node.ValueText)) : node.Value,
        Help = PlatformTerms.Windows(node.Help),
        Frame = node.Frame,
    };

    private static AxNode WindowsVersion(AxNode node) => new()
    {
        Role = node.Role,
        Label = PlatformTerms.Version(node.Label),
        Identifier = node.Identifier,
        Value = node.Value is { } value && !value.TryGetValue(out double _) ? System.Text.Json.Nodes.JsonValue.Create(PlatformTerms.Version(node.ValueText)) : node.Value,
        Help = node.Help,
        Frame = node.Frame,
        Enabled = node.Enabled,
    };

    public static string Frame(double[]? frame) => frame is null ? "[no frame]" : $"[{string.Join(", ", frame.Select(F))}]";

    public static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
