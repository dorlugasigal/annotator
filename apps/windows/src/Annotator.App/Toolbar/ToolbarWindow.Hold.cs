using Annotator.Core;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using CorePoint = Annotator.Core.Point;

namespace Annotator.App.Toolbar;

/// <summary>
/// Press and hold anywhere on the toolbar to move it (Mac InspectorPanel's holdPress, <see cref="ToolbarHold"/>): on a
/// button, a name or empty space, also on the toolbar shrunk to one button. A left press that stays within 3 points for 0.4
/// seconds picks the toolbar up: the background's drag starts from where the press started, and the control under the
/// pointer loses the pointer to the toolbar, so it never clicks. Letting go or moving further sooner leaves the press to
/// the control as usual, and the background's own drag still starts as soon as it moves. Sliders and fields get their
/// press at once. Windows hands the press to the control right away, where the Mac holds it back, so a held button looks
/// pressed until the toolbar is picked up. At the 0.4-second mark the button's own state decides, so a release that hasn't
/// reached the toolbar yet still clicks. Escape ends a move where the toolbar is, as on the Mac.
/// </summary>
internal sealed partial class ToolbarWindow
{
    private readonly ToolbarHoldGesture _hold = new();
    private DispatcherQueueTimer? _holdTimer;
    /// <summary>The pointer of the press that waits for the hold.</summary>
    private Pointer? _holdPointer;
    /// <summary>Where that press started, in desktop pixels.</summary>
    private CorePoint _holdPress;
    /// <summary>The element the press landed on.</summary>
    private DependencyObject? _holdSource;
    private Annotator.Platform.Hotkeys.HotkeyService? _escapeService;
    private Annotator.Platform.Hotkeys.HotkeyId? _escape;

