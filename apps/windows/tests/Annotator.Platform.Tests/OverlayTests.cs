using Annotator.Platform.Displays;
using Annotator.Platform.Input;
using Annotator.Platform.Overlays;
using Annotator.Platform.Windowing;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Tests;

public sealed class OverlayTests
{
    private const WINDOW_EX_STYLE BaseExStyle =
        WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP | WINDOW_EX_STYLE.WS_EX_TOPMOST |
        WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_LAYERED;

    [Fact]
    public async Task CreationCoversEachMonitorWithTheExpectedStyles()
    {
        // Real monitors, kept hidden or click-through and empty, so nothing appears on screen.
        await using OverlayHarness harness = await OverlayHarness.StartAsync(OverlayInputMode.Hidden, displays: null);

        var overlays = await harness.Run(() => harness.Manager.Overlays.Select(overlay => (overlay.Handle, overlay.Display, overlay.Root)).ToArray());
        IReadOnlyList<DisplayInfo> displays = DisplayEnumerator.GetDisplays();
        Assert.Equal(displays.Count, overlays.Length);
        foreach (var (handle, display, root) in overlays)
        {
            Assert.Contains(display, displays);
            Assert.NotNull(root);
            WINDOW_STYLE style = Win32.Style(handle);
            Assert.True(style.HasFlag(WINDOW_STYLE.WS_POPUP));
            Assert.Equal((WINDOW_STYLE)0, style & (WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_THICKFRAME | WINDOW_STYLE.WS_SYSMENU | WINDOW_STYLE.WS_VISIBLE));
            Assert.Equal(BaseExStyle | Win32.PassThrough, Win32.ExStyle(handle));
            Assert.Equal(display.Bounds, Win32.WindowRect(handle));
            var (alpha, flags) = Win32.LayeredAttributes(handle);
            Assert.Equal(255, alpha);
            Assert.True(flags.HasFlag(LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA));
            Assert.True(harness.Registry.Contains(handle));
        }

        InputModeChange shown = await harness.Run(() => harness.Manager.SetInputMode(OverlayInputMode.UseApps));
        Assert.Equal(new InputModeChange(OverlayInputMode.Hidden, OverlayInputMode.UseApps, FocusChange.Unchanged), shown);
        foreach (var (handle, _, _) in overlays)
        {
            Assert.True(Win32.IsVisible(handle));
            Assert.Equal(BaseExStyle | Win32.PassThrough, Win32.ExStyle(handle));
        }
    }

    [Fact]
    public async Task SwitchingInputModeFlipsClickThroughOnEveryOverlayTogether()
    {
        await using OverlayHarness harness = await OverlayHarness.StartAsync(OverlayInputMode.UseApps, () => TestDisplays.Tiles(2));

        var states = await harness.Run(() =>
        {
            OverlayManager manager = harness.Manager;
            var observed = new List<(OverlayInputMode Mode, WINDOW_EX_STYLE[] Styles, bool[] Visible, bool Hooked)>();
            void Observe() => observed.Add((
                manager.InputMode,
                [.. manager.Overlays.Select(overlay => Win32.ExStyle(overlay.Handle))],
                [.. manager.Overlays.Select(overlay => Win32.IsVisible(overlay.Handle))],
                manager.IsWatchingForeground));
            Observe();
            manager.SetInputMode(OverlayInputMode.Draw);
            Observe();
            manager.SetInputMode(OverlayInputMode.UseApps);
            Observe();
            manager.SetInputMode(OverlayInputMode.Hidden);
            Observe();
            return observed;
        });

        Assert.Equal([OverlayInputMode.UseApps, OverlayInputMode.Draw, OverlayInputMode.UseApps, OverlayInputMode.Hidden], states.Select(state => state.Mode));
        foreach (var state in states)
        {
            Assert.Equal(2, state.Styles.Length);
            WINDOW_EX_STYLE expected = state.Mode == OverlayInputMode.Draw ? BaseExStyle : BaseExStyle | Win32.PassThrough;
            Assert.All(state.Styles, style => Assert.Equal(expected, style));
            Assert.All(state.Visible, visible => Assert.Equal(state.Mode != OverlayInputMode.Hidden, visible));

            // The foreground hook exists only while overlays are visible (WIN-SYS-07).
            Assert.Equal(state.Mode != OverlayInputMode.Hidden, state.Hooked);
        }
    }

