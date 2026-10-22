using System.Reflection;
using System.Text;
using Annotator.Core;
using Annotator.Workspace.Settings;
using static Annotator.Workspace.Tests.TestWorkspace;

namespace Annotator.Workspace.Tests.Settings;

/// <summary>
/// The logic behind Help and About: ports of the Mac's IssueReportingTests and SupportInfoTests, the feedback draft, the
/// bundled release notes, the AI skill export and the Windows words of Settings.
/// </summary>
public sealed class SettingsLogicTests
{
    private const string Version = "0.1.0-alpha.2 (10)";

    [Fact]
    public void OnlyConfiguredGitHubNewIssuePagesAreAllowed()
    {
        Uri url = IssueReporting.IssueUrl("https://github.com/example/annotator-releases/issues/new", Version)!;
        Assert.NotNull(url);
        Dictionary<string, string> items = Items(url);
        Assert.Equal(Version, items["version"]);
        Assert.Equal("bug-report.yml", items["template"]);
        Assert.Equal($"[{Version}] ", items["title"]);
        Assert.False(items.ContainsKey("body"), "Never silently attach diagnostics or drawing data.");
        foreach (string? input in new[]
        {
            null, "", "$(ANNOTATOR_ISSUES_URL)", "http://github.com/example/repo/issues/new",
            "https://evil.example/example/repo/issues/new", "https://github.com/example/repo/issues/new?body=private",
            "https://github.com/example/repo/issues", "https://user@github.com/example/repo/issues/new",
            "https://github.com:443/example/repo/issues/new", "https://github.com/../repo/issues/new",
        })
        {
            Assert.Null(IssueReporting.IssueUrl(input, Version));
        }
    }

