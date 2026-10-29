using Annotator.Workspace.Packaging;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Tests.Settings;

namespace Annotator.Workspace.Tests.Packaging;

/// <summary>
/// The shared version (WIN-DST-04): a plain X.Y.Z read from apps/macos/project.yml as tools/release/release.py reads it, its
/// derived build Major * 10000 + Minor * 100 + Patch, which CURRENT_PROJECT_VERSION must hold, and the MSIX package
/// version (Major + 1).Minor.Patch.0.
/// </summary>
public sealed class SharedVersionTests
{
    /// <summary>The Store's last package before plain versions, which every new package must be above.</summary>
    private static readonly Version LastStorePackage = new(1, 1, 35, 0);

    [Fact]
    public void TheRepositoryVersionHasItsDerivedBuildBundledNotesAndAHigherPackageVersion()
    {
        SharedVersion shared = SharedVersion.Parse(File.ReadAllText(SettingsLogicTests.RepositoryFile("apps", "macos", "project.yml")));
        // tools/release/release.py check requires the newest notes entry to match the configured version and build, and What's
        // changed matches the running app the same way.
        ReleaseNotesEntry notes = ReleaseNotesCatalog.Load(SettingsLogicTests.RepositoryFile("releases", "notes.json"), shared.Version, shared.Build);
        Assert.Equal(shared.Version, notes.Version);
        Assert.Equal(shared.Build, notes.Build);
        Assert.Equal((shared.Major * 10000) + (shared.Minor * 100) + shared.Patch, shared.Build);
        Assert.Equal($"{shared.Major + 1}.{shared.Minor}.{shared.Patch}.0", shared.MsixVersion);
        Assert.Equal($"{shared.Version}.0", shared.AssemblyVersion);
        Assert.True(Version.Parse(shared.MsixVersion) > LastStorePackage, $"{shared.MsixVersion} must be above {LastStorePackage}.");
        Assert.True(shared.Build > 35, "The build must be above the builds of earlier versions.");
    }

    [Fact]
    public void TheFirstPlainVersionIs020WithBuild200AndPackage1200()
    {
        SharedVersion first = SharedVersion.Parse("settings:\n  base:\n    CURRENT_PROJECT_VERSION: 200\n    MARKETING_VERSION: 0.2.0\n");
        Assert.Equal("0.2.0", first.Version);
        Assert.Equal(200, first.Build);
        Assert.Equal("1.2.0.0", first.MsixVersion);
        Assert.Equal("0.2.0.0", first.AssemblyVersion);
        Assert.True(Version.Parse(first.MsixVersion) > LastStorePackage);
    }

    [Theory]
    [InlineData(0, 2, 0, 200, "1.2.0.0")]
    [InlineData(0, 2, 1, 201, "1.2.1.0")]
    [InlineData(0, 10, 3, 1003, "1.10.3.0")]
    [InlineData(0, 99, 99, 9999, "1.99.99.0")]
    [InlineData(1, 0, 0, 10000, "2.0.0.0")]
    [InlineData(1, 2, 3, 10203, "2.2.3.0")]
    [InlineData(0, 0, 1, 1, "1.0.1.0")]
    public void TheBuildIsDerivedFromTheVersion(int major, int minor, int patch, int build, string msix)
    {
        SharedVersion version = SharedVersion.Create(major, minor, patch);
        Assert.Equal(build, version.Build);
        Assert.Equal(msix, version.MsixVersion);
        Assert.Equal($"{major}.{minor}.{patch}", version.Version);
        Assert.Equal($"{major}.{minor}.{patch}.0", version.AssemblyVersion);
    }

    [Fact]
    public void LaterVersionsAlwaysHaveHigherBuildsAndPackageVersions()
    {
        SharedVersion[] releases =
        [
            SharedVersion.Create(0, 2, 0),
            SharedVersion.Create(0, 2, 1),
            SharedVersion.Create(0, 2, 99),
            SharedVersion.Create(0, 3, 0),
            SharedVersion.Create(0, 99, 99),
            SharedVersion.Create(1, 0, 0),
            SharedVersion.Create(1, 0, 1),
            SharedVersion.Create(2, 0, 0),
        ];
        for (int index = 1; index < releases.Length; index++)
        {
            Assert.True(releases[index].Build > releases[index - 1].Build,
                $"{releases[index].Version}'s build {releases[index].Build} must be above {releases[index - 1].Build}.");
            Assert.True(Version.Parse(releases[index].MsixVersion) > Version.Parse(releases[index - 1].MsixVersion),
                $"{releases[index].MsixVersion} must sort after {releases[index - 1].MsixVersion}.");
        }
        Assert.All(releases, release => Assert.True(Version.Parse(release.MsixVersion) > LastStorePackage));
        Assert.All(releases, release => Assert.True(Version.Parse(release.MsixVersion).Major >= 1));
        Assert.All(releases, release => Assert.Equal(0, Version.Parse(release.MsixVersion).Revision));
        Assert.All(releases, release => Assert.Equal(0, Version.Parse(release.AssemblyVersion).Revision));
    }

