using System.Diagnostics;
using Annotator.Platform.Displays;
using Annotator.Platform.Interop;
using Annotator.Platform.Overlays;
using Annotator.Platform.Threading;
using Annotator.Platform.Windowing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Tests;

/// <summary>
/// WIN-OVR-04: canvases stay below Annotator's higher layers. Activation brings a window to the top of its band, so a
/// click on a canvas in Draw, or focus coming back to it after a popover, used to put the canvas above the toolbar: the
/// toolbar still showed through the transparent canvas, but its buttons no longer took clicks.
/// </summary>
public sealed class StackingTests
{
    [Fact]
    public async Task APlaceAfterTheCanvasThreadsHiddenWindowCountsAsTheTop()
    {
        // Measured in the app: activation asks to put the canvas right after its thread's hidden IME window, which is still
        // below the toolbar then, and lifts that window to the top in the same move, so the canvas ends above the toolbar.
        await using OverlayHarness harness = await OverlayHarness.StartAsync(OverlayInputMode.Hidden, () => TestDisplays.Tiles(1, size: 48));
        PixelRect tile = TestDisplays.Tiles(1, size: 48)[0].Bounds;
        await using StandInWindow toolbar = await StandInWindow.StartAsync(harness.Registry, WindowLayer.Toolbar,
            PixelRect.FromSize(tile.Left + 8, tile.Top + 8, 24, 12));
        (nint overlay, HelperWindow helper) = await harness.Run(() =>
        {
            harness.Manager.SetInputMode(OverlayInputMode.Draw);
            return (harness.Manager.Overlays[0].Handle, HelperWindow.Create(HWND.Null));
        });
        try
        {
            Assert.True(await ZOrder.Until(() => ZOrder.IsAbove(toolbar.Window, overlay)), "Draw did not put the toolbar above the canvas.");
            (HWND? helperPlace, HWND? toolbarPlace) = await harness.Run(() =>
            {
                // The hidden helper sits below the toolbar, as the IME window did.
                const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
                Assert.True(PInvoke.SetWindowPos(helper.Handle, (HWND)overlay, 0, 0, 0, 0, flags));
                Assert.True(ZOrder.IsAbove(toolbar.Window, (nint)helper.Handle));
                return (StackOrder.BelowHigherLayers(harness.Registry, (HWND)overlay, WindowLayer.Canvas, helper.Handle),
                        StackOrder.BelowHigherLayers(harness.Registry, (HWND)overlay, WindowLayer.Canvas, (HWND)toolbar.Window));
            });
            Assert.Equal((HWND)toolbar.Window, helperPlace);
            Assert.Null(toolbarPlace);
        }
        finally
        {
            await harness.Run(() => helper.Destroy());
        }
    }

