using System.Drawing;
using System.Numerics;
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
/// The pointer lens's window (WIN-CAP-06): a click-through, topmost tool window in the adornment layer that never
/// activates, with room around the lens for its own shadow. It shows for a moment in the primary display's top-left corner.
/// </summary>
public sealed class LensWindowTests
{
    [Fact]
    public async Task TheLensIsAClickThroughToolWindowWithRoomForItsShadow()
    {
        // Stopped in finally, without Dispose: the await may resume on the dispatcher thread, which cannot wait for itself.
        DispatcherThread thread = DispatcherThread.Start("Annotator test pointer lens");
        try
        {
            var registry = new WindowRegistry();
            nint handle = 0;
            await thread.Run(() =>
            {
                using var compositor = new Compositor();
                using var lens = new LensWindow(registry, compositor, new LensWindowOptions { Accent = Windows.UI.Color.FromArgb(255, 0, 0x7A, 0xFF) });
                handle = lens.Handle;
                RegisteredWindow registered = Assert.Single(registry.Windows);
                Assert.Equal((handle, WindowLayer.Adornments), (registered.Handle, registered.Layer));
                Assert.False(lens.IsVisible);
                WINDOW_EX_STYLE style = Win32.ExStyle(handle);
                Assert.Equal(DockPreviewWindow.ExStyle, style & DockPreviewWindow.ExStyle);

                DisplayInfo primary = TestDisplays.Primary();
                int size = (int)Math.Round(240 * primary.Scale);
                PixelRect square = PixelRect.FromSize(primary.Bounds.Left + 40, primary.Bounds.Top + 40, size, size);
                HWND foreground = PInvoke.GetForegroundWindow();
                lens.Show(square, primary.Scale, new Vector2(size / 2f));
                Assert.True(lens.IsVisible);
                Assert.Equal(size, lens.PixelSize);
                Assert.Equal(new Vector2(size), lens.Content.Size);
                // The shadow's blur (16 points) and offset (4 points) fit around the lens.
                int margin = (int)Math.Ceiling(20 * primary.Scale);
                Assert.Equal(PixelRect.FromSize(square.Left - margin, square.Top - margin, size + 2 * margin, size + 2 * margin), Win32.WindowRect(handle));
                Assert.Equal(foreground, PInvoke.GetForegroundWindow());
                Assert.NotEqual(handle, WindowFromPoint(new Point(square.Left + size / 2, square.Top + size / 2)));

                lens.Show(PixelRect.FromSize(square.Left + 10, square.Top, size, size), primary.Scale, new Vector2(12, 30));
                Assert.Equal(square.Left + 10 - margin, Win32.WindowRect(handle).Left);
                lens.Hide();
                Assert.False(lens.IsVisible);
                Assert.Throws<ArgumentException>(() => lens.Show(PixelRect.FromSize(0, 0, 10, 20), 1, Vector2.Zero));
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
