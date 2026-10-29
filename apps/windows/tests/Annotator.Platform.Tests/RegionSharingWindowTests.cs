using System.Numerics;
using System.Runtime.InteropServices;
using Annotator.Platform.Displays;
using Annotator.Platform.Sharing;
using Annotator.Platform.Threading;
using Annotator.Platform.Windowing;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Tests;

/// <summary>
/// Region sharing's windows (WIN-CAP-08, spec 012): the click-through outline and the selection panels in the guide layer,
/// and the output window, an ordinary window that meeting apps list. Each test shows a window for a moment at most.
/// </summary>
public sealed class RegionSharingWindowTests
{
    private const uint Close = 0x0010;
    private const uint Caption = 0x00C00000, SystemMenu = 0x00080000, ThickFrame = 0x00040000, MaximizeBox = 0x00010000, MinimizeBox = 0x00020000;

    private static readonly Windows.UI.Color Accent = Windows.UI.Color.FromArgb(255, 0, 0x7A, 0xFF);

    [Fact]
    public async Task TheOutlineIsAClickThroughGuideAboveEverything()
    {
        DispatcherThread thread = DispatcherThread.Start("Annotator test region outline");
        try
        {
            var registry = new WindowRegistry();
            nint handle = 0;
            await thread.Run(() =>
            {
                using var compositor = new Compositor();
                using var outline = new RegionOutlineWindow(registry, compositor, Accent);
                handle = outline.Handle;
                RegisteredWindow registered = Assert.Single(registry.Windows);
                Assert.Equal((handle, WindowLayer.Guide), (registered.Handle, registered.Layer));
                Assert.Equal(RegionOutlineWindow.ExStyle, Win32.ExStyle(handle) & RegionOutlineWindow.ExStyle);
                Assert.False(outline.IsVisible);

                DisplayInfo primary = TestDisplays.Primary();
                PixelRect frame = PixelRect.FromSize(primary.Bounds.Left + 4, primary.Bounds.Top + 4, 48, 32);
                HWND foreground = PInvoke.GetForegroundWindow();
                outline.Show(frame, primary.Scale, selectionFill: true);
                Assert.True(outline.IsVisible);
                Assert.True(outline.ShowsFill);
                Assert.Equal(frame, Win32.WindowRect(handle));
                Assert.Equal(foreground, PInvoke.GetForegroundWindow());
                Assert.NotEqual(handle, WindowFromPoint(new System.Drawing.Point(frame.Left + frame.Width / 2, frame.Top + frame.Height / 2)));

                // Live sharing keeps only the border.
                outline.Show(frame, primary.Scale, selectionFill: false);
                Assert.False(outline.ShowsFill);
                outline.Hide();
                Assert.False(outline.IsVisible);
            });
            Assert.Empty(registry.Windows);
            Assert.False(Win32.IsWindow(handle));
        }
        finally
        {
            await thread.Stop();
        }
    }

    [Fact]
    public async Task ASelectionIsClampedToItsDisplayAndEndsOnce()
    {
        DispatcherThread thread = DispatcherThread.Start("Annotator test region selection");
        try
        {
            var registry = new WindowRegistry();
            await thread.Run(() =>
            {
                using var compositor = new Compositor();
                DisplayInfo primary = TestDisplays.Primary();
                using var panel = new RegionSelectionPanel(registry, compositor, primary, Accent);
                RegisteredWindow registered = Assert.Single(registry.Windows);
                Assert.Equal((panel.Handle, WindowLayer.Guide), (registered.Handle, registered.Layer));
                Assert.Equal(RegionSelectionPanel.ExStyle, Win32.ExStyle(panel.Handle) & RegionSelectionPanel.ExStyle);
                var results = new List<SelectionRect?>();
                panel.Completed += (_, selection) => results.Add(selection);

                double width = primary.Bounds.Width / primary.Scale, height = primary.Bounds.Height / primary.Scale;
                panel.Drag(new Vector2(200, 150), new Vector2(40, 60));
                Assert.Equal(new SelectionRect(40, 60, 160, 90), Assert.Single(results));
                Assert.Equal(results[0], panel.Selection);

                // A drag past the display's edge stops at the edge (Mac mouseDragged).
                panel.Drag(new Vector2(100, 100), new Vector2((float)width + 500, -300));
                Assert.Equal(new SelectionRect(100, 0, width - 100, 100), results[1]);

                // Released panels take no input.
                panel.Release();
                Assert.False(panel.TakesInput);
                panel.Drag(new Vector2(10, 10), new Vector2(60, 60));
                Assert.Equal(2, results.Count);
            });
            Assert.Empty(registry.Windows);
        }
        finally
        {
            await thread.Stop();
        }
    }