    [Fact]
    public async Task MouseActivationFollowsTheInputMode()
    {
        await using OverlayHarness harness = await OverlayHarness.StartAsync(OverlayInputMode.UseApps, () => TestDisplays.Tiles(1));

        var (useApps, draw) = await harness.Run(() =>
        {
            nint overlay = harness.Manager.Overlays[0].Handle;
            nint lParam = Win32.MakeLParam((int)PInvoke.HTCLIENT, (int)PInvoke.WM_LBUTTONDOWN);
            nint inUseApps = Win32.Send(overlay, PInvoke.WM_MOUSEACTIVATE, (nuint)overlay, lParam);
            harness.Manager.SetInputMode(OverlayInputMode.Draw);
            nint inDraw = Win32.Send(overlay, PInvoke.WM_MOUSEACTIVATE, (nuint)overlay, lParam);
            harness.Manager.SetInputMode(OverlayInputMode.UseApps);
            return (inUseApps, inDraw);
        });

        Assert.Equal((nint)PInvoke.MA_NOACTIVATE, useApps);
        Assert.Equal((nint)PInvoke.MA_ACTIVATE, draw);
    }

    [Fact]
    public async Task AltF4AndCloseRequestsReleaseInputWithoutDestroyingTheOverlay()
    {
        await using OverlayHarness harness = await OverlayHarness.StartAsync(OverlayInputMode.UseApps, () => TestDisplays.Tiles(1));

        nint overlay = await harness.Run(() =>
        {
            nint handle = harness.Manager.Overlays[0].Handle;
            const nint altDown = 1 << 29;
            nint altF4 = altDown | (0x3E << 16) | 1;
            Win32.Send(handle, PInvoke.WM_SYSKEYDOWN, (nuint)VIRTUAL_KEY.VK_F4, altF4);
            Win32.Send(handle, PInvoke.WM_SYSKEYUP, (nuint)VIRTUAL_KEY.VK_F4, altF4 | unchecked((nint)0xC0000000));
            Win32.Send(handle, PInvoke.WM_SYSCOMMAND, PInvoke.SC_CLOSE, 0);
            Win32.Send(handle, PInvoke.WM_CLOSE, 0, 0);
            return handle;
        });

        Assert.True(Win32.IsWindow(overlay));
        Assert.True(harness.Registry.Contains(overlay));
        Assert.Equal(
            [(overlay, InputReleaseReason.AltF4), (overlay, InputReleaseReason.CloseRequested), (overlay, InputReleaseReason.CloseRequested)],
            harness.Sink.ReleaseRequests);
        Assert.Empty(harness.Sink.Keys);
        Assert.Equal(1, await harness.Run(() => harness.Manager.Overlays.Count));
    }

    [Fact]
    public async Task RegistryLearnsAboutEachOverlayBeforeItIsShown()
    {
        var added = new List<(nint Handle, bool VisibleWhenAdded, WindowLayer Layer)>();
        var removed = new List<nint>();
        await using OverlayHarness harness = await OverlayHarness.StartAsync(
            OverlayInputMode.UseApps,
            () => TestDisplays.Tiles(2),
            registry => registry.Changed += (_, change) =>
            {
                if (change.Change == WindowRegistryChange.Added)
                {
                    added.Add((change.Window.Handle, Win32.IsVisible(change.Window.Handle), change.Window.Layer));
                }
                else
                {
                    removed.Add(change.Window.Handle);
                }
            });

        nint[] overlays = await harness.Run(() => harness.Manager.Overlays.Select(overlay => overlay.Handle).ToArray());
        Assert.Equal(overlays, added.Select(entry => entry.Handle));
        Assert.All(added, entry => Assert.False(entry.VisibleWhenAdded));
        Assert.All(added, entry => Assert.Equal(WindowLayer.Canvas, entry.Layer));
        Assert.All(overlays, handle => Assert.True(Win32.IsVisible(handle)));

        await harness.Run(() => harness.Manager.Dispose());
        Assert.Equal(overlays.Order(), removed.Order());
        Assert.Empty(harness.Registry.Windows);
        Assert.All(overlays, handle => Assert.False(Win32.IsWindow(handle)));
    }

