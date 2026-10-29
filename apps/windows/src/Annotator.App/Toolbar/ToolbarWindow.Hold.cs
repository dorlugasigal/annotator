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
/// pressed until the toolbar is picked up.
/// </summary>
internal sealed partial class ToolbarWindow
{
    private readonly ToolbarHoldGesture _hold = new();
    private DispatcherQueueTimer? _holdTimer;
    /// <summary>The pointer of the press that waits for the hold.</summary>
    private Pointer? _holdPointer;
    /// <summary>Where that press started, in desktop pixels.</summary>
    private CorePoint _holdPress;

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
        if (_hold.Elapse(buttonDown: pointer is not null, HoldAllowed()) is null || pointer is null)
        {
            return;
        }
        if (!_docking.IsTracking)
        {
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