    [Fact]
    public async Task TheOutputIsAnOrdinaryWindowBehindTheAppsWithoutMinimize()
    {
        DispatcherThread thread = DispatcherThread.Start("Annotator test shared region");
        try
        {
            var registry = new WindowRegistry();
            nint handle = 0;
            await thread.Run(() =>
            {
                using var compositor = new Compositor();
                using var window = new SharedRegionWindow(registry, compositor, "Annotator Dev - Shared Region");
                handle = window.Handle;
                RegisteredWindow registered = Assert.Single(registry.Windows);
                Assert.Equal((handle, WindowLayer.Ordinary), (registered.Handle, registered.Layer));
                Assert.Equal("Annotator Dev - Shared Region", Title(handle));
                uint style = (uint)Win32.Style(handle);
                Assert.Equal(Caption | SystemMenu | ThickFrame | MaximizeBox, style & (Caption | SystemMenu | ThickFrame | MaximizeBox));
                Assert.Equal(0u, style & MinimizeBox);
                Assert.Equal(0u, (uint)Win32.ExStyle(handle) & (uint)WINDOW_EX_STYLE.WS_EX_TOPMOST);

                DisplayInfo primary = TestDisplays.Primary();
                (int frameTop, int frameLeft, int frameBottom, int frameRight) = SharedRegionWindow.VisibleFrame(primary.Dpi);
                Assert.True(frameTop > 0 && frameLeft >= 0 && frameBottom >= 0 && frameRight >= 0, $"frame {frameTop} {frameLeft} {frameBottom} {frameRight}");
                PixelRect work = primary.WorkArea;
                int clientWidth = Math.Min(640, work.Width / 2), clientHeight = Math.Min(360, work.Height / 2);
                var client = PixelRect.FromSize(work.Right - frameRight - 24 - clientWidth, work.Top + frameTop + 24, clientWidth, clientHeight);
                window.Place(primary, client);
                Assert.Equal((clientWidth, clientHeight), window.ClientSize);
                Assert.Equal(client, window.ClientScreenRect());

                HWND foreground = PInvoke.GetForegroundWindow();
                window.ShowBehind();
                Assert.True(window.IsVisible);
                Assert.Equal(foreground, PInvoke.GetForegroundWindow());
                // Restoring the app's stacking leaves it an ordinary window: never topmost above the person's apps.
                Assert.Empty(StackOrder.Restore(registry));
                Assert.Equal(0u, (uint)Win32.ExStyle(handle) & (uint)WINDOW_EX_STYLE.WS_EX_TOPMOST);

                // Its close button only asks; the owner stops sharing and disposes it.
                int closes = 0;
                window.CloseRequested += () => closes++;
                Win32.Send(handle, Close, 0, 0);
                Assert.Equal(1, closes);
                Assert.True(Win32.IsWindow(handle));
            });
            Assert.Empty(registry.Windows);
            Assert.False(Win32.IsWindow(handle));
        }
        finally
        {
            await thread.Stop();
        }
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(System.Drawing.Point point);

    private static string Title(nint hwnd)
    {
        var text = new System.Text.StringBuilder(256);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, System.Text.StringBuilder text, int count);
}