    [Fact]
    public async Task ACanvasLeftAboveTheToolbarGoesBackBelowIt()
    {
        await using OverlayHarness harness = await OverlayHarness.StartAsync(OverlayInputMode.Hidden, () => TestDisplays.Tiles(1, size: 48));
        PixelRect tile = TestDisplays.Tiles(1, size: 48)[0].Bounds;
        await using StandInWindow toolbar = await StandInWindow.StartAsync(harness.Registry, WindowLayer.Toolbar,
            PixelRect.FromSize(tile.Left + 8, tile.Top + 8, 24, 12));
        nint overlay = await harness.Run(() =>
        {
            harness.Manager.SetInputMode(OverlayInputMode.Draw);
            return harness.Manager.Overlays[0].Handle;
        });
        Assert.True(await ZOrder.Until(() => ZOrder.IsAbove(toolbar.Window, overlay)), "Draw did not put the toolbar above the canvas.");

        // The toolbar drops below the canvas. A change of the canvas's place that does not say where it goes (as activation
        // moves its helper windows with it) leads to a check once the change is done, which puts the canvas back below.
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
        await toolbar.Thread.Run(() => Assert.True(PInvoke.SetWindowPos((HWND)toolbar.Window, (HWND)overlay, 0, 0, 0, 0, flags)));
        Assert.True(ZOrder.IsAbove(overlay, toolbar.Window));
        await harness.Run(() =>
        {
            var changed = new WINDOWPOS { hwnd = (HWND)overlay, flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE };
            unsafe
            {
                Win32.Send(overlay, PInvoke.WM_WINDOWPOSCHANGED, 0, (nint)(&changed));
            }
        });
        Assert.True(await ZOrder.Until(() => ZOrder.IsAbove(toolbar.Window, overlay)), "The canvas stayed above the toolbar.");
        Assert.True(Win32.ExStyle(overlay).HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST));
    }

    [Fact]
    public async Task ACanvasThatMovesToTheTopStaysBelowTheToolbar()
    {
        await using OverlayHarness harness = await OverlayHarness.StartAsync(OverlayInputMode.Hidden, () => TestDisplays.Tiles(1, size: 48));
        PixelRect tile = TestDisplays.Tiles(1, size: 48)[0].Bounds;
        await using StandInWindow toolbar = await StandInWindow.StartAsync(harness.Registry, WindowLayer.Toolbar,
            PixelRect.FromSize(tile.Left + 8, tile.Top + 8, 24, 12));

        nint overlay = await harness.Run(() =>
        {
            harness.Manager.SetInputMode(OverlayInputMode.Draw);
            return harness.Manager.Overlays[0].Handle;
        });
        Assert.True(await ZOrder.Until(() => ZOrder.IsAbove(toolbar.Window, overlay)), "Draw did not put the toolbar above the canvas.");

        // Activation moves a window to the top of its band (HWND_TOP); a foreground restore uses HWND_TOPMOST.
        await harness.Run(() =>
        {
            const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
            Assert.True(PInvoke.SetWindowPos((HWND)overlay, HWND.Null, 0, 0, 0, 0, flags));
            Assert.True(ZOrder.IsAbove(toolbar.Window, overlay), "HWND_TOP put the canvas above the toolbar.");
            Assert.True(PInvoke.SetWindowPos((HWND)overlay, HWND.HWND_TOPMOST, 0, 0, 0, 0, flags));
            Assert.True(ZOrder.IsAbove(toolbar.Window, overlay), "HWND_TOPMOST put the canvas above the toolbar.");
            harness.Manager.SetInputMode(OverlayInputMode.UseApps);
        });

        // The canvas stays in the topmost band, directly below the toolbar.
        Assert.True(Win32.ExStyle(overlay).HasFlag(WINDOW_EX_STYLE.WS_EX_TOPMOST));
        Assert.Equal(toolbar.Window, ZOrder.Above(overlay));
    }

    [Fact]
    public async Task ACanvasWithNoHigherWindowShownGoesToTheTop()
    {
        await using OverlayHarness harness = await OverlayHarness.StartAsync(OverlayInputMode.Draw, () => TestDisplays.Tiles(1));
        PixelRect tile = TestDisplays.Tiles(1)[0].Bounds;
        await using StandInWindow toolbar = await StandInWindow.StartAsync(harness.Registry, WindowLayer.Toolbar,
            PixelRect.FromSize(tile.Left + 2, tile.Top + 2, 8, 8));
        await toolbar.Thread.Run(() => PInvoke.ShowWindow((HWND)toolbar.Window, SHOW_WINDOW_CMD.SW_HIDE));

        nint overlay = await harness.Run(() =>
        {
            nint handle = harness.Manager.Overlays[0].Handle;
            const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
            Assert.True(PInvoke.SetWindowPos((HWND)handle, HWND.HWND_TOPMOST, 0, 0, 0, 0, flags));
            return handle;
        });

        // A hidden toolbar does not hold the canvas down.
        Assert.True(ZOrder.IsAbove(overlay, toolbar.Window));
    }

    /// <summary>
    /// Explicit: real input. Shows a 120-pixel canvas tile and a stand-in toolbar in the primary display's top-left corner
    /// for about a second, clicks only those two windows (each checked with WindowFromPoint first), then puts the pointer
    /// and the foreground window back. Reproduces the owner's report "sometimes i cant press the fill, or any button on
    /// the tool bar": a click on the canvas activates it, and the toolbar must still take the next click.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task ClickingTheCanvasLeavesTheToolbarClickable()
    {
        await using OverlayHarness harness = await OverlayHarness.StartAsync(OverlayInputMode.Hidden, () => TestDisplays.Tiles(1, size: 120));
        PixelRect tile = TestDisplays.Tiles(1, size: 120)[0].Bounds;
        await using StandInWindow toolbar = await StandInWindow.StartAsync(harness.Registry, WindowLayer.Toolbar,
            PixelRect.FromSize(tile.Left + 60, tile.Top + 10, 50, 30));
        // Another app's window, below the canvas, to take the foreground before Draw activates the canvas again.
        await using StandInWindow app = await StandInWindow.StartAsync(registry: null, WindowLayer.Canvas,
            PixelRect.FromSize(tile.Left + 4, tile.Top + 4, 20, 20), activates: true);
        (int X, int Y) button = (tile.Left + 85, tile.Top + 25);
        (int X, int Y) canvas = (tile.Left + 20, tile.Top + 90);

        OverlayWindow overlayWindow = await harness.Run(() =>
        {
            harness.Manager.SetInputMode(OverlayInputMode.Draw);
            return harness.Manager.Overlays[0];
        });
        nint overlay = overlayWindow.Handle;
        Assert.True(await ZOrder.Until(() => ZOrder.IsAbove(toolbar.Window, overlay)), "Draw did not put the toolbar above the canvas.");

        HWND before = PInvoke.GetForegroundWindow();
        Assert.True(PInvoke.GetCursorPos(out System.Drawing.Point pointer));
        try
        {
            Assert.Equal(toolbar.Window, RealInput.WindowAt(button.X, button.Y));

            // A click on the canvas in Draw activates it (WM_MOUSEACTIVATE answers MA_ACTIVATE).
            RealInput.Click(canvas.X, canvas.Y, overlay);
            Assert.True(await ZOrder.Until(() => (nint)PInvoke.GetForegroundWindow() == overlay), "The click did not activate the canvas.");
            Assert.Equal(toolbar.Window, RealInput.WindowAt(button.X, button.Y));
            RealInput.Click(button.X, button.Y, toolbar.Window);
            Assert.True(await ZOrder.Until(() => toolbar.Presses == 1), "The toolbar did not get the click.");

            // Use apps and another app, then Draw again with a focus request, as Resume drawing does.
            await harness.Run(() => harness.Manager.SetInputMode(OverlayInputMode.UseApps));
            Assert.True(PInvoke.SetForegroundWindow((HWND)app.Window));
            Assert.True(await ZOrder.Until(() => (nint)PInvoke.GetForegroundWindow() == app.Window), "The other window did not take the foreground.");
            FocusChange focus = await harness.Run(() => harness.Manager.SetInputMode(OverlayInputMode.Draw, overlayWindow).Focus);
            Assert.Equal(FocusChange.ActivatedOverlay, focus);
            Assert.True(await ZOrder.Until(() => (nint)PInvoke.GetForegroundWindow() == overlay), "Draw did not activate the canvas.");
            Assert.Equal(toolbar.Window, RealInput.WindowAt(button.X, button.Y));
            RealInput.Click(button.X, button.Y, toolbar.Window);
            Assert.True(await ZOrder.Until(() => toolbar.Presses == 2), "The toolbar did not get the second click.");
        }
        finally
        {
            await harness.Run(() => harness.Manager.SetInputMode(OverlayInputMode.Hidden));
            PInvoke.SetCursorPos(pointer.X, pointer.Y);
            if (PInvoke.IsWindow(before))
            {
                PInvoke.SetForegroundWindow(before);
            }
        }
    }
}

