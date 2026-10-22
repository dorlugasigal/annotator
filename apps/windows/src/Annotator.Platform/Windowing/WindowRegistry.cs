using Windows.Win32;
using Windows.Win32.Foundation;

namespace Annotator.Platform.Windowing;

/// <summary>
/// Annotator's stacking bands inside the topmost band, bottom to top (WIN-OVR-04): canvas, then
/// effects, selection adornments, HUD and toolbar, then popovers and error windows, then the region sharing
/// guides (the Mac's screen-saver level). <see cref="Ordinary"/> windows are not topmost and keep their own place.
/// </summary>
public enum WindowLayer
{
    /// <summary>
    /// An ordinary window that other apps can cover, such as the shared region output (WIN-CAP-08): capture leaves it out,
    /// but the stacking order never moves it.
    /// </summary>
    Ordinary = -1,

    Canvas = 0,
    Effects = 1,
    Adornments = 2,
    Hud = 3,
    Toolbar = 4,
    Popover = 5,
    Error = 6,

    /// <summary>The region sharing outline and selection panels, above every other Annotator window (Mac screen-saver level).</summary>
    Guide = 7,
}

/// <summary>An Annotator window known to the <see cref="WindowRegistry"/>.</summary>
/// <param name="ThreadId">The Win32 thread that owns the window.</param>
public sealed record RegisteredWindow(nint Handle, WindowLayer Layer, uint ThreadId);

public enum WindowRegistryChange
{
    Added,
    Removed,
}

public sealed class WindowRegistryChangedEventArgs(WindowRegistryChange change, RegisteredWindow window, IReadOnlyList<RegisteredWindow> windows) : EventArgs
{
    public WindowRegistryChange Change { get; } = change;

    public RegisteredWindow Window { get; } = window;

    /// <summary>Every registered window after this change, in registration order.</summary>
    public IReadOnlyList<RegisteredWindow> Windows { get; } = windows;
}

/// <summary>
/// The central list of Annotator windows that can appear on screen. Screen capture leaves these
/// windows out (Gate 0 S-1). The Magnification API leaves out only windows that have been shown at
/// least once, so the capture service applies its list again inside the call that first shows a
/// registered window, before the window paints; Platform's own overlays are shown once while cloaked
/// before they register. Register each window right after creating it and before showing it;
/// <see cref="Changed"/> runs synchronously on the registering thread so a listener can update
/// the exclusion in the same message-loop turn. Hidden helper windows that never paint, such as
/// message-only windows, are not registered.
/// </summary>
public sealed class WindowRegistry
{
    private readonly Lock _gate = new();
    private RegisteredWindow[] _windows = [];

    /// <summary>
    /// Raised synchronously on the thread that registered or unregistered the window. A
    /// handler that throws makes <see cref="Register"/> fail, so the window is never shown
    /// without its listeners knowing.
    /// </summary>
    public event EventHandler<WindowRegistryChangedEventArgs>? Changed;

    /// <summary>A snapshot in registration order.</summary>
    public IReadOnlyList<RegisteredWindow> Windows => Volatile.Read(ref _windows);

    public bool Contains(nint handle) => Array.Exists(Volatile.Read(ref _windows), window => window.Handle == handle);

    /// <summary>Registers a window of this process. Dispose the registration when the window is destroyed.</summary>
    public WindowRegistration Register(nint handle, WindowLayer layer)
    {
        var hwnd = (HWND)handle;
        if (handle == 0 || !PInvoke.IsWindow(hwnd))
        {
            throw new ArgumentException("The handle is not a window.", nameof(handle));
        }
        if (!Enum.IsDefined(layer))
        {
            throw new ArgumentOutOfRangeException(nameof(layer), layer, "Unknown window layer.");
        }
        uint threadId = PInvoke.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId != (uint)Environment.ProcessId)
        {
            throw new ArgumentException("Only Annotator's own windows can be registered.", nameof(handle));
        }

        var window = new RegisteredWindow(handle, layer, threadId);
        RegisteredWindow[] snapshot;
        lock (_gate)
        {
            if (Array.Exists(_windows, existing => existing.Handle == handle))
            {
                throw new InvalidOperationException($"Window 0x{handle:X} is already registered.");
            }
            snapshot = [.. _windows, window];
            Volatile.Write(ref _windows, snapshot);
        }

        var registration = new WindowRegistration(this, window);
        try
        {
            Changed?.Invoke(this, new WindowRegistryChangedEventArgs(WindowRegistryChange.Added, window, snapshot));
        }
        catch
        {
            Remove(window, notify: false);
            throw;
        }
        return registration;
    }

    internal void Remove(RegisteredWindow window, bool notify)
    {
        RegisteredWindow[] snapshot;
        lock (_gate)
        {
            int index = Array.IndexOf(_windows, window);
            if (index < 0)
            {
                return;
            }
            snapshot = [.. _windows[..index], .. _windows[(index + 1)..]];
            Volatile.Write(ref _windows, snapshot);
        }
        if (notify)
        {
            Changed?.Invoke(this, new WindowRegistryChangedEventArgs(WindowRegistryChange.Removed, window, snapshot));
        }
    }
}

/// <summary>Keeps a window in the <see cref="WindowRegistry"/> until disposed.</summary>
public sealed class WindowRegistration : IDisposable
{
    private readonly WindowRegistry _registry;
    private int _disposed;

    internal WindowRegistration(WindowRegistry registry, RegisteredWindow window)
    {
        _registry = registry;
        Window = window;
    }

    public RegisteredWindow Window { get; }

    /// <summary>Removes the window and raises <see cref="WindowRegistry.Changed"/> once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _registry.Remove(Window, notify: true);
        }
    }
}