    /// <summary>Watches every press on the toolbar, including those its controls handle.</summary>
    private void WatchHold()
    {
        _root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnHoldPressed), handledEventsToo: true);
        _root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnHoldMoved), handledEventsToo: true);
        _root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnHoldReleased), handledEventsToo: true);
        _root.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnHoldCanceled), handledEventsToo: true);
        _root.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnHoldCaptureLost), handledEventsToo: true);
    }

    /// <summary>Mac allowsHoldToMove with isVisible: the toolbar shows, rests and isn't being dragged.</summary>
    private bool HoldAllowed() => _shown && !_closing && Presented() && !_docking.IsDragging;

    private void OnHoldPressed(object sender, PointerRoutedEventArgs args)
    {
        PointerPoint point = args.GetCurrentPoint(null);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }
        StopHoldTimer();
        _holdPointer = null;
        if (!_hold.Press(args.Pointer.PointerId, new CorePoint(point.Position.X, point.Position.Y), HoldAllowed(), PressTargets(args.OriginalSource)))
        {
            return;
        }
        _holdPointer = args.Pointer;
        _holdSource = args.OriginalSource as DependencyObject;
        _holdPress = EventPoint(args);
        _holdTimer ??= CreateHoldTimer();
        _holdTimer.Start();
    }

    private void OnHoldMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_hold.IsWaiting)
        {
            Windows.Foundation.Point position = args.GetCurrentPoint(null).Position;
            if (_hold.Move(args.Pointer.PointerId, new CorePoint(position.X, position.Y)) == ToolbarHoldMove.Moved)
            {
                StopHoldTimer();
                _holdPointer = null;
            }
        }
        if (ReferenceEquals(_dragSurface, _root))
        {
            OnPointerMoved(_root, args);
        }
    }

    private void OnHoldReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_hold.Release(args.Pointer.PointerId))
        {
            StopHoldTimer();
            _holdPointer = null;
        }
        if (ReferenceEquals(_dragSurface, _root))
        {
            OnPointerReleased(_root, args);
        }
    }

    private void OnHoldCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_hold.Release(args.Pointer.PointerId))
        {
            StopHoldTimer();
            _holdPointer = null;
        }
        if (ReferenceEquals(_dragSurface, _root) && _docking.IsTracking)
        {
            CancelDrag();
        }
    }

    /// <summary>
    /// The control that had the press lost the pointer before the hold ended, so the press is over; or the toolbar lost the
    /// pointer it took for the move, which ends the move where the toolbar is. The control's own loss when the toolbar
    /// takes the pointer changes nothing.
    /// </summary>
    private void OnHoldCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_hold.Release(args.Pointer.PointerId))
        {
            StopHoldTimer();
            _holdPointer = null;
        }
        if (ReferenceEquals(_dragSurface, _root) && _docking.IsTracking && !Captures(_root, args.Pointer.PointerId))
        {
            CancelDrag();
        }
    }

    /// <summary>
    /// Mac finishHold: the press stayed still for the delay. A press on the background becomes its drag at once; a press on
    /// a control moves the pointer to the toolbar, so the control never clicks, and the toolbar follows it from the press.
    /// </summary>
    private void OnHoldElapsed()
    {
        StopHoldTimer();
        Pointer? pointer = _holdPointer;
        _holdPointer = null;
        // A release that arrives at the same instant as the timer has not reached the toolbar yet: the button's own state
        // decides, so a click that lasted just 0.4 seconds still clicks.
        bool down = pointer is not null && PrimaryButtonDown();
        if (_hold.Elapse(buttonDown: down, HoldAllowed()) is null || !down)
        {
            return;
        }
        if (!_docking.IsTracking)
        {
            // The control under the press took the pointer when it was pressed, and WinUI gives a captured pointer to no
            // one else: it lets go first, which ends its press without a click.
            ReleaseControlCapture(pointer!);
            if (!_root.CapturePointer(pointer))
            {
                // The press is no longer down here: it stays the control's.
                return;
            }
            _dragSurface = _root;
            _dragAvailable = Available();
            _dragDisplays = Displays();
            _dockingSizes.Clear();
            _ghostFailed = false;
        }
        _interacted();
        _docking.Hold(_holdPress, ToCore(Frame), Scale());
        _root.ShowMoveCursor(true);
        TakeEscape();
    }

    /// <summary>The element between the press and the toolbar that holds <paramref name="pointer"/> lets go of it.</summary>
    private void ReleaseControlCapture(Pointer pointer)
    {
        DependencyObject? element = _holdSource;
        _holdSource = null;
        for (; element is not null && !ReferenceEquals(element, _root); element = VisualTreeHelper.GetParent(element))
        {
            if (element is UIElement control && Captures(control, pointer.PointerId))
            {
                control.ReleasePointerCapture(pointer);
                return;
            }
        }
    }

    /// <summary>Whether the left button (the primary one, even when the buttons are swapped) is down now.</summary>
    private static bool PrimaryButtonDown()
    {
        const int SwapButton = 23;
        Windows.Win32.UI.Input.KeyboardAndMouse.VIRTUAL_KEY key = Windows.Win32.PInvoke.GetSystemMetrics((Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX)SwapButton) != 0
            ? Windows.Win32.UI.Input.KeyboardAndMouse.VIRTUAL_KEY.VK_RBUTTON
            : Windows.Win32.UI.Input.KeyboardAndMouse.VIRTUAL_KEY.VK_LBUTTON;
        return (Windows.Win32.PInvoke.GetAsyncKeyState((int)key) & 0x8000) != 0;
    }

    /// <summary>
    /// Mac cancelToolbarDrag on Escape: the toolbar never takes the keyboard, so while it moves Escape is taken for the
    /// toolbar alone and ends the move where the toolbar is. It goes back to the other apps as soon as the move ends, and
    /// the app in front never gets that Escape (a slide show keeps running).
    /// </summary>
    private void TakeEscape()
    {
        if (_escape is not null)
        {
            return;
        }
        try
        {
            _escapeService ??= CreateEscapeService();
            if (_escapeService.Register(new Annotator.Platform.Hotkeys.HotkeyChord(default, 0x1B)) is
                Annotator.Platform.Hotkeys.HotkeyRegistrationResult.Registered registered)
            {
                _escape = registered.Id;
            }
        }
        catch (Annotator.Platform.NativeCallException)
        {
            // Without it Escape stays the other apps'; the move still ends when the button goes up.
        }
    }

    private void ReleaseEscape()
    {
        if (_escape is not { } id || _escapeService is null)
        {
            return;
        }
        _escape = null;
        try
        {
            _escapeService.Unregister(id);
        }
        catch (Annotator.Platform.NativeCallException)
        {
        }
    }

    private Annotator.Platform.Hotkeys.HotkeyService CreateEscapeService()
    {
        var service = new Annotator.Platform.Hotkeys.HotkeyService();
        service.Pressed += (_, _) =>
        {
            if (_docking.IsTracking)
            {
                CancelDrag();
            }
        };
        return service;
    }

    /// <summary>Mac clearHold: nothing waits for a hold any more.</summary>
    private void ClearHold()
    {
        _hold.Cancel();
        StopHoldTimer();
        _holdPointer = null;
    }

    private void StopHoldTimer() => _holdTimer?.Stop();

    private DispatcherQueueTimer CreateHoldTimer()
    {
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(ToolbarHold.Delay);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => OnHoldElapsed();
        return timer;
    }

    /// <summary>
    /// Mac keepsPressAtOnce: what the press landed on, from the element under the pointer out to the toolbar. Sliders,
    /// text fields and number fields get their press at once.
    /// </summary>
    private IEnumerable<ToolbarPressTarget> PressTargets(object source)
    {
        for (DependencyObject? element = source as DependencyObject; element is not null && !ReferenceEquals(element, _root);
             element = VisualTreeHelper.GetParent(element))
        {
            yield return element switch
            {
                Slider => ToolbarPressTarget.Slider,
                TextBox => ToolbarPressTarget.TextField,
                NumberBox => ToolbarPressTarget.NumberField,
                _ => ToolbarPressTarget.Other,
            };
        }
    }

    private static bool Captures(UIElement element, uint pointer)
    {
        if (element.PointerCaptures is not { } captures)
        {
            return false;
        }
        foreach (Pointer captured in captures)
        {
            if (captured.PointerId == pointer)
            {
                return true;
            }
        }
        return false;
    }
}