/// <summary>The z-order of top-level windows, read from the top.</summary>
internal static class ZOrder
{
    /// <summary>True when <paramref name="upper"/> is above <paramref name="lower"/>.</summary>
    public static bool IsAbove(nint upper, nint lower)
    {
        int steps = 0;
        for (HWND window = PInvoke.GetTopWindow(HWND.Null); !window.IsNull && steps < 100_000; window = PInvoke.GetWindow(window, GET_WINDOW_CMD.GW_HWNDNEXT), steps++)
        {
            if ((nint)window == upper)
            {
                return true;
            }
            if ((nint)window == lower)
            {
                return false;
            }
        }
        throw new InvalidOperationException("Neither window is in the z-order.");
    }

    /// <summary>The window directly above <paramref name="window"/>.</summary>
    public static nint Above(nint window) => (nint)PInvoke.GetWindow((HWND)window, GET_WINDOW_CMD.GW_HWNDPREV);

    /// <summary>Waits until <paramref name="condition"/> holds, at most two seconds.</summary>
    public static async Task<bool> Until(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > 2000)
            {
                return false;
            }
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        return true;
    }
}

/// <summary>
/// Real mouse and keyboard input for explicit tests. Every click first checks with WindowFromPoint that the test's own
/// window is under the point, and every key that the foreground window is the test's own; otherwise it refuses.
/// </summary>
internal static unsafe class RealInput
{
    public static nint WindowAt(int x, int y)
    {
        using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
        HWND window = PInvoke.WindowFromPoint(new System.Drawing.Point(x, y));
        return window.IsNull ? 0 : (nint)PInvoke.GetAncestor(window, GET_ANCESTOR_FLAGS.GA_ROOT);
    }

