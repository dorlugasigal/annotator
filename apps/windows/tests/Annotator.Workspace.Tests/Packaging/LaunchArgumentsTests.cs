using Annotator.Workspace.Packaging;

namespace Annotator.Workspace.Tests.Packaging;

/// <summary>
/// What a launch asks for: a board from "Annotator.exe board.annotator" or the package's file association ("%1",
/// WIN-SYS-04), and the quiet start of the package's startup task (WIN-SYS-01).
/// </summary>
public sealed class LaunchArgumentsTests
{
    [Fact]
    public void AnOrdinaryLaunchHasNoBoardAndIsNotQuiet()
    {
        LaunchArguments launch = LaunchArguments.Parse([]);
        Assert.Null(launch.Board);
        Assert.False(launch.StartupTask);
        Assert.False(launch.Quiet);
    }

    [Fact]
    public void TheStartupTaskSwitchMakesAQuietLaunch()
    {
        LaunchArguments launch = LaunchArguments.Parse([LaunchArguments.StartupTaskSwitch]);
        Assert.True(launch.StartupTask);
        Assert.True(launch.Quiet);
        Assert.Null(launch.Board);
        Assert.Equal("--startup-task", LaunchArguments.StartupTaskSwitch);
    }

    [Fact]
    public void TheAfterUpdateSwitchMakesAQuietLaunch()
    {
        LaunchArguments launch = LaunchArguments.Parse([LaunchArguments.AfterUpdateSwitch]);
        Assert.True(launch.AfterUpdate);
        Assert.False(launch.StartupTask);
        Assert.True(launch.Quiet);
        Assert.Null(launch.Board);
        Assert.Equal("--after-update", LaunchArguments.AfterUpdateSwitch);
    }

    [Fact]
    public void AFileAssociationLaunchOpensTheBoardAtItsFullPath()
    {
        string board = Path.Combine(Path.GetTempPath(), "Team notes", "Explanation.annotator");
        LaunchArguments launch = LaunchArguments.Parse([board]);
        Assert.Equal(Path.GetFullPath(board), launch.Board);
        Assert.False(launch.Quiet);
    }

    [Fact]
    public void ARelativeBoardIsResolvedFromTheLaunchFolder()
    {
        LaunchArguments launch = LaunchArguments.Parse(["Explanation.annotator"]);
        Assert.Equal(Path.Combine(Environment.CurrentDirectory, "Explanation.annotator"), launch.Board);
    }

    [Fact]
    public void ABoardIsNeverOpenedQuietlyAndSwitchesAreNotBoards()
    {
        string board = Path.GetFullPath("one.annotator");
        LaunchArguments launch = LaunchArguments.Parse(["--startup-task", "--other", "", board, Path.GetFullPath("two.annotator")]);
        Assert.Equal(board, launch.Board);
        Assert.True(launch.StartupTask);
        Assert.False(launch.Quiet);
    }
}