    [Fact]
    public void ParsingReadsTheBaseSettingsLikeTheReleaseTool()
    {
        SharedVersion quoted = SharedVersion.Parse("settings:\n  base:\n    CURRENT_PROJECT_VERSION: \"10203\"\r\n    MARKETING_VERSION: \"1.2.3\"\n    OTHER: 4\n");
        Assert.Equal("1.2.3", quoted.Version);
        Assert.Equal(10203, quoted.Build);
        Assert.Equal("2.2.3.0", quoted.MsixVersion);
        SharedVersion spaced = SharedVersion.Parse("    MARKETING_VERSION: '0.3.0'   \n    CURRENT_PROJECT_VERSION: 300\n");
        Assert.Equal("0.3.0", spaced.Version);
        Assert.Equal(300, spaced.Build);
    }

    [Theory]
    [InlineData("    MARKETING_VERSION: 0.2.0\n", "CURRENT_PROJECT_VERSION")]
    [InlineData("    MARKETING_VERSION: 0.2.0\n    MARKETING_VERSION: 0.3.0\n    CURRENT_PROJECT_VERSION: 200\n", "MARKETING_VERSION")]
    [InlineData("  MARKETING_VERSION: 0.2.0\n    CURRENT_PROJECT_VERSION: 200\n", "MARKETING_VERSION")]
    [InlineData("    MARKETING_VERSION: 0.2.0-beta.1\n    CURRENT_PROJECT_VERSION: 200\n", "three numbers")]
    [InlineData("    MARKETING_VERSION: 0.2.0+5\n    CURRENT_PROJECT_VERSION: 200\n", "three numbers")]
    [InlineData("    MARKETING_VERSION: v0.2.0\n    CURRENT_PROJECT_VERSION: 200\n", "three numbers")]
    [InlineData("    MARKETING_VERSION: 0.2\n    CURRENT_PROJECT_VERSION: 200\n", "three numbers")]
    [InlineData("    MARKETING_VERSION: 0.02.0\n    CURRENT_PROJECT_VERSION: 200\n", "three numbers")]
    [InlineData("    MARKETING_VERSION: 0.100.0\n    CURRENT_PROJECT_VERSION: 10000\n", "99 or lower")]
    [InlineData("    MARKETING_VERSION: 0.1.100\n    CURRENT_PROJECT_VERSION: 200\n", "99 or lower")]
    [InlineData("    MARKETING_VERSION: 0.2.0\n    CURRENT_PROJECT_VERSION: 35\n", "CURRENT_PROJECT_VERSION must be 200")]
    [InlineData("    MARKETING_VERSION: 0.2.0\n    CURRENT_PROJECT_VERSION: 0200\n", "CURRENT_PROJECT_VERSION must be 200")]
    [InlineData("    MARKETING_VERSION: 0.2.1\n    CURRENT_PROJECT_VERSION: 200\n", "CURRENT_PROJECT_VERSION must be 201")]
    [InlineData("    MARKETING_VERSION: 0.0.0\n    CURRENT_PROJECT_VERSION: 0\n", "above 0.0.0")]
    [InlineData("    MARKETING_VERSION: 65534.0.0\n    CURRENT_PROJECT_VERSION: 655340000\n", "too large for a Windows version")]
    [InlineData("    MARKETING_VERSION: 99999999999.0.0\n    CURRENT_PROJECT_VERSION: 1\n", "too large for a version number")]
    public void InvalidSettingsAreRefusedWithTheReason(string projectYml, string reason)
    {
        FormatException error = Assert.Throws<FormatException>(() => SharedVersion.Parse(projectYml));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1, 0, 0, "negative")]
    [InlineData(0, 100, 0, "99 or lower")]
    [InlineData(0, 0, 100, "99 or lower")]
    [InlineData(0, 0, 0, "above 0.0.0")]
    public void OutOfRangeNumbersAreRefused(int major, int minor, int patch, string reason)
    {
        FormatException error = Assert.Throws<FormatException>(() => SharedVersion.Create(major, minor, patch));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }
}