    public static void Click(int x, int y, nint expected)
    {
        Press(x, y, expected);
        Release();
    }

    /// <summary>Moves the pointer to a point over <paramref name="expected"/> and presses the left button.</summary>
    public static void Press(int x, int y, nint expected)
    {
        using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
        if (WindowAt(x, y) != expected)
        {
            throw new InvalidOperationException($"Refused to press at {x}, {y}: the test's window is not there.");
        }
        Move(x, y);
        Thread.Sleep(30);
        if (WindowAt(x, y) != expected)
        {
            throw new InvalidOperationException($"Refused to press at {x}, {y}: the window changed.");
        }
        Send(new MOUSEINPUT { dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN });
    }

    /// <summary>Moves the pointer as the mouse does, to a point over <paramref name="expected"/>.</summary>
    public static void DragTo(int x, int y, nint expected)
    {
        if (WindowAt(x, y) != expected)
        {
            throw new InvalidOperationException($"Refused to drag to {x}, {y}: the test's window is not there.");
        }
        Move(x, y);
    }

    public static void Release() => Send(new MOUSEINPUT { dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP });

    /// <summary>Presses or releases a key while one of this process's windows is the foreground window.</summary>
    public static void Key(VIRTUAL_KEY key, bool up)
    {
        HWND foreground = PInvoke.GetForegroundWindow();
        PInvoke.GetWindowThreadProcessId(foreground, out uint process);
        if (foreground.IsNull || process != (uint)Environment.ProcessId)
        {
            throw new InvalidOperationException("Refused to send a key: the foreground window is not the test's own.");
        }
        Span<INPUT> inputs =
        [
            new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD, Anonymous = { ki = new KEYBDINPUT { wVk = key, dwFlags = up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0 } } },
        ];
        if (PInvoke.SendInput(inputs, sizeof(INPUT)) != (uint)inputs.Length)
        {
            throw NativeCallException.FromLastError("SendInput");
        }
    }

    private static void Move(int x, int y)
    {
        using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
        int left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        int top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        int width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        int height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
        Send(new MOUSEINPUT
        {
            dx = (int)Math.Round((x - left) * 65535.0 / (width - 1)),
            dy = (int)Math.Round((y - top) * 65535.0 / (height - 1)),
            dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK,
        });
    }

    private static void Send(MOUSEINPUT mouse)
    {
        Span<INPUT> inputs = [new INPUT { type = INPUT_TYPE.INPUT_MOUSE, Anonymous = { mi = mouse } }];
        if (PInvoke.SendInput(inputs, sizeof(INPUT)) != (uint)inputs.Length)
        {
            throw NativeCallException.FromLastError("SendInput");
        }
        Thread.Sleep(15);
    }
}