    [Fact]
    public void FeedbackAndSupportAreExplicitOptInLinks()
    {
        Uri url = IssueReporting.IssueUrl("https://github.com/example/releases/issues/new", Version, feedback: true)!;
        Assert.Contains("feedback.yml", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("body=", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Null(IssueReporting.SupportUrl(null));
        Assert.Null(IssueReporting.SupportUrl("$(ANNOTATOR_SUPPORT_URL)"));
        Assert.Null(IssueReporting.SupportUrl("http://example.com/support"));
        Assert.Null(IssueReporting.SupportUrl("******example.com/support"));
        Assert.Null(IssueReporting.SupportUrl("https://example.com:8443/support"));
        Assert.Equal("example.com", IssueReporting.SupportUrl("https://example.com/support")?.Host);
    }

    [Fact]
    public void ProductPagesHangBelowTheWebsite()
    {
        Assert.Equal("https://dorlugasigal.github.io/annotator/docs/", IssueReporting.ProductPageUrl(null, ProductPage.Help)!.AbsoluteUri);
        Assert.Equal("https://dorlugasigal.github.io/annotator/privacy/", IssueReporting.ProductPageUrl("$(ANNOTATOR_WEBSITE_URL)", ProductPage.Privacy)!.AbsoluteUri);
        Assert.Equal("https://example.com/app/downloads/", IssueReporting.ProductPageUrl("https://example.com/app/", ProductPage.Downloads)!.AbsoluteUri);
        Assert.Equal("https://example.com/ai/", IssueReporting.ProductPageUrl("https://example.com", ProductPage.Ai)!.AbsoluteUri);
        Assert.Null(IssueReporting.ProductPageUrl("http://example.com", ProductPage.Help));
    }

    [Fact]
    public void FeedbackDraftsNeedWordsAndBecomeABrowserDraftOrACopy()
    {
        const string Base = "https://github.com/example/annotator/issues/new";
        var empty = new FeedbackDraft(FeedbackKind.Issue) { Title = " ", Details = "Steps" };
        Assert.Equal(FeedbackFailureKind.Empty, Assert.Throws<FeedbackFailure>(() => empty.Report(Version)).Kind);
        Assert.Equal("Add a short title and describe what happened.", Assert.Throws<FeedbackFailure>(() => empty.GitHubUrl(Base, Version)).Message);

        var draft = new FeedbackDraft(FeedbackKind.Issue) { Title = "Pen & blur", Details = "1. Draw\n2. Blur = 50%" };
        Assert.Equal($"Report a problem\nVersion: {Version}\n\nPen & blur\n\n1. Draw\n2. Blur = 50%\n", draft.Report(Version));
        Dictionary<string, string> items = Items(draft.GitHubUrl(Base, Version));
        Assert.Equal(["template", "version", "title", "steps"], items.Keys);
        Assert.Equal($"[{Version}] Pen & blur", items["title"]);
        Assert.Equal("1. Draw\n2. Blur = 50%", items["steps"]);

        var feedback = new FeedbackDraft(FeedbackKind.Feedback) { Title = "Nice", Details = "Fast" };
        Dictionary<string, string> feedbackItems = Items(feedback.GitHubUrl(Base, Version));
        Assert.Equal("feedback.yml", feedbackItems["template"]);
        Assert.Equal("Fast", feedbackItems["experience"]);
        Assert.Equal("Share feedback", FeedbackKind.Feedback.Title);

        Assert.Equal(FeedbackFailureKind.Unconfigured, Assert.Throws<FeedbackFailure>(() => draft.GitHubUrl(null, Version)).Kind);
        var long_ = draft with { Details = new string('x', FeedbackDraft.MaximumLinkBytes) };
        Assert.Equal(FeedbackFailureKind.TooLongForLink, Assert.Throws<FeedbackFailure>(() => long_.GitHubUrl(Base, Version)).Kind);
        // The copied report keeps every character, however long.
        Assert.Contains(long_.Details, long_.Report(Version), StringComparison.Ordinal);
    }

    [Fact]
    public void BundledReleaseNotesMatchTheRunningVersionAndBuild()
    {
        byte[] catalog = File.ReadAllBytes(RepositoryFile("releases", "notes.json"));
        ReleaseNotesEntry entry = ReleaseNotesCatalog.Current(catalog, "0.1.0-beta.7", 25);
        Assert.Equal("2026-10-13", entry.Date);
        Assert.Equal(["Added", "Changed"], entry.Changes.Select(change => change.Category));
        Assert.NotEmpty(entry.KnownIssues);

        // Windows has no entry yet: a development build says the notes are not available for it.
        ReleaseNotesFailure missing = Assert.Throws<ReleaseNotesFailure>(() => ReleaseNotesCatalog.Current(catalog, "0.0.0-dev", 0));
        Assert.False(missing.IsInvalid);
        Assert.Equal("Release notes are not available for this build. Include the app version when reporting this problem.", missing.Message);
        Assert.False(Assert.Throws<ReleaseNotesFailure>(() => ReleaseNotesCatalog.Load(Path.Combine(AppContext.BaseDirectory, "missing-notes.json"), "0.1.0-beta.7", 25)).IsInvalid);
        Assert.False(Assert.Throws<ReleaseNotesFailure>(() => ReleaseNotesCatalog.Load(RepositoryFile("releases", "notes.json"), "0.1.0-beta.7", null)).IsInvalid);
    }

    [Fact]
    public void WindowsShowsTheWindowsNotesOfAnEntryAndOtherwiseItsOwn()
    {
        // beta.8 is the first entry with Windows notes: the Mac app reads the entry's own changes, this app the Windows ones.
        byte[] catalog = File.ReadAllBytes(RepositoryFile("releases", "notes.json"));
        ReleaseNotesEntry beta8 = ReleaseNotesCatalog.Current(catalog, "0.1.0-beta.8", 26);
        Assert.Equal("2026-10-20", beta8.Date);
        Assert.Equal(["Added"], beta8.Changes.Select(change => change.Category));
        Assert.StartsWith("This is the first Windows version of Annotator.", beta8.Changes[0].Items[0], StringComparison.Ordinal);
        Assert.StartsWith("Annotator is not in the Microsoft Store yet.", beta8.KnownIssues[0], StringComparison.Ordinal);
        Assert.DoesNotContain(beta8.Changes.SelectMany(change => change.Items), item => item.Contains("VoiceOver", StringComparison.Ordinal));

        const string Json = """
            {"schemaVersion":1,"releases":[
              {"version":"2","build":2,"date":"d","changes":{"Fixed":["Mac fix"]},"knownIssues":["Mac limit"],
               "platforms":{"windows":{"changes":{"Removed":["Windows removal"],"Added":["Windows feature"]},"knownIssues":[]}}},
              {"version":"1","build":1,"date":"d","changes":{"Added":["Shared"]},"knownIssues":["Shared limit"],"platforms":{}}]}
            """;
        ReleaseNotesEntry windows = ReleaseNotesCatalog.Current(Encoding.UTF8.GetBytes(Json), "2", 2);
        Assert.Equal(["Added", "Removed"], windows.Changes.Select(change => change.Category));
        Assert.Equal(["Windows feature"], windows.Changes[0].Items);
        Assert.Empty(windows.KnownIssues);
        ReleaseNotesEntry shared = ReleaseNotesCatalog.Current(Encoding.UTF8.GetBytes(Json), "1", 1);
        Assert.Equal(["Shared"], shared.Changes[0].Items);
        Assert.Equal(["Shared limit"], shared.KnownIssues);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"schemaVersion":2,"releases":[{"version":"1","build":1,"date":"d","changes":{"Added":["a"]},"knownIssues":[]}]}""")]
    [InlineData("""{"schemaVersion":1,"releases":[]}""")]
    [InlineData("""{"schemaVersion":1,"releases":[{"version":"1","build":0,"date":"d","changes":{"Added":["a"]},"knownIssues":[]}]}""")]
    [InlineData("""{"schemaVersion":1,"releases":[{"version":"1","build":1,"date":"d","changes":{"Improved":["a"]},"knownIssues":[]}]}""")]
    [InlineData("""{"schemaVersion":1,"releases":[{"version":"1","build":1,"date":"d","changes":{"Added":[" "]},"knownIssues":[]}]}""")]
    [InlineData("""{"schemaVersion":1,"releases":[{"version":"1","build":1,"date":"d","changes":{},"knownIssues":[]}]}""")]
    [InlineData("""{"schemaVersion":1,"releases":[{"version":"1","build":1,"date":"d","changes":{"Added":["a"]}}]}""")]
    [InlineData("""{"schemaVersion":1,"releases":[{"version":"1","build":1,"date":"d","changes":{"Added":["a"]},"knownIssues":[]},{"version":"2","build":1,"date":"d","changes":{"Added":["b"]},"knownIssues":[]}]}""")]
    [InlineData("""{"schemaVersion":1,"releases":[{"version":"1","build":1,"date":"d","changes":{"Added":["a"]},"knownIssues":[],"platforms":[]}]}""")]
    [InlineData("""{"schemaVersion":1,"releases":[{"version":"1","build":1,"date":"d","changes":{"Added":["a"]},"knownIssues":[],"platforms":{"windows":{"changes":{},"knownIssues":[]}}}]}""")]
    [InlineData("""{"schemaVersion":1,"releases":[{"version":"1","build":1,"date":"d","changes":{"Added":["a"]},"knownIssues":[],"platforms":{"windows":{"changes":{"Added":[" "]},"knownIssues":[]}}}]}""")]
    [InlineData("""{"schemaVersion":1,"releases":[{"version":"1","build":1,"date":"d","changes":{"Added":["a"]},"knownIssues":[],"platforms":{"windows":{"changes":{"Improved":["a"]},"knownIssues":[]}}}]}""")]
    [InlineData("""{"schemaVersion":1,"releases":[{"version":"1","build":1,"date":"d","changes":{"Added":["a"]},"knownIssues":[],"platforms":{"windows":{"changes":{"Added":["a"]}}}}]}""")]
    [InlineData("""{"schemaVersion":1,"releases":[{"version":"1","build":1,"date":"d","changes":{},"knownIssues":[],"platforms":{"windows":{"changes":{"Added":["a"]},"knownIssues":[]}}}]}""")]
    public void InvalidCatalogsCannotBeRead(string json)
    {
        ReleaseNotesFailure failure = Assert.Throws<ReleaseNotesFailure>(() => ReleaseNotesCatalog.Current(Encoding.UTF8.GetBytes(json), "1", 1));
        Assert.True(failure.IsInvalid);
        Assert.Equal("The release notes could not be read. Include the app version when reporting this problem.", failure.Message);
    }

    [Fact]
    public void TheSkillIsSavedAsANewAnnotatorFolderAndNothingIsReplaced()
    {
        string scratch = Path.Combine(AppContext.BaseDirectory, "scratch-settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string source = RepositoryFile("integrations", "computer-use", "annotator", "SKILL.md");
            string saved = ComputerUseSkill.Export(scratch, source);
            Assert.Equal(Path.Combine(scratch, "annotator"), saved);
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(saved, "SKILL.md")));
            Assert.Equal("Saved the skill to " + saved + ". Open Setup guide under Help > Use an AI assistant.", ComputerUseSkill.Saved(saved));

            Assert.Equal(SkillExportFailure.FolderExists().Message, Assert.Throws<SkillExportFailure>(() => ComputerUseSkill.Export(scratch, source)).Message);
            Assert.Equal(SkillExportFailure.MissingResource().Message,
                Assert.Throws<SkillExportFailure>(() => ComputerUseSkill.Export(scratch, Path.Combine(scratch, "missing.md"))).Message);
            Assert.Equal("Choose a folder on this PC for the skill.",
                Assert.Throws<SkillExportFailure>(() => ComputerUseSkill.Export(Path.Combine(scratch, "missing"), source)).Message);
            Assert.Equal("Choose a folder on this PC for the skill.", Assert.Throws<SkillExportFailure>(() => ComputerUseSkill.Export("relative", source)).Message);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void SupportInfoIsCoarseAndHoldsNoDrawingsPathsOrNames()
    {
        WorkspaceModel model = Drawing();
        AssertAccepted(model.Add(Primary, new Element(Tool.Text, new Rect(10, 10, 200, 50), text: "private-drawing-content-684")));
        model.UseApps();
        var facts = new SupportFacts
        {
            Version = "0.0.0-dev (0)",
            BuildLabel = "Unstamped development build",
            Installation = "development build (not packaged)",
            Windows = "11 (build 26100)",
            Architecture = "x64 on x64",
            Screens = 2,
            LaunchAtSignIn = LaunchAtSignInState.NotPackaged,
            Capture = "stopped",
        };
        string text = SupportInfo.Text(facts, model.Snapshot);
        Assert.StartsWith("Annotator support info\nVersion: 0.0.0-dev (0)\nRunning build: Unstamped development build\n", text, StringComparison.Ordinal);
        foreach (string line in new[]
        {
            "Launch at sign-in: notPackaged", "Windows: 11 (build 26100)", "Architecture: x64 on x64", "Screens: 2", "Mode: Interact",
            "Screen capture permission: not needed", "Screen capture: stopped", "Screen capture has worked: false",
            "Global show / hide shortcut: not registered", "Scroll shortcut: Alt only",
        })
        {
            Assert.Contains(line + "\n", text, StringComparison.Ordinal);
        }
        Assert.EndsWith("No drawings, screenshots or document names are included.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-drawing-content-684", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
        Assert.DoesNotContain("macOS", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportInfoOfAStoreCopySaysHowItUpdatesAndCaptures()
    {
        var facts = new SupportFacts
        {
            Version = "0.1.0-beta.7 (25)",
            BuildLabel = "Release 1a2b3c4",
            Installation = "Microsoft Store package",
            Windows = "11 (build 26200)",
            Architecture = "arm64 on arm64",
            Screens = 1,
            UpdatesConfigured = true,
            AutomaticUpdateChecks = true,
            LaunchAtSignIn = LaunchAtSignInState.On,
            CapturePermission = "allowed",
            Capture = "stopped",
        };
        string text = SupportInfo.Text(facts, Create().Snapshot);
        foreach (string line in new[]
        {
            "Updates configured: true", "Automatic update checks: true", "Launch at sign-in: enabled", "App: Microsoft Store package",
            "Architecture: arm64 on arm64", "Screen capture permission: allowed",
        })
        {
            Assert.Contains(line + "\n", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WindowsCopyUsesTheWindowsTermsOfSpecification013()
    {
        var copy = typeof(SettingsCopy).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Concat(Enum.GetValues<LaunchAtSignInState>().Select(state => state.Message))
            .Concat([StartupTaskFailure.Unavailable().Message, StartupTaskFailure.Unchanged().Message, SkillExportFailure.InvalidDestination().Message])
            .ToList();
        Assert.True(copy.Count > 80);
        foreach (string mac in new[]
        {
            "macOS", "Mac ", "menu-bar", "menu bar", "Login Items", "log in", "System Settings", "Option-", "Command", "Finder",
            "VoiceOver", "Reduce Motion", "Screen Recording", "Accessibility",
        })
        {
            Assert.DoesNotContain(copy, text => text.Contains(mac, StringComparison.Ordinal));
        }
        Assert.All(copy, text => Assert.False(string.IsNullOrWhiteSpace(text)));
    }

    [Fact]
    public void SettingsTabsKeepTheMacOrderNamesAndIdentifiers()
    {
        Assert.Equal(["General", "Toolbar & Shortcuts", "Pointer", "Break timer", "Permissions", "Help", "About"], SettingsTab.AllCases.Select(tab => tab.Title));
        Assert.Equal(["gearshape", "keyboard", "cursorarrow.rays", "timer", "lock.shield", "questionmark.circle", "info.circle"],
            SettingsTab.AllCases.Select(tab => tab.Symbol));
        Assert.All(SettingsTab.AllCases, tab => Assert.Equal(tab, SettingsTab.FromRawValue(tab.RawValue)));
        Assert.Null(SettingsTab.FromRawValue("shortcuts"));
    }

    private static Dictionary<string, string> Items(Uri url)
    {
        var items = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in url.Query.TrimStart('?').Split('&'))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            items.Add(Uri.UnescapeDataString(pair[..equals]), Uri.UnescapeDataString(pair[(equals + 1)..]));
        }
        return items;
    }

    /// <summary>A file of the repository checkout these tests run from.</summary>
    internal static string RepositoryFile(params string[] parts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException($"{string.Join('/', parts)} was not found above {AppContext.BaseDirectory}.");
    }
}
