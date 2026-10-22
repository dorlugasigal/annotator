using System.Runtime.InteropServices;
using Annotator.Platform.Capture;
using Annotator.Platform.Displays;
using Annotator.Platform.Threading;
using Annotator.Platform.Windowing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Tests;

/// <summary>
/// Pick from screen (WIN-UI-02) on the real desktop. Explicit: each shows a 200-pixel magenta square near the primary
/// display's top-left corner and the loupe for about a second.
/// </summary>
public sealed class ScreenColorSamplerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>The loupe's ring lies over the square; the pixel under the ring is the square's, so capture leaves the loupe out.</summary>
    [Fact(Explicit = true)]
    public async Task ReadsThePixelUnderTheLoupeAndLeavesTheLoupeOut()
    {
        await using var square = await MagentaSquare.ShowAsync();
        var result = new TaskCompletionSource<ScreenColorSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScreenColorSampler sampler = ScreenColorSampler.Start(new WindowRegistry(), sample => result.TrySetResult(sample),
            new ScreenColorSamplerOptions { CatchesClicks = false });
        sampler.MoveTo(square.CenterX, square.CenterY);
        int diameter = await sampler.DiameterAsync().WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.InRange(diameter, 60, square.Size - 20);
        // Frames of the screen with the loupe on it.
        await Task.Delay(500, TestContext.Current.CancellationToken);
        sampler.PickAt(square.CenterX + diameter / 2 - 3, square.CenterY);
        ScreenColorSample sample = await result.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        var picked = Assert.IsType<ScreenColorSample.Picked>(sample);
        Assert.Equal((255, 0, 255), (picked.Red, picked.Green, picked.Blue));
    }

    [Fact(Explicit = true)]
    public async Task CancelEndsWithoutAColorAndRemovesTheLoupe()
    {
        var result = new TaskCompletionSource<ScreenColorSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        ScreenColorSampler sampler = ScreenColorSampler.Start(new WindowRegistry(), sample => result.TrySetResult(sample),
            new ScreenColorSamplerOptions { CatchesClicks = false });
        nint window = await sampler.WindowAsync().WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.True(Win32.IsVisible(window));
        sampler.Cancel();
        Assert.IsType<ScreenColorSample.Cancelled>(await result.Task.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.False(Win32.IsVisible(window));
        sampler.Dispose();
        Assert.True(SpinWait.SpinUntil(() => !Win32.IsWindow(window), Wait));
    }

    /// <summary>
    /// A real click: the pointer moves onto the square, and the left button is pressed and released there, only after
    /// Windows confirms the loupe window is under the pointer. The pointer goes back where it was.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task AClickPicksThePixelUnderThePointer()
    {
        await using var square = await MagentaSquare.ShowAsync();
        var result = new TaskCompletionSource<ScreenColorSample>(TaskCreationOptions.RunContinuationsAsynchronously);
        using ScreenColorSampler sampler = ScreenColorSampler.Start(new WindowRegistry(), sample => result.TrySetResult(sample));
        nint window = await sampler.WindowAsync().WaitAsync(Wait, TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.True(OwnClick.Click(square.CenterX, square.CenterY, window), "The loupe window was not under the pointer, so nothing was clicked.");
        ScreenColorSample sample = await result.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        var picked = Assert.IsType<ScreenColorSample.Picked>(sample);
        Assert.Equal((255, 0, 255), (picked.Red, picked.Green, picked.Blue));
    }

    /// <summary>A magenta square on its own thread, 40 pixels from the primary display's top-left corner.</summary>
    private sealed class MagentaSquare : IAsyncDisposable
    {
        private readonly DispatcherThread _thread;
        private nint _window;

        private MagentaSquare(DispatcherThread thread, DisplayInfo display)
        {
            _thread = thread;
            Left = display.Bounds.Left + 40;
            Top = display.Bounds.Top + 40;
        }

        public int Size => 200;

        public int Left { get; }

        public int Top { get; }

        public int CenterX => Left + Size / 2;

        public int CenterY => Top + Size / 2;

        public static async Task<MagentaSquare> ShowAsync()
        {
            var square = new MagentaSquare(DispatcherThread.Start("Annotator sampler test magenta"), TestDisplays.Primary());
            await square._thread.Run(() =>
            {
                using (Interop.PerMonitorDpiScope.Enter())
                {
                    square._window = TestWindow.Create(square.Left, square.Top, square.Size, square.Size, magenta: true);
                }
                PInvoke.ShowWindow((HWND)square._window, SHOW_WINDOW_CMD.SW_SHOWNA);
            });
            // Let it paint and reach the screen.
            await Task.Delay(200, TestContext.Current.CancellationToken);
            return square;
        }

        public async ValueTask DisposeAsync()
        {
            await _thread.Run(() =>
            {
                if (_window != 0)
                {
                    TestWindow.Destroy(_window);
                }
            });
            await _thread.Stop();
        }
    }

    /// <summary>One click for this test's own window: refused unless that window is under the point.</summary>
    private static class OwnClick
    {
        private const uint MouseMove = 0x1, LeftDown = 0x2, LeftUp = 0x4, Absolute = 0x8000, VirtualDesk = 0x4000;

        public static bool Click(int x, int y, nint window)
        {
            // Physical pixels, as the loupe and the displays use them.
            using Interop.PerMonitorDpiScope scope = Interop.PerMonitorDpiScope.Enter();
            if (!GetCursorPos(out Point before))
            {
                return false;
            }
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
            int ax = (int)Math.Round((x - left) * 65535.0 / (width - 1)), ay = (int)Math.Round((y - top) * 65535.0 / (height - 1));
            try
            {
                if (WindowFromPoint(new Point(x, y)) != window)
                {
                    return false;
                }
                Send(ax, ay, MouseMove | Absolute | VirtualDesk);
                Thread.Sleep(60);
                if (WindowFromPoint(new Point(x, y)) != window)
                {
                    return false;
                }
                Send(ax, ay, LeftDown | Absolute | VirtualDesk);
                Thread.Sleep(40);
                Send(ax, ay, LeftUp | Absolute | VirtualDesk);
                return true;
            }
            finally
            {
                SetCursorPos(before.X, before.Y);
            }
        }

        private static void Send(int x, int y, uint flags)
        {
            var input = new Input { Type = 0, Mouse = new MouseInput { X = x, Y = y, Flags = flags } };
            if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            {
                throw new InvalidOperationException($"SendInput failed with {Marshal.GetLastPInvokeError()}.");
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct Point(int X, int Y);

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X;
            public int Y;
            public uint Data;
            public uint Flags;
            public uint Time;
            public nint Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public uint Type;
            public MouseInput Mouse;
            // INPUT's union is as large as its largest member, KEYBDINPUT and HARDWAREINPUT being smaller than MOUSEINPUT.
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, Input[] inputs, int size);

        [DllImport("user32.dll")]
        private static extern nint WindowFromPoint(Point point);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);
    }
}
