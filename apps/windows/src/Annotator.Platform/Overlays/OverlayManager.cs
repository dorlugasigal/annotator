using System.Collections.ObjectModel;
using Annotator.Platform.Displays;
using Annotator.Platform.Windowing;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using SystemDispatcherQueue = Windows.System.DispatcherQueue;

namespace Annotator.Platform.Overlays;

/// <summary>
/// Owns one <see cref="OverlayWindow"/> per display and switches their input mode together.
/// Create it on a thread with a Windows.System dispatcher queue (for example a
/// <see cref="Threading.DispatcherThread"/>) and use it only there.
/// </summary>
/// <remarks>
/// <para>Use apps makes every overlay click-through in one pass, so no display keeps input
/// alone (WIN-OVR-02). Draw makes them take pointer input; a click on a canvas activates it,
/// and <see cref="SetInputMode"/> can activate the target display's overlay and remember the
/// previously active window (WIN-OVR-03). Leaving Draw gives focus back to that window if
/// Annotator still has the focus.</para>
/// <para>While overlays are visible, an out-of-context foreground WinEvent hook restores
/// Annotator's stacking order once per foreground change; there is no hook while hidden and
/// never a timer (WIN-OVR-04, WIN-SYS-07).</para>
/// <para>A display, scale or work-area change raises <see cref="DisplaysChanging"/> while the
/// old overlays still exist, so the app can cancel pending edits, then rebuilds the overlays,
/// leaves Draw for Use apps and raises <see cref="OverlaysRebuilt"/> (WIN-OVR-05).</para>
/// </remarks>
public sealed class OverlayManager : IDisposable
{
    private readonly WindowRegistry _registry;
    private readonly IOverlayInputSink _sink;
    private readonly bool _ownsCompositor;
    private readonly bool _restoresStackOrder;
    private readonly DisplayWatcher _watcher;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private OverlayWindow[] _overlays = [];
    private ReadOnlyCollection<OverlayWindow> _overlaysView = ReadOnlyCollection<OverlayWindow>.Empty;
    private ForegroundHook? _foregroundHook;
    private VirtualDesktopTracker? _desktops;
    private bool _desktopsUnavailable;
    private HWND _previousForeground;
    private OverlayInputMode _mode;
    private bool _disposed;

    private OverlayManager(OverlayManagerOptions options, Compositor compositor, bool ownsCompositor)
    {
        _registry = options.Registry;
        _sink = options.InputSink;
        Compositor = compositor;
        _ownsCompositor = ownsCompositor;
        _restoresStackOrder = options.RestoresStackOrder;
        _watcher = options.DisplaySource is null ? new DisplayWatcher() : new DisplayWatcher(options.DisplaySource);
        _watcher.Changed += OnDisplaysChanged;
        _watcher.RefreshFailed += OnDisplayRefreshFailed;
    }

    /// <summary>Raised on the overlay thread before overlays are rebuilt; the old overlays still exist.</summary>
    public event EventHandler<DisplaysChangedEventArgs>? DisplaysChanging;

    /// <summary>Raised on the overlay thread after the overlays were rebuilt; attach content to the new roots.</summary>
    public event EventHandler<OverlaysRebuiltEventArgs>? OverlaysRebuilt;

    /// <summary>Raised on the overlay thread when background work fails, such as restoring the stacking order.</summary>
    public event EventHandler<OverlayFailureEventArgs>? Failed;

    /// <summary>
    /// Raised on the overlay thread when the person switched virtual desktops while the overlays were visible (WIN-OVR-06),
    /// noticed from the foreground change that comes with the switch (<see cref="VirtualDesktopTracker"/>).
    /// </summary>
    public event EventHandler? VirtualDesktopSwitched;

    /// <summary>The compositor that hosts every overlay's visual tree.</summary>
    public Compositor Compositor { get; }

    public IReadOnlyList<OverlayWindow> Overlays
    {
        get
        {
            VerifyAccess();
            return _overlaysView;
        }
    }

    public IReadOnlyList<DisplayInfo> Displays
    {
        get
        {
            VerifyAccess();
            return _watcher.Displays;
        }
    }

    public OverlayInputMode InputMode
    {
        get
        {
            VerifyAccess();
            return _mode;
        }
    }

    internal DisplayWatcher Watcher => _watcher;

    /// <summary>True while the foreground WinEvent hook exists (only while overlays are visible).</summary>
    internal bool IsWatchingForeground => _foregroundHook is not null;

    /// <summary>Creates the overlays for every display in <see cref="OverlayManagerOptions.InitialMode"/>.</summary>
    public static OverlayManager Create(OverlayManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Registry);
        ArgumentNullException.ThrowIfNull(options.InputSink);
        if (!Enum.IsDefined(options.InitialMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.InitialMode, "Unknown overlay input mode.");
        }
        if (SystemDispatcherQueue.GetForCurrentThread() is null)
        {
            throw new InvalidOperationException(
                "Create overlays on a thread with a Windows.System dispatcher queue, such as a DispatcherThread. " +
                "Windows.UI.Composition cannot create a compositor without one (Gate 0 S-12).");
        }

