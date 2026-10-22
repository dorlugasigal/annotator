using Annotator.Workspace.Settings;

namespace Annotator.Workspace.Tests.Settings;

/// <summary>
/// Launch at sign-in (WIN-SYS-01): ports of the Mac's LoginItemTests over the startup task's states, with a fake task that
/// records every change, so no test touches Windows.
/// </summary>
public sealed class LaunchAtSignInTests
{
    private sealed class FakeTask : IStartupTask
    {
        public LaunchAtSignInState State = LaunchAtSignInState.Off;
        public LaunchAtSignInState AfterEnable = LaunchAtSignInState.On;
        public Exception? EnableError;
        public Exception? DisableError;
        public Exception? ReadError;
        public int Enables;
        public int Disables;
        public int Opens;
        public TaskCompletionSource? Gate;

        public Task<LaunchAtSignInState> ReadAsync() => ReadError is { } error ? Task.FromException<LaunchAtSignInState>(error) : Task.FromResult(State);

        public async Task<LaunchAtSignInState> RequestEnableAsync()
        {
            Enables++;
            if (Gate is { } gate)
            {
                await gate.Task;
            }
            if (EnableError is { } error)
            {
                throw error;
            }
            State = AfterEnable;
            return State;
        }

        public Task<LaunchAtSignInState> DisableAsync()
        {
            Disables++;
            if (DisableError is { } error)
            {
                return Task.FromException<LaunchAtSignInState>(error);
            }
            State = LaunchAtSignInState.Off;
            return Task.FromResult(State);
        }

        public void OpenSettings() => Opens++;
    }