    [Fact]
    public async Task MouseAndKeyMessagesReachTheInputSink()
    {
        await using OverlayHarness harness = await OverlayHarness.StartAsync(OverlayInputMode.Draw, () => TestDisplays.Tiles(1));

        nint overlay = await harness.Run(() =>
        {
            nint handle = harness.Manager.Overlays[0].Handle;
            Win32.Send(handle, PInvoke.WM_LBUTTONDOWN, (nuint)MK_LBUTTON, Win32.MakeLParam(3, 4));
            Win32.Send(handle, PInvoke.WM_MOUSEMOVE, (nuint)MK_LBUTTON, Win32.MakeLParam(5, 6));
            Win32.Send(handle, PInvoke.WM_LBUTTONUP, 0, Win32.MakeLParam(5, 6));
            Win32.Send(handle, PInvoke.WM_KEYDOWN, 'A', (0x1E << 16) | 1);
            Win32.Send(handle, PInvoke.WM_CHAR, 'a', (0x1E << 16) | 1);
            harness.Manager.SetInputMode(OverlayInputMode.UseApps);
            return handle;
        });

        RecordedPointer[] pointers = [.. harness.Sink.Pointers.Where(pointer => pointer.Kind is not (PointerInputKind.Enter or PointerInputKind.Leave))];
        Assert.Equal([PointerInputKind.Down, PointerInputKind.Move, PointerInputKind.Up], pointers.Select(pointer => pointer.Kind));
        Assert.All(pointers, pointer => Assert.Equal(overlay, pointer.Overlay));
        Assert.All(pointers, pointer => Assert.Equal(PointerDeviceKind.Mouse, pointer.Device));
        Assert.Equal(PointerButtons.Left, pointers[0].ChangedButton);
        Assert.Equal(PointerButtons.Left, pointers[0].Buttons);
        Assert.Equal(new System.Numerics.Vector2(3, 4), pointers[0].Position);
        Assert.Equal(new System.Numerics.Vector2(5, 6), pointers[1].Position);
        Assert.Equal(PointerButtons.None, pointers[2].Buttons);
        Assert.Equal(PointerButtons.Left, pointers[2].ChangedButton);

        Assert.Equal(2, harness.Sink.Keys.Count);
        Assert.Equal(KeyInputKind.KeyDown, harness.Sink.Keys[0].Kind);
        Assert.Equal('A', harness.Sink.Keys[0].VirtualKey);
        Assert.Equal(0x1E, harness.Sink.Keys[0].ScanCode);
        Assert.Equal(KeyInputKind.Character, harness.Sink.Keys[1].Kind);
        Assert.Equal('a', harness.Sink.Keys[1].Character);
    }