        bool ownsCompositor = options.Compositor is null;
        Compositor compositor = options.Compositor ?? new Compositor();
        OverlayManager? manager = null;
        try
        {
            manager = new OverlayManager(options, compositor, ownsCompositor);
            manager._mode = options.InitialMode;
            manager.ReplaceOverlays(manager.BuildOverlays(manager._watcher.Displays, options.InitialMode, throwOnFailure: true));
            manager.UpdateForegroundHook();
            if (options.InitialMode != OverlayInputMode.Hidden)
            {
                manager.RestoreStackOrder();
            }
            return manager;
        }
        catch
        {
            if (manager is not null)
            {
                manager.Dispose();
            }
            else if (ownsCompositor)
            {
                compositor.Dispose();
            }
            throw;
        }
    }

    /// <summary>
    /// Switches every overlay to <paramref name="mode"/> at once. With Draw,
    /// <paramref name="activate"/> names the target display's overlay to bring to the foreground.
    /// Leaving Draw returns focus to the window that was active before, when Annotator still has it.
    /// </summary>
    public InputModeChange SetInputMode(OverlayInputMode mode, OverlayWindow? activate = null)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown overlay input mode.");
        }
        if (activate is not null)
        {
            if (mode != OverlayInputMode.Draw)
            {
                throw new ArgumentException("Only Draw activates an overlay.", nameof(activate));
            }
            if (Array.IndexOf(_overlays, activate) < 0)
            {
                throw new ArgumentException("The overlay no longer belongs to this manager; the displays changed.", nameof(activate));
            }
        }

        OverlayInputMode previous = _mode;
        FocusChange focus = previous == OverlayInputMode.Draw && mode != OverlayInputMode.Draw
            ? RestorePreviousFocus()
            : FocusChange.Unchanged;

        try
        {
            foreach (OverlayWindow overlay in _overlays)
            {
                overlay.ApplyMode(mode);
            }
        }
        catch
        {
            // Never leave some displays in another mode than the rest (WIN-OVR-02).
            foreach (OverlayWindow overlay in _overlays)
            {
                overlay.TryApplyMode(previous);
            }
            throw;
        }

        _mode = mode;
        UpdateForegroundHook();
        if (previous == OverlayInputMode.Hidden && mode != OverlayInputMode.Hidden)
        {
            RestoreStackOrder();
        }
        if (activate is not null)
        {
            focus = Activate(activate);
        }
        return new InputModeChange(previous, mode, focus);
    }

    /// <summary>Reads the displays again, for example after resuming from sleep, and rebuilds if they changed.</summary>
    public bool RefreshDisplays()
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _watcher.Refresh();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        VerifyAccess();
        _disposed = true;
        try
        {
            _foregroundHook?.Dispose();
            _foregroundHook = null;
            _desktops?.Dispose();
            _desktops = null;
            DestroyOverlays();
        }
        finally
        {
            _watcher.Changed -= OnDisplaysChanged;
            _watcher.RefreshFailed -= OnDisplayRefreshFailed;
            _watcher.Dispose();
            if (_ownsCompositor)
            {
                Compositor.Dispose();
            }
        }
    }

    internal void OnOverlayDpiChanged()
    {
        if (!_disposed)
        {
            _watcher.Schedule(DisplayChangeReasons.Dpi);
        }
    }

    internal void ReportFailure(NativeCallException failure) =>
        Failed?.Invoke(this, new OverlayFailureEventArgs(failure.Operation, failure));

    private List<OverlayWindow> BuildOverlays(IReadOnlyList<DisplayInfo> displays, OverlayInputMode mode, bool throwOnFailure)
    {
        var overlays = new List<OverlayWindow>(displays.Count);
        try
        {
            foreach (DisplayInfo display in displays)
            {
                try
                {
                    overlays.Add(OverlayWindow.Create(this, Compositor, display, mode, _registry, _sink));
                }
                catch (NativeCallException failure) when (!throwOnFailure)
                {
                    // Keep the other displays covered; the app learns which one failed.
                    Failed?.Invoke(this, new OverlayFailureEventArgs($"Create the overlay for {display.DeviceName}", failure));
                }
            }
        }
        catch
        {
            foreach (OverlayWindow overlay in overlays)
            {
                overlay.Destroy();
            }
            throw;
        }
        return overlays;
    }

    private void ReplaceOverlays(List<OverlayWindow> overlays)
    {
        _overlays = [.. overlays];
        _overlaysView = Array.AsReadOnly(_overlays);
    }

    private void DestroyOverlays()
    {
        OverlayWindow[] overlays = _overlays;
        ReplaceOverlays([]);
        List<Exception>? failures = null;
        foreach (OverlayWindow overlay in overlays)
        {
            try
            {
                overlay.Destroy();
            }
            catch (NativeCallException failure)
            {
                (failures ??= []).Add(failure);
            }
        }
        if (failures is not null)
        {
            throw new AggregateException("Some overlays could not be destroyed.", failures);
        }
    }

    private void OnDisplaysChanged(object? sender, DisplaysChangedEventArgs change)
    {
        if (_disposed)
        {
            return;
        }
        DisplaysChanging?.Invoke(this, change);

        OverlayInputMode previous = _mode;
        OverlayInputMode next = previous == OverlayInputMode.Draw ? OverlayInputMode.UseApps : previous;
        FocusChange focus = previous == OverlayInputMode.Draw ? RestorePreviousFocus() : FocusChange.Unchanged;
        try
        {
            DestroyOverlays();
        }
        catch (AggregateException failure)
        {
            Failed?.Invoke(this, new OverlayFailureEventArgs("Destroy the overlays of the previous displays", failure));
        }

        _mode = next;
        ReplaceOverlays(BuildOverlays(change.Current, next, throwOnFailure: false));
        UpdateForegroundHook();
        if (next != OverlayInputMode.Hidden)
        {
            RestoreStackOrder();
        }
        OverlaysRebuilt?.Invoke(this, new OverlaysRebuiltEventArgs(change, new InputModeChange(previous, next, focus), _overlaysView));
    }

    private void OnDisplayRefreshFailed(object? sender, DisplayRefreshFailedEventArgs failure) =>
        Failed?.Invoke(this, new OverlayFailureEventArgs(failure.Error.Operation, failure.Error));

    private FocusChange Activate(OverlayWindow overlay)
    {
        HWND foreground = PInvoke.GetForegroundWindow();
        if (!foreground.IsNull && !_registry.Contains((nint)foreground))
        {
            _previousForeground = foreground;
        }
        if (PInvoke.SetForegroundWindow(overlay.Hwnd) || TakeForeground(overlay.Hwnd, foreground))
        {
            return FocusChange.ActivatedOverlay;
        }
        return FocusChange.ActivationRefused;
    }

    /// <summary>
    /// Windows refuses <c>SetForegroundWindow</c> once the person types or clicks after the shortcut that started Draw,
    /// for example during the show animation. Sharing the foreground thread's input state lets the overlay take focus
    /// anyway, as the shortcut asked for it.
    /// </summary>
    private static unsafe bool TakeForeground(HWND window, HWND foreground)
    {
        if (foreground.IsNull)
        {
            return false;
        }
        uint foregroundThread = PInvoke.GetWindowThreadProcessId(foreground, null);
        uint thread = PInvoke.GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == thread || !PInvoke.AttachThreadInput(thread, foregroundThread, true))
        {
            return false;
        }
        try
        {
            return PInvoke.SetForegroundWindow(window);
        }
        finally
        {
            PInvoke.AttachThreadInput(thread, foregroundThread, false);
        }
    }

    private FocusChange RestorePreviousFocus()
    {
        HWND previous = _previousForeground;
        _previousForeground = HWND.Null;
        HWND foreground = PInvoke.GetForegroundWindow();
        if (previous.IsNull || foreground.IsNull || !_registry.Contains((nint)foreground))
        {
            // Nothing remembered, or the person already moved to another app: leave focus alone.
            return FocusChange.Unchanged;
        }
        if (!PInvoke.IsWindow(previous))
        {
            return FocusChange.PreviousWindowGone;
        }
        return PInvoke.SetForegroundWindow(previous) ? FocusChange.RestoredPreviousWindow : FocusChange.RestoreRefused;
    }

    private void UpdateForegroundHook()
    {
        bool needed = !_disposed && _restoresStackOrder && _mode != OverlayInputMode.Hidden && _overlays.Length > 0;
        if (needed && _foregroundHook is null)
        {
            _foregroundHook = ForegroundHook.Install(OnForegroundChanged);
            StartDesktopTracking();
        }
        else if (!needed && _foregroundHook is not null)
        {
            _foregroundHook.Dispose();
            _foregroundHook = null;
        }
    }

    /// <summary>Another app's window came to the foreground: restore the order, and notice a virtual desktop switch.</summary>
    private void OnForegroundChanged(HWND foreground)
    {
        RestoreStackOrder();
        if (_desktops is { } desktops && desktops.Switched((nint)foreground))
        {
            VirtualDesktopSwitched?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Starts noticing virtual desktop switches from the window of another app that is in front now (or was, before an
    /// overlay took the focus). Without a virtual desktop manager the failure is reported once and switches go unnoticed.
    /// </summary>
    private void StartDesktopTracking()
    {
        if (_desktopsUnavailable)
        {
            return;
        }
        try
        {
            _desktops ??= VirtualDesktopTracker.Create();
        }
        catch (NativeCallException failure)
        {
            _desktopsUnavailable = true;
            ReportFailure(failure);
            return;
        }
        HWND foreground = PInvoke.GetForegroundWindow();
        _desktops.Reset(_registry.Contains((nint)foreground) ? (nint)_previousForeground : (nint)foreground);
    }

    private void RestoreStackOrder()
    {
        foreach (NativeCallException failure in StackOrder.Restore(_registry))
        {
            ReportFailure(failure);
        }
    }

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
        {
            throw new InvalidOperationException("OverlayManager must be used on the overlay thread that created it.");
        }
    }
}
