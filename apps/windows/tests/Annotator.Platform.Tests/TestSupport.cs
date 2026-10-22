using System.Numerics;
using Annotator.Platform.Displays;
using Annotator.Platform.Input;
using Annotator.Platform.Interop;
using Annotator.Platform.Overlays;
using Annotator.Platform.Threading;
using Annotator.Platform.Windowing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

// Tests create real windows and register global shortcuts; run them one at a time.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Annotator.Platform.Tests;

internal static class TestTimeouts
{
    public static readonly TimeSpan Work = TimeSpan.FromSeconds(15);
}

internal static class DispatcherThreadTestExtensions
{
    public static Task<T> Run<T>(this DispatcherThread thread, Func<T> work) =>
        thread.RunAsync(work).WaitAsync(TestTimeouts.Work, TestContext.Current.CancellationToken);

    public static Task Run(this DispatcherThread thread, Action work) =>
        thread.RunAsync(work).WaitAsync(TestTimeouts.Work, TestContext.Current.CancellationToken);

    public static Task Stop(this DispatcherThread thread) =>
        thread.ShutdownAsync().WaitAsync(TestTimeouts.Work, TestContext.Current.CancellationToken);
}

internal static class TestDisplays
{
    public static DisplayInfo Primary() => DisplayEnumerator.GetDisplays().Single(display => display.IsPrimary);

    /// <summary>
    /// Small squares in the primary monitor's top-left corner stand in for displays, so tests that
    /// switch overlays to Draw never cover the screen.
    /// </summary>
    public static IReadOnlyList<DisplayInfo> Tiles(int count, int size = 16)
    {
        DisplayInfo primary = Primary();
        return [.. Enumerable.Range(0, count).Select(index =>
        {
            PixelRect bounds = PixelRect.FromSize(primary.Bounds.Left + (index * size), primary.Bounds.Top, size, size);
            return primary with
            {
                DeviceName = $@"\\.\ANNOTATOR-TEST{index + 1}",
                Bounds = bounds,
                WorkArea = bounds,
                IsPrimary = index == 0,
            };
        })];
    }
}

internal readonly record struct RecordedPointer(
    nint Overlay,
    PointerInputKind Kind,
    PointerDeviceKind Device,
    uint PointerId,
    PointerButtons Buttons,
    PointerButtons ChangedButton,
    Vector2 Position,
    int SampleCount,
    ModifierKeys Modifiers = ModifierKeys.None);

internal sealed class RecordingSink : IOverlayInputSink
{
    public List<RecordedPointer> Pointers { get; } = [];

    public List<KeyInput> Keys { get; } = [];

    public List<(nint Overlay, InputReleaseReason Reason)> ReleaseRequests { get; } = [];

    public List<(nint Overlay, bool HasFocus)> FocusChanges { get; } = [];

    public void OnPointer(OverlayWindow overlay, in PointerInput input) =>
        Pointers.Add(new RecordedPointer(overlay.Handle, input.Kind, input.Device, input.PointerId, input.Buttons, input.ChangedButton,
                                         input.Current.Position, input.Samples.Length, input.Modifiers));

    public bool OnKey(OverlayWindow overlay, in KeyInput input)
    {
        Keys.Add(input);
        return true;
    }

    public void OnInputReleaseRequested(OverlayWindow overlay, InputReleaseReason reason) => ReleaseRequests.Add((overlay.Handle, reason));

    public void OnFocusChanged(OverlayWindow overlay, bool hasFocus) => FocusChanges.Add((overlay.Handle, hasFocus));
}

/// <summary>An overlay manager on its own dispatcher thread, as the app runs it.</summary>
internal sealed class OverlayHarness : IAsyncDisposable
{
    private OverlayHarness(DispatcherThread thread) => Thread = thread;

    public DispatcherThread Thread { get; }

    public WindowRegistry Registry { get; } = new();

    public RecordingSink Sink { get; } = new();

    public OverlayManager Manager { get; private set; } = null!;

    public static async Task<OverlayHarness> StartAsync(OverlayInputMode mode, Func<IReadOnlyList<DisplayInfo>>? displays, Action<WindowRegistry>? beforeCreate = null)
    {
        var harness = new OverlayHarness(DispatcherThread.Start("Annotator test overlays"));
        try
        {
            await harness.Thread.Run(() =>
            {
                beforeCreate?.Invoke(harness.Registry);
                harness.Manager = OverlayManager.Create(new OverlayManagerOptions
                {
                    Registry = harness.Registry,
                    InputSink = harness.Sink,
                    DisplaySource = displays,
                    InitialMode = mode,
                });
            });
            return harness;
        }
        catch
        {
            await harness.Thread.Stop();
            throw;
        }
    }

    public Task<T> Run<T>(Func<T> work) => Thread.Run(work);

    public Task Run(Action work) => Thread.Run(work);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Thread.Run(() => Manager?.Dispose());
        }
        finally
        {
            await Thread.Stop();
        }
    }
}

internal static unsafe class Win32
{
    public const WINDOW_EX_STYLE PassThrough = WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;

    public static WINDOW_STYLE Style(nint window) => WindowLongs.GetStyle((HWND)window);

    public static WINDOW_EX_STYLE ExStyle(nint window) => WindowLongs.GetExStyle((HWND)window);

    public static bool IsWindow(nint window) => PInvoke.IsWindow((HWND)window);

    public static bool IsVisible(nint window) => PInvoke.IsWindowVisible((HWND)window);

    public static nint Send(nint window, uint message, nuint wParam, nint lParam) =>
        PInvoke.SendMessage((HWND)window, message, wParam, lParam).Value;

    public static bool Post(nint window, uint message, nuint wParam, nint lParam) =>
        PInvoke.PostMessage((HWND)window, message, wParam, lParam);

    public static PixelRect WindowRect(nint window)
    {
        using PerMonitorDpiScope scope = PerMonitorDpiScope.Enter();
        if (!PInvoke.GetWindowRect((HWND)window, out RECT rect))
        {
            throw new InvalidOperationException("GetWindowRect failed.");
        }
        return new PixelRect(rect.left, rect.top, rect.right, rect.bottom);
    }

    public static (byte Alpha, LAYERED_WINDOW_ATTRIBUTES_FLAGS Flags) LayeredAttributes(nint window)
    {
        COLORREF key;
        byte alpha;
        LAYERED_WINDOW_ATTRIBUTES_FLAGS flags;
        if (!PInvoke.GetLayeredWindowAttributes((HWND)window, &key, &alpha, &flags))
        {
            throw new InvalidOperationException("GetLayeredWindowAttributes failed.");
        }
        return (alpha, flags);
    }

    public static nint MakeLParam(int low, int high) => (nint)(((high & 0xFFFF) << 16) | (low & 0xFFFF));
}
