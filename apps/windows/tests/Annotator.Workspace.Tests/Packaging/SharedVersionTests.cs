using Annotator.Workspace.Packaging;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Tests.Settings;

namespace Annotator.Workspace.Tests.Packaging;

/// <summary>
/// The shared version and build (WIN-DST-04): read from apps/macos/project.yml as tools/release/release.py reads it, shown as the
/// Mac's label, and mapped to the MSIX package version (Major + 1).Minor.Build.0.
/// </summary>
public sealed class SharedVersionTests
{
    [Fact]
    public void TheRepositoryVersionHasBundledNotesAndAValidPackageVersion()
    {
        SharedVersion shared = SharedVersion.Parse(File.ReadAllText(SettingsLogicTests.RepositoryFile("apps", "macos", "project.yml")));
        // tools/release/release.py check requires the newest notes entry to match the configured version and build, and What's
        // changed matches the running app the same way.
        ReleaseNotesEntry notes = ReleaseNotesCatalog.Load(SettingsLogicTests.RepositoryFile("releases", "notes.json"), shared.NotesVersion, shared.Build);
        Assert.Equal(shared.NotesVersion, notes.Version);
        Assert.Equal(shared.Build, notes.Build);
        Assert.Equal($"{shared.Major + 1}.{shared.Minor}.{shared.Build}.0", shared.MsixVersion);
        Assert.Equal($"{shared.NotesVersion} ({shared.Build})", shared.Label);
    }

    [Fact]
    public void ThePackageVersionFollowsTheSpecificationExample()
    {
        // WIN-DST-04: "0.1.0 build 26 becomes 1.1.26.0"; About shows the shared label, such as 0.1.0-beta.8 (26).
        SharedVersion beta = SharedVersion.Create(0, 1, 0, "beta.8", 26);
        Assert.Equal("1.1.26.0", beta.MsixVersion);
        Assert.Equal("0.1.0-beta.8 (26)", beta.Label);
        Assert.Equal("0.1.0-beta.8", beta.NotesVersion);
        SharedVersion stable = SharedVersion.Create(1, 2, 3, "", 40);
        Assert.Equal("2.2.40.0", stable.MsixVersion);
        Assert.Equal("1.2.3 (40)", stable.Label);
        Assert.Equal("1.2.3", stable.NotesVersion);
    }

    [Fact]
    public void LaterBuildsAlwaysSortHigherAsPackageVersions()
    {
        // The patch number is not part of the package version, so only the always-increasing build keeps the order.
        SharedVersion[] releases =
        [
            SharedVersion.Create(0, 1, 0, "alpha.1", 9),
            SharedVersion.Create(0, 1, 0, "beta.7", 25),
            SharedVersion.Create(0, 1, 1, "", 26),
            SharedVersion.Create(0, 2, 0, "alpha.1", 27),
            SharedVersion.Create(0, 9, 4, "", 60),
            SharedVersion.Create(1, 0, 0, "rc.1", 61),
            SharedVersion.Create(1, 0, 0, "", 62),
        ];
        for (int index = 1; index < releases.Length; index++)
        {
            Assert.True(Version.Parse(releases[index].MsixVersion) > Version.Parse(releases[index - 1].MsixVersion),
                $"{releases[index].MsixVersion} must sort after {releases[index - 1].MsixVersion}.");
        }
        Assert.All(releases, release => Assert.True(Version.Parse(release.MsixVersion).Major >= 1));
        Assert.All(releases, release => Assert.Equal(0, Version.Parse(release.MsixVersion).Revision));
    }

    [Fact]
    public void ParsingReadsTheBaseSettingsLikeTheReleaseTool()
    {
        SharedVersion quoted = SharedVersion.Parse("settings:\n  base:\n    CURRENT_PROJECT_VERSION: \"31\"\r\n    MARKETING_VERSION: \"0.3.2\"\n    ANNOTATOR_PRERELEASE: \"\"\n");
        Assert.Equal("0.3.2 (31)", quoted.Label);
        Assert.Equal("1.3.31.0", quoted.MsixVersion);
        SharedVersion plain = SharedVersion.Parse("    MARKETING_VERSION: 2.0.1\n    ANNOTATOR_PRERELEASE: rc.2   \n    CURRENT_PROJECT_VERSION: 40\n");
        Assert.Equal("2.0.1-rc.2 (40)", plain.Label);
        Assert.Equal("3.0.40.0", plain.MsixVersion);
    }

    [Theory]
    [InlineData("    MARKETING_VERSION: 0.1.0\n    ANNOTATOR_PRERELEASE: beta.1\n", "CURRENT_PROJECT_VERSION")]
    [InlineData("    MARKETING_VERSION: 0.1.0\n    MARKETING_VERSION: 0.2.0\n    ANNOTATOR_PRERELEASE: beta.1\n    CURRENT_PROJECT_VERSION: 3\n", "MARKETING_VERSION")]
    [InlineData("    MARKETING_VERSION: 0.1.0-beta.1\n    ANNOTATOR_PRERELEASE: beta.1\n    CURRENT_PROJECT_VERSION: 3\n", "three numbers")]
    [InlineData("    MARKETING_VERSION: 0.1\n    ANNOTATOR_PRERELEASE: beta.1\n    CURRENT_PROJECT_VERSION: 3\n", "three numbers")]
    [InlineData("    MARKETING_VERSION: 0.1.0\n    ANNOTATOR_PRERELEASE: gamma.1\n    CURRENT_PROJECT_VERSION: 3\n", "alpha, beta or rc")]
    [InlineData("    MARKETING_VERSION: 0.1.0\n    ANNOTATOR_PRERELEASE: beta.0\n    CURRENT_PROJECT_VERSION: 3\n", "alpha, beta or rc")]
    [InlineData("    MARKETING_VERSION: 0.1.0\n    ANNOTATOR_PRERELEASE: beta.1\n    CURRENT_PROJECT_VERSION: 0\n", "positive integer")]
    [InlineData("    MARKETING_VERSION: 0.1.0\n    ANNOTATOR_PRERELEASE: beta.1\n    CURRENT_PROJECT_VERSION: 70000\n", "too large")]
    [InlineData("  MARKETING_VERSION: 0.1.0\n    ANNOTATOR_PRERELEASE: beta.1\n    CURRENT_PROJECT_VERSION: 3\n", "MARKETING_VERSION")]
    public void InvalidSettingsAreRefusedWithTheReason(string projectYml, string reason)
    {
        FormatException error = Assert.Throws<FormatException>(() => SharedVersion.Parse(projectYml));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }
}
