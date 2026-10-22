using Annotator.Platform.Displays;
using Annotator.Platform.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Tests;

public sealed class DisplayTests
{
    [Fact]
    public void EnumerationFindsThePrimaryMonitorWithASaneDpi()
    {
        IReadOnlyList<DisplayInfo> displays = DisplayEnumerator.GetDisplays();

        DisplayInfo primary = Assert.Single(displays, display => display.IsPrimary);
        Assert.Same(primary, displays[0]);
        Assert.NotEqual(0, primary.Handle);
        Assert.StartsWith(@"\\.\DISPLAY", primary.DeviceName, StringComparison.Ordinal);
        Assert.Equal(0, primary.Bounds.Left);
        Assert.Equal(0, primary.Bounds.Top);
        Assert.InRange(primary.Dpi, 96u, 480u);
        Assert.Equal(primary.Dpi / 96.0, primary.Scale);
        foreach (DisplayInfo display in displays)
        {
            Assert.False(display.Bounds.IsEmpty);
            Assert.False(display.WorkArea.IsEmpty);
            Assert.True(display.Bounds.Contains(display.WorkArea), $"{display.DeviceName}: the work area lies inside the monitor.");
            Assert.InRange(display.Dpi, 96u, 480u);
        }
    }

    [Fact]
    public unsafe void EnumerationReportsPhysicalPixelsOnADpiUnawareThread()
    {
        DisplayInfo primary = TestDisplays.Primary();

        // What a DPI-unaware caller sees: Windows scales the monitor by 96 / DPI.
        DPI_AWARENESS_CONTEXT previous = PInvoke.SetThreadDpiAwarenessContext((DPI_AWARENESS_CONTEXT)(nint)(-1));
        Assert.False(previous.IsNull);
        MONITORINFO unaware;
        try
        {
            unaware = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            Assert.True(PInvoke.GetMonitorInfo((HMONITOR)primary.Handle, &unaware));
        }
        finally
        {
            PInvoke.SetThreadDpiAwarenessContext(previous);
        }

        int unawareWidth = unaware.rcMonitor.right - unaware.rcMonitor.left;
        Assert.InRange(primary.Bounds.Width, (unawareWidth * primary.Scale) - 1, (unawareWidth * primary.Scale) + 1);
    }

    [Fact]
    public async Task WatcherReportsAChangeOnlyWhenTheDisplaysDiffer()
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator test displays");
        IReadOnlyList<DisplayInfo> current = TestDisplays.Tiles(1);
        var changed = new TaskCompletionSource<DisplaysChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        DisplayWatcher watcher = await thread.Run(() =>
        {
            var created = new DisplayWatcher(() => current);
            created.Changed += (_, change) => changed.TrySetResult(change);
            return created;
        });

        // A work-area message with nothing different changes nothing.
        bool unchanged = await thread.Run(() =>
        {
            Win32.Send((nint)watcher.Handle, PInvoke.WM_SETTINGCHANGE, (nuint)SYSTEM_PARAMETERS_INFO_ACTION.SPI_SETWORKAREA, 0);
            return watcher.Refresh();
        });
        Assert.False(unchanged);

        IReadOnlyList<DisplayInfo> replaced = TestDisplays.Tiles(2);
        current = replaced;
        await thread.Run(() =>
        {
            Win32.Send((nint)watcher.Handle, PInvoke.WM_DISPLAYCHANGE, 32, Win32.MakeLParam(16, 16));
            Win32.Send((nint)watcher.Handle, PInvoke.WM_DPICHANGED, 0, 0);
        });
        DisplaysChangedEventArgs change = await changed.Task.WaitAsync(TestTimeouts.Work, TestContext.Current.CancellationToken);

        // Both messages of the burst are coalesced into one check.
        Assert.Equal(DisplayChangeReasons.DisplaySettings | DisplayChangeReasons.Dpi, change.Reasons);
        Assert.Single(change.Previous);
        Assert.Equal(replaced, change.Current);
        Assert.Equal(replaced, await thread.Run(() => watcher.Displays));
        await thread.Run(watcher.Dispose);
    }

    [Fact]
    public async Task WatcherRequiresItsOwnThread()
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator test displays");
        DisplayWatcher watcher = await thread.Run(() => new DisplayWatcher(() => TestDisplays.Tiles(1)));

        Assert.Throws<InvalidOperationException>(() => watcher.Displays);
        await thread.Run(watcher.Dispose);
    }
}