    [Fact]
    public async Task LosingTheMouseCaptureCancelsTheGesture()
    {
        await using OverlayHarness harness = await OverlayHarness.StartAsync(OverlayInputMode.Draw, () => TestDisplays.Tiles(1));

        await harness.Run(() =>
        {
            nint handle = harness.Manager.Overlays[0].Handle;
            Win32.Send(handle, PInvoke.WM_LBUTTONDOWN, (nuint)MK_LBUTTON, Win32.MakeLParam(2, 2));
            Assert.Equal(handle, (nint)PInvoke.GetCapture());

            // Another window taking the mouse sends WM_CAPTURECHANGED.
            Assert.True(PInvoke.ReleaseCapture());
            harness.Manager.SetInputMode(OverlayInputMode.UseApps);
        });

        PointerInputKind[] kinds = [.. harness.Sink.Pointers.Select(pointer => pointer.Kind).Where(kind => kind is not (PointerInputKind.Enter or PointerInputKind.Leave))];
        Assert.Equal([PointerInputKind.Down, PointerInputKind.Canceled], kinds);
    }

    [Fact]
    public async Task DisplayChangesRebuildOverlaysAndReleaseDrawingInput()
    {
        IReadOnlyList<DisplayInfo> displays = TestDisplays.Tiles(2);
        await using OverlayHarness harness = await OverlayHarness.StartAsync(OverlayInputMode.Draw, () => displays);
        var rebuilt = new TaskCompletionSource<OverlaysRebuiltEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldOverlaysAliveWhenChanging = new List<bool>();

        nint[] before = await harness.Run(() =>
        {
            OverlayManager manager = harness.Manager;
            nint[] handles = [.. manager.Overlays.Select(overlay => overlay.Handle)];
            manager.DisplaysChanging += (_, _) => oldOverlaysAliveWhenChanging.AddRange(handles.Select(Win32.IsWindow));
            manager.OverlaysRebuilt += (_, change) => rebuilt.TrySetResult(change);
            return handles;
        });

        displays = TestDisplays.Tiles(1, size: 24);
        await harness.Run(() => Win32.Send((nint)harness.Manager.Watcher.Handle, PInvoke.WM_DISPLAYCHANGE, 32, 0));
        OverlaysRebuiltEventArgs change = await rebuilt.Task.WaitAsync(TestTimeouts.Work, TestContext.Current.CancellationToken);

        Assert.Equal([true, true], oldOverlaysAliveWhenChanging);
        Assert.Equal(DisplayChangeReasons.DisplaySettings, change.DisplayChange.Reasons);
        Assert.Equal(OverlayInputMode.Draw, change.ModeChange.Previous);
        Assert.Equal(OverlayInputMode.UseApps, change.ModeChange.Current);
        OverlayWindow replacement = Assert.Single(change.Overlays);
        Assert.Equal(displays[0], replacement.Display);
        Assert.All(before, handle => Assert.False(Win32.IsWindow(handle)));
        Assert.Equal([replacement.Handle], harness.Registry.Windows.Select(window => window.Handle));
        Assert.Equal(BaseExStyle | Win32.PassThrough, Win32.ExStyle(replacement.Handle));
        Assert.Equal(OverlayInputMode.UseApps, await harness.Run(() => harness.Manager.InputMode));

        // Nothing changed, so a refresh does not rebuild.
        Assert.False(await harness.Run(() => harness.Manager.RefreshDisplays()));
    }

    [Fact]
    public async Task OverlaysRequireAThreadWithADispatcherQueue()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => OverlayManager.Create(new OverlayManagerOptions
        {
            Registry = new WindowRegistry(),
            InputSink = new RecordingSink(),
            DisplaySource = () => TestDisplays.Tiles(1),
        }), TestContext.Current.CancellationToken));
        Assert.Contains("dispatcher queue", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActivatingAnOverlayRequiresDraw()
    {
        await using OverlayHarness harness = await OverlayHarness.StartAsync(OverlayInputMode.Hidden, () => TestDisplays.Tiles(1));

        await harness.Run(() =>
        {
            OverlayWindow overlay = harness.Manager.Overlays[0];
            Assert.Throws<ArgumentException>(() => harness.Manager.SetInputMode(OverlayInputMode.UseApps, overlay));
            Assert.Equal(OverlayInputMode.Hidden, harness.Manager.InputMode);
        });
    }

    private const uint MK_LBUTTON = 0x0001;
}
