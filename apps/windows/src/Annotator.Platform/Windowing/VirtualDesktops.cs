using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Annotator.Platform.Windowing;

/// <summary>
/// Notices virtual desktop switches (WIN-OVR-06). Windows tells apps nothing about them, so on each foreground change the
/// tracker asks whether the last foreground window of another app is still on the current virtual desktop
/// (IVirtualDesktopManager.IsWindowOnCurrentVirtualDesktop). When it is not, the person switched desktops: Win+Ctrl+arrow,
/// Task View or activating a window on another desktop. A window shown on every desktop never counts as left behind.
/// Nothing runs between foreground changes.
/// </summary>
internal sealed unsafe partial class VirtualDesktopTracker : IDisposable
{
    private static readonly Guid ClsidVirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    private static readonly Guid IidVirtualDesktopManager = new("A5CD92FF-29BE-454C-8D04-D82879FB3F1B");
    private const uint ClsctxAll = 0x17;

    // IVirtualDesktopManager after IUnknown: IsWindowOnCurrentVirtualDesktop, GetWindowDesktopId, MoveWindowToDesktop.
    private const int IsOnCurrentSlot = 3;
    private const int DesktopIdSlot = 4;

    private readonly Func<nint, bool?> _onCurrentDesktop;
    private readonly Func<nint, bool> _belongsToDesktop;
    private nint _manager;
    private nint _reference;

    internal VirtualDesktopTracker(Func<nint, bool?> onCurrentDesktop, Func<nint, bool> belongsToDesktop)
    {
        _onCurrentDesktop = onCurrentDesktop;
        _belongsToDesktop = belongsToDesktop;
    }

    private VirtualDesktopTracker(nint manager)
    {
        _manager = manager;
        _onCurrentDesktop = IsOnCurrentDesktop;
        _belongsToDesktop = HasDesktop;
    }

    /// <summary>Creates the tracker with the system's virtual desktop manager on the calling (COM) thread.</summary>
    /// <exception cref="NativeCallException">The virtual desktop manager is unavailable.</exception>
    public static VirtualDesktopTracker Create()
    {
        nint manager;
        Guid clsid = ClsidVirtualDesktopManager, iid = IidVirtualDesktopManager;
        NativeCallException.ThrowIfFailed(CoCreateInstance(&clsid, 0, ClsctxAll, &iid, &manager), "CoCreateInstance(VirtualDesktopManager)");
        return new VirtualDesktopTracker(manager);
    }

    /// <summary>Starts over from <paramref name="foreground"/>, for example when the overlays show again.</summary>
    public void Reset(nint foreground)
    {
        _reference = 0;
        Remember(foreground);
    }

    /// <summary>
    /// A window of another app became the foreground window. Returns true when the person left the virtual desktop of the
    /// last such window; the new window becomes the reference, and until one with a desktop of its own is active nothing
    /// more is reported (an empty desktop shows only the shell).
    /// </summary>
    public bool Switched(nint foreground)
    {
        bool switched = _reference != 0 && _onCurrentDesktop(_reference) == false;
        if (switched)
        {
            _reference = 0;
        }
        Remember(foreground);
        return switched;
    }

    public void Dispose()
    {
        if (_manager != 0)
        {
            Marshal.Release(_manager);
            _manager = 0;
        }
    }

    /// <summary>Keeps <paramref name="window"/> as the reference when it belongs to one virtual desktop.</summary>
    private void Remember(nint window)
    {
        if (window != 0 && _belongsToDesktop(window))
        {
            _reference = window;
        }
    }

    /// <summary>Whether the window is on the current virtual desktop; null when Windows cannot tell, for example after it closed.</summary>
    private bool? IsOnCurrentDesktop(nint window)
    {
        if (_manager == 0 || !PInvoke.IsWindow((HWND)window))
        {
            return null;
        }
        BOOL onCurrent;
        int result = ((delegate* unmanaged[Stdcall]<nint, nint, BOOL*, int>)(*(void***)_manager)[IsOnCurrentSlot])(_manager, window, &onCurrent);
        return result < 0 ? null : onCurrent;
    }

    /// <summary>Whether the window lives on a virtual desktop (not the shell's taskbar or desktop, which have none).</summary>
    private bool HasDesktop(nint window)
    {
        if (_manager == 0)
        {
            return false;
        }
        Guid desktop;
        int result = ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, int>)(*(void***)_manager)[DesktopIdSlot])(_manager, window, &desktop);
        return result >= 0 && desktop != Guid.Empty;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);
}