    [Fact]
    public async Task ReadingOrRefreshingNeverChangesTheStartupTask()
    {
        var task = new FakeTask();
        var reports = new List<string>();
        var controller = new LaunchAtSignIn(task, reports.Add);
        Assert.Equal(LaunchAtSignInState.Checking, controller.State);
        Assert.False(controller.State.CanChange);
        foreach (LaunchAtSignInState state in Enum.GetValues<LaunchAtSignInState>().Where(state => state != LaunchAtSignInState.Checking))
        {
            task.State = state;
            await controller.RefreshAsync();
            Assert.Equal(state, controller.State);
        }
        Assert.Equal(0, task.Enables);
        Assert.Equal(0, task.Disables);
        Assert.Equal(0, task.Opens);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task ThePersonTurnsItOnAndOffAndOpensStartupAppsOnlyOnRequest()
    {
        var task = new FakeTask();
        var controller = new LaunchAtSignIn(task, message => Assert.Fail(message));
        await controller.RefreshAsync();
        await controller.SetEnabledAsync(true);
        Assert.Equal(LaunchAtSignInState.On, controller.State);
        Assert.True(controller.State.IsRequested);
        await controller.SetEnabledAsync(true);
        Assert.Equal(1, task.Enables);
        await controller.SetEnabledAsync(false);
        Assert.Equal(LaunchAtSignInState.Off, controller.State);
        Assert.Equal(1, task.Disables);
        Assert.Equal(0, task.Opens);
        controller.OpenSettings();
        Assert.Equal(1, task.Opens);
        Assert.Null(controller.ErrorMessage);
    }

    [Fact]
    public async Task TurnedOffInWindowsCannotBeTurnedOnHereAndSaysWhereToGo()
    {
        var task = new FakeTask { State = LaunchAtSignInState.DisabledByUser, AfterEnable = LaunchAtSignInState.DisabledByUser };
        var reports = new List<string>();
        var controller = new LaunchAtSignIn(task, reports.Add);
        await controller.RefreshAsync();
        Assert.True(controller.State.CanChange);
        Assert.False(controller.State.IsRequested);
        Assert.Contains("Windows Settings > Apps > Startup", controller.State.Message, StringComparison.Ordinal);

        await controller.SetEnabledAsync(true);
        Assert.Equal(1, task.Enables);
        Assert.Equal(LaunchAtSignInState.DisabledByUser, controller.State);
        Assert.Equal("Could not change launch at sign-in: Windows did not apply the requested startup change. Check Startup apps and try again. " +
            "Open Startup apps to check its status.", controller.ErrorMessage);
        Assert.Single(reports);

        // Turning it off again changes nothing: it is already off.
        await controller.SetEnabledAsync(false);
        Assert.Equal(0, task.Disables);
    }

    [Theory]
    [InlineData(LaunchAtSignInState.DisabledByPolicy, false)]
    [InlineData(LaunchAtSignInState.EnabledByPolicy, true)]
    [InlineData(LaunchAtSignInState.NotPackaged, false)]
    [InlineData(LaunchAtSignInState.Unavailable, false)]
    public async Task StatesWindowsOrThePackageControlCannotChange(LaunchAtSignInState state, bool requested)
    {
        var task = new FakeTask { State = state };
        var reports = new List<string>();
        var controller = new LaunchAtSignIn(task, reports.Add);
        await controller.RefreshAsync();
        Assert.False(controller.State.CanChange);
        Assert.Equal(requested, controller.State.IsRequested);

        await controller.SetEnabledAsync(!requested);
        Assert.Equal(0, task.Enables + task.Disables);
        Assert.Equal(state, controller.State);
        Assert.StartsWith("Could not change launch at sign-in: Windows cannot manage launch at sign-in for this app.", controller.ErrorMessage, StringComparison.Ordinal);
        Assert.Single(reports);
    }

    [Fact]
    public async Task FailuresKeepTheStateWindowsReportsAndAnOtherStateClearsTheError()
    {
        var task = new FakeTask { EnableError = new StartupTaskFailure("Registration was blocked.") };
        var reports = new List<string>();
        var controller = new LaunchAtSignIn(task, reports.Add);
        await controller.RefreshAsync();
        await controller.SetEnabledAsync(true);
        Assert.Equal(LaunchAtSignInState.Off, controller.State);
        Assert.Contains("Registration was blocked.", controller.ErrorMessage, StringComparison.Ordinal);
        Assert.False(controller.IsChanging);

        task.EnableError = null;
        task.State = LaunchAtSignInState.On;
        task.DisableError = new InvalidOperationException("Removal was blocked.");
        await controller.RefreshAsync();
        Assert.Null(controller.ErrorMessage);
        await controller.SetEnabledAsync(false);
        Assert.Equal(LaunchAtSignInState.On, controller.State);
        Assert.Contains("Removal was blocked.", controller.ErrorMessage, StringComparison.Ordinal);

        // The same state keeps the error; another one clears it.
        await controller.RefreshAsync();
        Assert.NotNull(controller.ErrorMessage);
        task.State = LaunchAtSignInState.Off;
        await controller.RefreshAsync();
        Assert.Null(controller.ErrorMessage);
        Assert.Equal(2, reports.Count);
    }

    [Fact]
    public async Task AnUnreadableTaskIsUnavailableNotSuccessShaped()
    {
        var task = new FakeTask { ReadError = new System.Runtime.InteropServices.COMException("No package.") };
        var controller = new LaunchAtSignIn(task, _ => { });
        await controller.RefreshAsync();
        Assert.Equal(LaunchAtSignInState.Unavailable, controller.State);
        Assert.False(controller.State.CanChange);
    }

    [Fact]
    public async Task AChangeInFlightBlocksOtherChangesAndRefreshes()
    {
        var task = new FakeTask { Gate = new TaskCompletionSource() };
        var controller = new LaunchAtSignIn(task, message => Assert.Fail(message));
        await controller.RefreshAsync();
        Task change = controller.SetEnabledAsync(true);
        Assert.True(controller.IsChanging);
        await controller.SetEnabledAsync(false);
        task.State = LaunchAtSignInState.DisabledByPolicy;
        await controller.RefreshAsync();
        Assert.Equal(LaunchAtSignInState.Off, controller.State);
        task.State = LaunchAtSignInState.Off;
        task.Gate.SetResult();
        await change;
        Assert.False(controller.IsChanging);
        Assert.Equal(LaunchAtSignInState.On, controller.State);
        Assert.Equal(0, task.Disables);
    }
}