/// <summary>A hidden window owned by another window of its thread, like the IME windows Windows gives every thread.</summary>
internal sealed class HelperWindow : NativeWindow
{
    public static HelperWindow Create(HWND owner)
    {
        var window = new HelperWindow();
        window.CreateHandle("Annotator.Platform.Tests.Helper", WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, WINDOW_STYLE.WS_POPUP, 0, 0, 1, 1, owner,
            "CreateWindowEx (helper)");
        return window;
    }

    public void Destroy() => DestroyHandle("DestroyWindow (helper)");
}

/// <summary>
/// A stand-in for an app window of a higher layer, such as the toolbar: a topmost tool window that never activates, on
/// its own thread as the app's toolbar is, registered before it is shown. It counts the presses it receives. With
/// <c>activates</c>, it stands in for another app's ordinary window instead: not topmost, activating and unregistered.
/// </summary>
internal sealed class StandInWindow : NativeWindow, IAsyncDisposable
{
    private WindowRegistration? _registration;
    private bool _activates;
    private int _presses;

    private StandInWindow(DispatcherThread thread) => Thread = thread;

    public DispatcherThread Thread { get; }

    public nint Window => (nint)Handle;

    public int Presses => Volatile.Read(ref _presses);

    public static async Task<StandInWindow> StartAsync(WindowRegistry? registry, WindowLayer layer, PixelRect bounds, bool activates = false)
    {
        var thread = DispatcherThread.Start("Annotator test stand-in window");
        var window = new StandInWindow(thread) { _activates = activates };
        try
        {
            await thread.Run(() =>
            {
                WINDOW_EX_STYLE style = activates ? WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                    : WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
                using (PerMonitorDpiScope.Enter())
                {
                    window.CreateHandle("Annotator.Platform.Tests.StandIn", style, WINDOW_STYLE.WS_POPUP,
                        bounds.Left, bounds.Top, bounds.Width, bounds.Height, HWND.Null, "CreateWindowEx (stand-in)");
                }
                window._registration = registry?.Register(window.Window, layer);
                PInvoke.ShowWindow(window.Handle, SHOW_WINDOW_CMD.SW_SHOWNA);
                const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
                if (!activates && !PInvoke.SetWindowPos(window.Handle, HWND.HWND_TOPMOST, 0, 0, 0, 0, flags))
                {
                    throw NativeCallException.FromLastError("SetWindowPos (stand-in)");
                }
            });
            return window;
        }
        catch
        {
            await thread.Stop();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Thread.Run(() => DestroyHandle("DestroyWindow (stand-in)"));
        }
        finally
        {
            await Thread.Stop();
        }
    }

    protected override LRESULT WndProc(uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_MOUSEACTIVATE when !_activates:
                return (LRESULT)(nint)PInvoke.MA_NOACTIVATE;
            case PInvoke.WM_LBUTTONDOWN:
                Interlocked.Increment(ref _presses);
                return default;
            default:
                return base.WndProc(message, wParam, lParam);
        }
    }

    protected override void OnDestroyed(HWND destroyed)
    {
        _registration?.Dispose();
        _registration = null;
    }
}
