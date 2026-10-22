using System.Runtime.InteropServices;
using Annotator.Platform.Interop;
using Annotator.Platform.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.Platform.Tests;

public sealed class DispatcherThreadTests
{
    [Fact]
    public async Task RunsQueuedWorkInOrderOnItsOwnStaThread()
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator test worker");
        int testThread = Environment.CurrentManagedThreadId;
        var order = new List<int>();

        Task first = thread.RunAsync(() => order.Add(1));
        Task second = thread.RunAsync(() => order.Add(2));
        var (threadId, apartment, hasAccess, name, hasQueue) = await thread.Run(() => (
            Environment.CurrentManagedThreadId,
            Thread.CurrentThread.GetApartmentState(),
            thread.HasThreadAccess,
            Thread.CurrentThread.Name,
            Windows.System.DispatcherQueue.GetForCurrentThread() is not null));
        await Task.WhenAll(first, second).WaitAsync(TestTimeouts.Work, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], order);
        Assert.NotEqual(testThread, threadId);
        Assert.Equal(ApartmentState.STA, apartment);
        Assert.True(hasAccess);
        Assert.False(thread.HasThreadAccess);
        Assert.Equal("Annotator test worker", name);
        Assert.True(hasQueue);
        Assert.Equal(42, await thread.Run(() => 6 * 7));
    }

    [Fact]
    public async Task WorkExceptionsReachTheReturnedTask()
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator test worker");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => thread.Run(() => throw new InvalidOperationException("boom")));

        Assert.Equal("boom", failure.Message);
        Assert.Equal(1, await thread.Run(() => 1));
    }

    [Fact]
    public async Task ShutdownStopsTheThreadAndRejectsLaterWork()
    {
        DispatcherThread thread = DispatcherThread.Start("Annotator test worker");
        InvalidOperationException? ownThreadDispose = await thread.Run(() =>
        {
            try
            {
                thread.Dispose();
                return null;
            }
            catch (InvalidOperationException expected)
            {
                return expected;
            }
        });
        Assert.NotNull(ownThreadDispose);

        thread.Dispose();

        Assert.True(thread.IsShutDown);
        Assert.Throws<ObjectDisposedException>(() => { _ = thread.RunAsync(() => { }); });
        await thread.ShutdownAsync().WaitAsync(TestTimeouts.Work, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CurrentThreadQueueCannotBeAddedTwice()
    {
        using DispatcherThread thread = DispatcherThread.Start("Annotator test worker");

        await thread.Run(() => Assert.Throws<InvalidOperationException>(DispatcherQueueInterop.CreateOnCurrentThread));
    }

    [Fact]
    public async Task TheMessageLoopTurnsKeyPressesIntoCharacters()
    {
        // The canvas text editor types the WM_CHAR messages that TranslateMessage makes from WM_KEYDOWN on this loop.
        using DispatcherThread thread = DispatcherThread.Start("Annotator test keys");
        nint window = await thread.Run(KeyWindow.Create);
        try
        {
            await thread.Run(() => KeyWindow.PressSpace(window));
            using var timeout = new CancellationTokenSource(TestTimeouts.Work);
            while (KeyWindow.Received == 0)
            {
                await Task.Delay(10, timeout.Token);
            }
            Assert.Equal(' ', KeyWindow.Received);
        }
        finally
        {
            await thread.Run(() => KeyWindow.Destroy(window));
        }
    }

    /// <summary>A hidden window that records the first character it receives.</summary>
    private static unsafe class KeyWindow
    {
        private const string ClassName = "Annotator.Tests.Keys";
        private static int s_received;
        private static bool s_registered;

        public static char Received => (char)Volatile.Read(ref s_received);

        public static nint Create()
        {
            Volatile.Write(ref s_received, 0);
            if (!s_registered)
            {
                fixed (char* name = ClassName)
                {
                    var windowClass = new WNDCLASSEXW
                    {
                        cbSize = (uint)sizeof(WNDCLASSEXW),
                        lpfnWndProc = &Procedure,
                        hInstance = (HINSTANCE)PInvoke.GetModuleHandle((PCWSTR)null).Value,
                        lpszClassName = name,
                    };
                    if (PInvoke.RegisterClassEx(&windowClass) == 0)
                    {
                        throw new InvalidOperationException($"RegisterClassEx failed with {Marshal.GetLastPInvokeError()}.");
                    }
                }
                s_registered = true;
            }
            HWND window = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE, ClassName, null,
                                                 WINDOW_STYLE.WS_POPUP, -32000, -32000, 1, 1, HWND.Null, HMENU.Null, HINSTANCE.Null, null);
            return window.IsNull ? throw new InvalidOperationException($"CreateWindowEx failed with {Marshal.GetLastPInvokeError()}.") : (nint)window;
        }

        /// <summary>Posts a Space press (scan code 0x39), which every keyboard layout types as a space.</summary>
        public static void PressSpace(nint window)
        {
            if (!PInvoke.PostMessage((HWND)window, PInvoke.WM_KEYDOWN, (nuint)VIRTUAL_KEY.VK_SPACE, 1 | (0x39 << 16)))
            {
                throw new InvalidOperationException($"PostMessage failed with {Marshal.GetLastPInvokeError()}.");
            }
        }

        public static void Destroy(nint window) => PInvoke.DestroyWindow((HWND)window);

        [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
        private static LRESULT Procedure(HWND window, uint message, WPARAM wParam, LPARAM lParam)
        {
            if (message == PInvoke.WM_CHAR)
            {
                Interlocked.CompareExchange(ref s_received, (int)wParam.Value, 0);
                return default;
            }
            return PInvoke.DefWindowProc(window, message, wParam, lParam);
        }
    }
}
