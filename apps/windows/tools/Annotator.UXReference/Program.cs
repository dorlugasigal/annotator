using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace Annotator.UXReference;

/// <summary>
/// Compares Annotator's Windows toolbar, tray flyout, canvas and color popover with the macOS UX reference (WIN-PAR-06).
/// <code>
/// dotnet run --project apps/windows/tools/Annotator.UXReference -- run --out &lt;folder&gt; [--app &lt;Annotator.exe&gt;]
///     [--states toolbar/*,menu/*] [--themes light,dark] [--reference reference/ux-reference/macos]
/// </code>
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return 0;
            }
            Options options = Options.Parse(args);
            return args[0] switch
            {
                "run" => Run(options),
                "list" => List(options),
                "cursors" => CompareCursors(options),
                _ => throw new ArgumentException($"Unknown command \"{args[0]}\".\n{Usage}"),
            };
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
    }

    private const string Usage = """
        Annotator.UXReference: compares the Windows toolbar, tray flyout, canvas, color popover, Tools window, Settings with the Toolbar & Shortcuts, Pointer and Break timer pages, the tour, dialogs, keycaps, the error window, region sharing and the break screen with reference/ux-reference/macos (WIN-PAR-06).

          run   --out <folder>  Shows each state in Annotator.exe (development builds, off-screen, never activated),
                                dumps its UI Automation tree, compares it with the reference and writes, per state and
                                theme: <state>-<theme>.windows.json, .windows.png (2x), .sheet.png and .diff.txt, plus
                                summary.md and summary.json.
          list                  Lists the states that --states selects.
          cursors --out <folder> Compares every cursor of cursors.json (size, hot spot and 2x pixels of the cursors
                                Annotator draws; kind of the system ones) and writes cursors/cursors.sheet.png and .diff.txt.

        Options:
          --app <path>          Annotator.exe; default: the Debug build, else apps/windows/out/app/Annotator.exe.
          --reference <folder>  reference/ux-reference/macos; found from the current folder by default.
          --states <patterns>   Comma-separated surface/state patterns with * (default: toolbar/*,menu/*,canvas/*,properties/*,pickers/*,tools-window/*,settings/*,tour/*,dialogs/*,region-sharing/*,break-timer/*).
          --themes <list>       light,dark (default both).
          --timeout <seconds>   How long Annotator.exe may take to show a state (default 40).
          --usage-counts available
                                Shows Share usage counts in Settings and the tour as available and off (the Mac's
                                states, as Windows shows them once it may send usage counts, WIN-PRV-01) and compares
                                them with the Mac's words; by default they show as unavailable, as this build has them.
          --contrast <theme>    Shows each state as in a Windows contrast theme: aquatic, desert, dusk or nightsky (WIN-A11Y-03,
                                through WinUI's test hooks; no Windows setting changes). Output names end in -<theme>.
          --text-scale <scale>  Shows each state with Windows' Text size at this scale, up to 2.25. Output names end in -text<scale>.
        """;

    private static int CompareCursors(Options options)
    {
        string output = options.Output ?? throw new ArgumentException("cursors needs --out <folder>.");
        foreach (string line in Cursors.Run(options.Reference, output))
        {
            Console.WriteLine(line);
        }
        Console.WriteLine($"Sheet: {Path.Combine(output, "cursors", "cursors.sheet.png")}");
        return 0;
    }

    private static int List(Options options)
    {
        foreach (string state in options.States())
        {
            Console.WriteLine(state);
        }
        return 0;
    }

    private static int Run(Options options)
    {
        string output = options.Output ?? throw new ArgumentException("run needs --out <folder>.");
        Directory.CreateDirectory(output);
        string exe = options.App();
        Console.WriteLine($"Annotator.exe: {exe}");
        Console.WriteLine($"Reference: {options.Reference}");
        var rows = new List<SummaryRow>();
        foreach (string name in options.States())
        {
            string surface = name[..name.IndexOf('/', StringComparison.Ordinal)];
            string state = name[(surface.Length + 1)..];
            foreach (string theme in options.Themes.Where(theme => ReferenceState.Captured(options.Reference, surface, state, theme)))
            {
                rows.Add(RunState(options, exe, surface, state, theme, Path.Combine(output, surface)));
                Console.WriteLine(rows[^1].Line);
            }
        }
        WriteSummary(output, rows, exe, options);
        Console.WriteLine($"Summary: {Path.Combine(output, "summary.md")}");
        return 0;
    }

    private static SummaryRow RunState(Options options, string exe, string surface, string state, string theme, string folder)
    {
        Directory.CreateDirectory(folder);
        string stem = Path.Combine(folder, $"{state}-{theme}{options.Suffix}");
        ReferenceState reference = ReferenceState.LoadOrWindowsOnly(options.Reference, surface, state, theme);
        BitmapSource? macos = reference.WindowsOnly ? null : Sheets.Load(reference.ImagePath);
        // Each state has its own scratch folder: an app that is still quitting from the state before never touches this one.
        string scratch = Path.Combine(folder, $".app-{state}-{theme}");
        try
        {
            using AppSession session = AppSession.Start(exe, $"{surface}/{state}", theme, scratch, options.Timeout, options.UsageCountsAvailable,
                                                        options.Simulation);
            AppReport report = session.Report;
            if (report.Status != "shown")
            {
                Sheets.Save(Sheets.Sheet(macos, null, $"{reference.Name} ({theme}): not shown on Windows", string.Empty), stem + ".sheet.png");
                File.WriteAllText(stem + ".diff.txt", $"{reference.Name} ({theme}): {report.Status}: {report.Reason}\n");
                return new SummaryRow(reference, report.Status == "unavailable" ? "not built" : "failed", report.Reason ?? string.Empty, null);
            }
            List<AxNode> nodes = report.Window != 0 ? UiaDump.Read(report.Window, report.Scale, includeRoot: report.Canvas) : [];
            string? scrolled = null;
            if (report.Window != 0 && ((surface == "settings" && (state.StartsWith("tab-toolbar", StringComparison.Ordinal) || state.StartsWith("tab-pointer", StringComparison.Ordinal)))
                || (surface == "toolbar" && state == "full-properties-vertical-arrow")))
            {
                // The Mac reports the frames of the content below the fold too (ScrolledFrames); the other Settings pages
                // report them themselves (SettingsFrames in the app). The vertical toolbar's full properties scroll on both.
                nodes = ScrolledFrames.Complete(report.Window, report.Scale, nodes, out string scrollNote);
                scrolled = session.Exited is { } exited ? $"{scrollNote}; {exited}" : scrollNote;
            }
            if (report.Scope is { } scope && reference.Accessibility is { } tree)
            {
                // Only that part of the Mac window is the app's (a Settings page): compare its subtree on both sides.
                reference = reference with { Accessibility = Scoped(tree, scope) };
                nodes = Scoped(nodes, scope);
            }
            string layout = Path.Combine(scratch, "layout.txt");
            if (File.Exists(layout))
            {
                File.Copy(layout, stem + ".layout.txt", overwrite: true);
            }
            BitmapSource windows = Sheets.WindowsAt2x(report.ImagePath!, report.ImageWidth, report.ImageHeight, report.Width, report.Height);
            Sheets.Save(windows, stem + ".windows.png");
            string caption = $"Windows at {report.Scale * 100:0}%, scaled to 2x ({UiaDump.Format(report.Width)} x {UiaDump.Format(report.Height)} DIPs)";
            Sheets.Save(Sheets.Sheet(macos, windows,
                $"{reference.Name} ({theme}): {reference.Notes}", caption), stem + ".sheet.png");
            var dump = new JsonObject
            {
                ["state"] = $"{surface}/{state}",
                ["appearance"] = theme,
                ["scale"] = report.Scale,
                ["size"] = new JsonArray(Math.Round(report.Width, 2), Math.Round(report.Height, 2)),
                ["accessibility"] = new JsonArray([.. nodes.Select(node => (JsonNode)node.ToJson())]),
            };
            if (report.Elements is { } shown)
            {
                dump["elements"] = shown.DeepClone();
            }
            File.WriteAllText(stem + ".windows.json", dump.ToJsonString(Json.Indented));
            if (reference.WindowsOnly)
            {
                File.WriteAllText(stem + ".diff.txt", $"{reference.Name} ({theme}): Windows only, no macOS capture; review the sheet and windows.json.\n");
                return new SummaryRow(reference, "windows only", "No macOS capture; review the sheet and windows.json.", null);
            }
            if (reference.Accessibility is null)
            {
                File.WriteAllText(stem + ".diff.txt", $"{reference.Name} ({theme}): image only; compare the sheet.\n");
                return new SummaryRow(reference, "image only", "No accessibility tree in the reference; compare the sheet.", null);
            }
            Comparison comparison = Comparer.Compare(reference, nodes, report.Width, report.Height);
            string diff = Comparer.Report(reference, comparison);
            string note = string.Empty;
            if (reference.DerivedAccessibility is { } derived)
            {
                diff = $"Expected accessibility from {derived}.\n" + diff;
                note = "accessibility from the Mac source";
            }
            if (scrolled is not null)
            {
                diff = $"Frames below the fold: {scrolled}.\n" + diff;
            }
            if (reference.SourceNote is { } source)
            {
                diff = source + "\n" + diff;
                note = reference.SourceSummary ?? "Use apps label from the Mac source";
            }
            if (reference.Elements is not null)
            {
                List<string> objects = CanvasExpectations.CompareElements(reference.Elements, report.Elements);
                diff += CanvasExpectations.Report(reference.Elements, report.Elements, objects);
                note = $"{note}; objects: {(objects.Count == 0 ? "match" : $"{objects.Count} differ ({objects[0]})")}";
                if (objects.Count > 0)
                {
                    File.WriteAllText(stem + ".diff.txt", diff);
                    return new SummaryRow(reference, "differs", note, comparison);
                }
            }
            File.WriteAllText(stem + ".diff.txt", diff);
            return new SummaryRow(reference, comparison.Matches ? "match" : comparison.MatchesButTextWidths ? "match (text widths)" : "differs", note, comparison);
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or IOException or System.Windows.Automation.ElementNotAvailableException or InvalidDataException)
        {
            File.WriteAllText(stem + ".diff.txt", $"{reference.Name} ({theme}): failed: {error}\n");
            return new SummaryRow(reference, "failed", error.Message, null);
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    /// <summary>The first node with <paramref name="role"/>, depth first, as a one-node list; empty when there is none.</summary>
    private static List<AxNode> Scoped(IEnumerable<AxNode> nodes, string role)
    {
        foreach (AxNode node in nodes)
        {
            if (node.Role == role)
            {
                return [node];
            }
            if (Scoped(node.Children, role) is [_, ..] found)
            {
                return found;
            }
        }
        return [];
    }

    private static void WriteSummary(string output, List<SummaryRow> rows, string exe, Options options)
    {
        var text = new StringBuilder();
        text.AppendLine("# UX reference comparison");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Run: {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Annotator.exe: `{exe}` ({File.GetLastWriteTime(exe):yyyy-MM-dd HH:mm})");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Reference: `{options.Reference}`, manifest source commit {ManifestCommit(options.Reference)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Tolerance: frames within {Comparer.Tolerance} DIP; words exact after the platform terms of specification 013");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Share usage counts: {(options.UsageCountsAvailable ? "available (--usage-counts available)" : "unavailable, as this build has it")}");
        text.AppendLine();
        text.AppendLine("| State | Theme | Result | Match | Text width | Differ | Missing | Extra | Note |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (SummaryRow row in rows)
        {
            Comparison? c = row.Comparison;
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {row.Reference.Name} | {row.Reference.Theme} | {row.Result} | {c?.Count(NodeOutcome.Match)} | {c?.Count(NodeOutcome.TextWidth)} | {c?.Count(NodeOutcome.Differs)} | " +
                $"{c?.Count(NodeOutcome.Missing)} | {c?.Count(NodeOutcome.Extra)} | {(c?.SurfaceDifferences.FirstOrDefault() ?? row.Note).Replace("|", "/", StringComparison.Ordinal)} |");
        }
        File.WriteAllText(Path.Combine(output, "summary.md"), text.ToString());
        var json = new JsonArray([.. rows.Select(row => (JsonNode)new JsonObject
        {
            ["state"] = row.Reference.Name,
            ["theme"] = row.Reference.Theme,
            ["result"] = row.Result,
            ["note"] = row.Note,
            ["match"] = row.Comparison?.Count(NodeOutcome.Match),
            ["textWidth"] = row.Comparison?.Count(NodeOutcome.TextWidth),
            ["differ"] = row.Comparison?.Count(NodeOutcome.Differs),
            ["missing"] = row.Comparison?.Count(NodeOutcome.Missing),
            ["extra"] = row.Comparison?.Count(NodeOutcome.Extra),
            ["surface"] = row.Comparison is { } comparison ? new JsonArray([.. comparison.SurfaceDifferences.Select(item => (JsonNode)item)]) : null,
        })]);
        File.WriteAllText(Path.Combine(output, "summary.json"), json.ToJsonString(Json.Indented));
    }

    private static string ManifestCommit(string reference)
    {
        string manifest = Path.Combine(reference, "manifest.json");
        if (!File.Exists(manifest))
        {
            return "unknown";
        }
        JsonNode? data = JsonNode.Parse(File.ReadAllText(manifest));
        return data?["source"]?["commit"]?.GetValue<string>() ?? data?["commit"]?.GetValue<string>() ?? "unknown";
    }

    private sealed record SummaryRow(ReferenceState Reference, string Result, string Note, Comparison? Comparison)
    {
        public string Line => Comparison is { } c
            ? $"{Reference.Name} {Reference.Theme}: {Result} ({c.Count(NodeOutcome.Match)} match, {c.Count(NodeOutcome.TextWidth)} text width, {c.Count(NodeOutcome.Differs)} differ, " +
              $"{c.Count(NodeOutcome.Missing)} missing, {c.Count(NodeOutcome.Extra)} extra){(c.SurfaceDifferences.Count > 0 ? "; " + c.SurfaceDifferences[0] : string.Empty)}" +
              (Note.Length > 0 ? $"; {Note}" : string.Empty)
            : $"{Reference.Name} {Reference.Theme}: {Result} {Note}";
    }

    private sealed class Options
    {
        public string? Output { get; private set; }

        public string? AppPath { get; private set; }

        public string Reference { get; private set; } = string.Empty;

        public IReadOnlyList<string> Patterns { get; private set; } = ["toolbar/*", "menu/*", "canvas/*", "properties/*", "pickers/*", "tools-window/*", "settings/*", "tour/*", "dialogs/*", "region-sharing/*", "break-timer/*"];

        public IReadOnlyList<string> Themes { get; private set; } = ["light", "dark"];

        public TimeSpan Timeout { get; private set; } = TimeSpan.FromSeconds(40);

        /// <summary>Share usage counts shows as available (WIN-PRV-01, <c>--usage-counts available</c>).</summary>
        public bool UsageCountsAvailable { get; private set; }

        /// <summary>--contrast and --text-scale, passed to the app (WIN-A11Y-03 simulations).</summary>
        public List<string> Simulation { get; } = [];

        /// <summary>The file name suffix of a simulation, such as "-nightsky" or "-text2.25".</summary>
        public string Suffix => string.Concat(Simulation.Where((_, index) => index % 2 == 1)
            .Select(value => "-" + (value.Contains('.', StringComparison.Ordinal) || char.IsDigit(value[0]) ? "text" + value : value)));

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (int index = 1; index < args.Length; index++)
            {
                string Next() => index + 1 < args.Length ? args[++index] : throw new ArgumentException($"{args[index]} needs a value.");
                switch (args[index])
                {
                    case "--out": options.Output = Path.GetFullPath(Next()); break;
                    case "--app": options.AppPath = Path.GetFullPath(Next()); break;
                    case "--reference": options.Reference = Path.GetFullPath(Next()); break;
                    case "--states": options.Patterns = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                    case "--themes": options.Themes = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                    case "--timeout": options.Timeout = TimeSpan.FromSeconds(double.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--usage-counts":
                        options.UsageCountsAvailable = Next() switch
                        {
                            "available" => true,
                            "unavailable" => false,
                            _ => throw new ArgumentException("--usage-counts takes available or unavailable."),
                        };
                        break;
                    case "--contrast": options.Simulation.AddRange(["--contrast", Next()]); break;
                    case "--text-scale": options.Simulation.AddRange(["--text-scale", Next()]); break;
                    default: throw new ArgumentException($"Unknown option \"{args[index]}\".\n{Usage}");
                }
            }
            PlatformTerms.UsageCountsAvailable = options.UsageCountsAvailable;
            if (options.Themes.Any(theme => theme is not ("light" or "dark")))
            {
                throw new ArgumentException("--themes takes light, dark or both.");
            }
            options.Reference = options.Reference.Length > 0 ? options.Reference : FindReference();
            return options;
        }

        public IEnumerable<string> States()
        {
            foreach (string surface in new[] { "toolbar", "menu", "canvas", "properties", "pickers", "tools-window", "settings", "tour", "dialogs", "region-sharing", "break-timer" })
            {
                foreach (string state in ReferenceState.States(Reference, surface)
                             .Concat(ReferenceState.WindowsOnlyStates.Where(name => name.StartsWith(surface + "/", StringComparison.Ordinal))
                                 .Select(name => name[(surface.Length + 1)..])))
                {
                    string name = $"{surface}/{state}";
                    if (Patterns.Any(pattern => Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$")))
                    {
                        yield return name;
                    }
                }
            }
        }

        public string App()
        {
            string root = Path.GetFullPath(Path.Combine(Reference, "..", "..", ".."));
            string[] candidates = AppPath is { } path ? [path] :
            [
                Path.Combine(root, "apps", "windows", "src", "Annotator.App", "bin", "x64", "Debug", "net10.0-windows10.0.26100.0", "win-x64", "Annotator.exe"),
                Path.Combine(root, "apps", "windows", "out", "app", "Annotator.exe"),
            ];
            return candidates.FirstOrDefault(File.Exists)
                ?? throw new ArgumentException($"Annotator.exe was not found ({string.Join(", ", candidates)}). Build it or pass --app.");
        }

        private static string FindReference()
        {
            for (string? folder = Environment.CurrentDirectory; folder is not null; folder = Path.GetDirectoryName(folder))
            {
                string candidate = Path.Combine(folder, "reference", "ux-reference", "macos");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
            throw new ArgumentException("reference/ux-reference/macos was not found from the current folder. Pass --reference.");
        }
    }
}
