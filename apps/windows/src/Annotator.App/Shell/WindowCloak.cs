using System.Diagnostics;
using System.Runtime.InteropServices;
using Annotator.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.App.Shell;

/// <summary>
/// Shows and hides an app window so that it takes the first click a person aims at it (2026-10-20). Windows routes a WinUI
/// window's pointer input to its content only once the content is on screen; until then a press reaches the window's
/// <c>Microsoft.UI.Content.DesktopChildSiteBridge</c> child window, which does nothing with it. A window that shows anew counts
/// as shown at once but draws its content 40 to 140 ms later while the GPU is busy, and a window shown again after a real hide
/// needs a while too. So the window is cloaked (DWMWA_CLOAK) whenever it should not be seen: Windows counts it as shown and
/// its content renders and keeps its input, while DWM draws it nowhere, <c>WindowFromPoint</c> passes through it and UI
/// Automation leaves it out.
/// <list type="bullet">
/// <item>The first showing happens cloaked; once the content has rendered a frame and the compositor has taken it
/// (<c>RequestCommitAsync</c>), the window uncloaks and DWM shows it with its content in its next frame, about when it would
/// have drawn without the cloak. At most <see cref="FirstFrameLimit"/> passes before it uncloaks.</item>
/// <item>Hiding cloaks the window in place, and a window with a taskbar button leaves the taskbar and Alt+Tab until it shows
/// again. A window that has the keyboard hides for real first, so Windows gives the keyboard to the window it chooses, and
/// then shows again cloaked without activation. A window that DWM cannot cloak hides as before.</item>
/// <item>A hidden, cloaked window that Windows activates anyway hides for real, so no hidden window keeps the keyboard.</item>
/// </list>
/// </summary>
internal sealed class WindowCloak
{
    /// <summary>The longest a first showing stays cloaked while its content draws; then it shows as it is.</summary>
    public static readonly TimeSpan FirstFrameLimit = TimeSpan.FromMilliseconds(500);

    private readonly Window _window;
    private readonly HWND _hwnd;
    private readonly string _name;
    private bool _cloaked;
    private bool _shownBefore;
    private bool _hidden = true;
    private bool _waiting;
    /// <summary>The window had a taskbar button and Alt+Tab entry when it hid; it gets them back when it shows.</summary>
    private bool _restoresSwitchers;
    private long _shownAt;
    private DispatcherQueueTimer? _limit;

    /// <param name="name">The window's name in error messages and measurement notes, for example "the Tools window".</param>
    public WindowCloak(Window window, HWND hwnd, string name)
    {
        _window = window;
        _hwnd = hwnd;
        _name = name;
        window.Activated += OnActivated;
        window.Closed += (_, _) => StopWaiting();
    }

    /// <summary>
    /// Call just before the window shows (AppWindow.Show): the first showing starts cloaked until its content has drawn; a
    /// window hidden by cloaking uncloaks.
    /// </summary>
    public void BeforeShow()
    {
        _hidden = false;
        if (!_shownBefore)
        {
            _shownBefore = true;
            if (SetCloaked(true).Succeeded)
            {
                WaitForFirstFrame();
            }
            return;
        }
        if (_restoresSwitchers)
        {
            _restoresSwitchers = false;
            _window.AppWindow.IsShownInSwitchers = true;
        }
        if (_cloaked && !_waiting)
        {
            Uncloak();
        }
    }

