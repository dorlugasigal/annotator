using System.Drawing;
using System.Runtime.InteropServices;
using Annotator.Platform.Displays;
using Annotator.Platform.Threading;
using Annotator.Platform.Windowing;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Tests;

/// <summary>
/// The toolbar's dock ghost (WIN-OVR-10): a click-through, topmost tool window in the popover layer that never activates.
/// It shows for a moment on a small tile in the primary display's top-left corner.
/// </summary>
public sealed class DockPreviewTests
{
    [Fact]
    public async Task TheGhostIsAClickThroughToolWindowAboveTheToolbar()
    {
        // Stopped in finally, without Dispose: the await may resume on the dispatcher thread, which cannot wait for itself.
        DispatcherThread thread = DispatcherThread.Start("Annotator test dock preview");
        try
        {
            var registry = new WindowRegistry();
            nint handle = 0;
            await thread.Run(() =>
            {
                using var compositor = new Compositor();
                using var ghost = new DockPreviewWindow(registry, compositor, Windows.UI.Color.FromArgb(255, 0, 0x7A, 0xFF));
                handle = ghost.Handle;
                RegisteredWindow registered = Assert.Single(registry.Windows);
                Assert.Equal((handle, WindowLayer.Popover), (registered.Handle, registered.Layer));
                Assert.False(ghost.IsVisible);
                WINDOW_EX_STYLE style = Win32.ExStyle(handle);
                Assert.Equal(DockPreviewWindow.ExStyle, style & DockPreviewWindow.ExStyle);
                Assert.Equal((byte)255, Win32.LayeredAttributes(handle).Alpha);

                DisplayInfo primary = TestDisplays.Primary();
                PixelRect tile = PixelRect.FromSize(primary.Bounds.Left + 4, primary.Bounds.Top + 4, 40, 24);
                HWND foreground = PInvoke.GetForegroundWindow();
                ghost.Show(tile, primary.Scale);
                Assert.True(ghost.IsVisible);
                Assert.Equal(tile, Win32.WindowRect(handle));
                Assert.Equal(foreground, PInvoke.GetForegroundWindow());
                Assert.NotEqual(handle, WindowFromPoint(new Point(tile.Left + tile.Width / 2, tile.Top + tile.Height / 2)));

                PixelRect moved = PixelRect.FromSize(tile.Left + 8, tile.Top, 56, 32);
                ghost.Show(moved, primary.Scale);
                Assert.Equal(moved, Win32.WindowRect(handle));
                ghost.Hide();
                Assert.False(ghost.IsVisible);
                ghost.Hide();
                Assert.Throws<ArgumentException>(() => ghost.Show(PixelRect.FromSize(0, 0, 0, 10), 1));
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
    private static extern nint WindowFromPoint(Point point);
}