    /// <summary>
    /// Hides the window: cloaked in place while it does not have the keyboard; otherwise hidden for real, so Windows moves the
    /// keyboard on, and shown again cloaked without activation. A cloaked window loses its taskbar button and Alt+Tab entry
    /// until it shows again, as a hidden one does. Hides for real when DWM cannot cloak it.
    /// </summary>
    public void Hide()
    {
        _hidden = true;
        StopWaiting();
        bool keyboard = PInvoke.GetForegroundWindow() == _hwnd;
        if (!keyboard && (_cloaked || SetCloaked(true).Succeeded))
        {
            LeaveSwitchers();
            Note("hide", "cloaked");
            return;
        }
        _window.AppWindow.Hide();
        if (keyboard && (_cloaked || SetCloaked(true).Succeeded))
        {
            // Shown again for Windows only, so the content keeps its input for the next showing.
            LeaveSwitchers();
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNA);
            Note("hide", "hidden, then shown cloaked");
            return;
        }
        Note("hide", "hidden");
    }

    /// <summary>A cloaked window keeps its taskbar button unless it leaves the switchers while it is hidden.</summary>
    private void LeaveSwitchers()
    {
        if (_window.AppWindow.IsShownInSwitchers)
        {
            _window.AppWindow.IsShownInSwitchers = false;
            _restoresSwitchers = true;
        }
    }

    private void WaitForFirstFrame()
    {
        _waiting = true;
        _shownAt = Stopwatch.GetTimestamp();
        // The content renders its first frame once it is loaded; Rendered follows each frame of this thread's windows.
        CompositionTarget.Rendered += OnRendered;
        _limit = _window.DispatcherQueue.CreateTimer();
        _limit.Interval = FirstFrameLimit;
        _limit.IsRepeating = false;
        _limit.Tick += (_, _) => Reveal("late");
        _limit.Start();
    }

    private void OnRendered(object? sender, RenderedEventArgs args)
    {
        if (_window.Content is FrameworkElement { IsLoaded: true })
        {
            CompositionTarget.Rendered -= OnRendered;
            _ = RevealWhenCommittedAsync();
        }
    }

    /// <summary>Uncloaks once the compositor has taken the frame with the content, so DWM's next frame shows it.</summary>
    private async Task RevealWhenCommittedAsync()
    {
        try
        {
            await _window.Compositor.RequestCommitAsync();
        }
        catch (Exception error) when (error is COMException or ObjectDisposedException or InvalidOperationException)
        {
            // The window closed or its compositor went: the window shows (or hides) as it is.
            Note("commit", error.Message);
        }
        Reveal("drawn");
    }

    private void Reveal(string why)
    {
        if (!_waiting)
        {
            return;
        }
        StopWaiting();
        Note("reveal", FormattableString.Invariant($"{why} after {Stopwatch.GetElapsedTime(_shownAt).TotalMilliseconds:F0} ms"));
        if (!_hidden)
        {
            Uncloak();
        }
    }

    private void StopWaiting()
    {
        if (!_waiting)
        {
            return;
        }
        _waiting = false;
        CompositionTarget.Rendered -= OnRendered;
        _limit?.Stop();
        _limit = null;
    }

    /// <summary>Shows the cloaked window on screen; a refusal by DWM is reported.</summary>
    private void Uncloak()
    {
        HRESULT result = SetCloaked(false);
        if (result.Failed)
        {
            Native.CallbackErrors.Report(new NativeCallException($"DwmSetWindowAttribute(DWMWA_CLOAK off) for {_name}", result.Value));
        }
    }

    /// <summary>A cloaked window that Windows activates while it is hidden hides for real, so no hidden window keeps the keyboard.</summary>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_cloaked && _hidden && args.WindowActivationState != WindowActivationState.Deactivated)
        {
            Note("activated", "hidden for real");
            _window.AppWindow.Hide();
        }
    }

    /// <summary>Cloaks or uncloaks the window; <see cref="_cloaked"/> follows what DWM accepted.</summary>
    private unsafe HRESULT SetCloaked(bool cloaked)
    {
        BOOL value = cloaked;
        HRESULT result = PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, (uint)sizeof(BOOL));
        if (result.Succeeded)
        {
            _cloaked = cloaked;
        }
        return result;
    }

    /// <summary>Measurement notes (ANNOTATOR_STARTUP_LOG); nothing is written otherwise.</summary>
    private void Note(string what, string detail) => StartupLog.Note("cloak", $"{_name} {what}: {detail}");
}
