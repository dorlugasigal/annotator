using Annotator.Platform.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Annotator.App.Shell;

/// <summary>Where a popover opens relative to its anchor.</summary>
internal enum PopoverPlacement
{
    /// <summary>Above the anchor when there is room (tray icons on a bottom taskbar), else below.</summary>
    AboveOrBelow,

    /// <summary>Below the anchor when there is room (toolbar controls), else above.</summary>
    BelowOrAbove,
}

/// <summary>
/// A small borderless window that takes keyboard focus while open, like a Mac popover: the tray flyout and the
/// color palette. Escape closes it and gives focus back; a click elsewhere deactivates and closes it. It needs
/// no event hooks: activation changes tell it everything (WIN-UI-01).
/// </summary>
internal abstract class PopoverWindow : Window
{
    private readonly WindowRegistration _registration;
    private readonly WindowCloak _cloak;
    /// <summary>The surface at a larger text size (WIN-A11Y-03), zoomed; null at 100%.</summary>
    private ZoomPanel? _zoom;
    private HWND _previousForeground;
    private bool _closing;

    protected PopoverWindow(WindowRegistry registry, string title)
    {
        Title = title;
        Hwnd = WindowChrome.Configure(this, noActivate: false);
        // Registered before the first show so capture can leave it out (S-1).
        _registration = registry.Register((nint)Hwnd, WindowLayer.Popover);
        // It shows cloaked until its content has drawn, so the first click on it works (WindowCloak).
        _cloak = new WindowCloak(this, Hwnd, $"the {title} popover");
        Activated += OnActivated;
        Closed += (_, _) =>
        {
            _registration.Dispose();
            Dismissed?.Invoke();
        };
    }

    /// <summary>Raised once, after the popover closed for any reason.</summary>
    public event Action? Dismissed;

    public HWND Hwnd { get; }

    /// <summary>The surface to measure and show. Set once in the constructor of the subclass.</summary>
    protected FrameworkElement? Surface { get; set; }

    /// <summary>Measures the surface, places it next to <paramref name="anchor"/> inside the work area and takes focus.</summary>
    public void Open(RectInt32 anchor, PopoverPlacement placement)
    {
        if (Surface is null)
        {
            throw new InvalidOperationException("The popover has no content.");
        }
        _anchor = anchor;
        _placement = placement;
        Surface.KeyDown += OnKeyDown;
        // Nothing can take focus before the surface is laid out, and then Escape and the arrow keys reached nothing, so the
        // first control takes the keyboard again once the surface is loaded.
        Surface.Loaded += (_, _) =>
        {
            Place();
            if (!_closing)
            {
                FocusFirst();
            }
        };
        ShowSurface();
        Place();

        HWND foreground = PInvoke.GetForegroundWindow();
        _previousForeground = foreground == Hwnd ? HWND.Null : foreground;
        _cloak.BeforeShow();
        AppWindow.Show(activateWindow: true);
        WindowChrome.KeepOnTop(Hwnd);
        PInvoke.SetForegroundWindow(Hwnd);
        FocusFirst();
    }

    private RectInt32 _anchor;
    private PopoverPlacement _placement;
    private PointInt32? _referenceOrigin;

    /// <summary>
    /// Development only (UX reference capture): shows the surface with its top-left corner at <paramref name="origin"/>
    /// in physical pixels, without activating the window or moving focus.
    /// </summary>
    public void ShowForReference(PointInt32 origin)
    {
        if (Surface is null)
        {
            throw new InvalidOperationException("The popover has no content.");
        }
        _referenceOrigin = origin;
        _anchor = new RectInt32(origin.X, origin.Y, 1, 1);
        Surface.Loaded += (_, _) => Place();
        ShowSurface();
        Place();
        AppWindow.Show(activateWindow: false);
    }

    /// <summary>Makes the surface the window's content, in a root measured at the surface's own size.</summary>
    private void ShowSurface()
    {
        var root = new WindowRoot { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        root.Children.Add(Surface!);
        AutomationFrames.Nudge(root);
        AppResources.FollowTheme(root);
        _zoom = TextScaling.Zoom > 1 ? new ZoomPanel(root, TextScaling.Zoom) : null;
        Content = _zoom ?? (UIElement)root;
        _shown = true;
    }

    private bool _shown;

    /// <summary>Sizes the window to the surface again after its content changed, keeping it next to its anchor.</summary>
    protected void Refit()
    {
        if (_shown && !_closing)
        {
            Place();
        }
    }

    /// <summary>Sizes the window to the surface (measured again once it is loaded) and keeps it in the work area.</summary>
    private void Place()
    {
        MonitorArea monitor = WindowChrome.MonitorFor(_anchor);
        var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);
        Surface!.Measure(unlimited);
        if (_zoom is not null && ZoomPanel.FreezeText(Surface))
        {
            Surface.Measure(unlimited);
        }
        Size desired = Surface.DesiredSize;
        if (desired.Width < 1 || desired.Height < 1)
        {
            desired = new Size(320, 240);
        }
        double zoom = 1;
        if (_zoom is { } panel)
        {
            // As large as the text size makes it, while it fits in the work area with its 8-point gaps.
            zoom = TextScaling.Fit(desired.Width, desired.Height,
                new Size(monitor.WorkArea.Width / monitor.Scale - 16, monitor.WorkArea.Height / monitor.Scale - 16));
            panel.Zoom = zoom;
        }
        int width = (int)Math.Ceiling(desired.Width * zoom * monitor.Scale);
        int height = (int)Math.Ceiling(desired.Height * zoom * monitor.Scale);
        if (_referenceOrigin is { } origin)
        {
            AppWindow.MoveAndResize(new RectInt32(origin.X, origin.Y, width, height));
            return;
        }
        int gap = (int)Math.Round(8 * monitor.Scale);
        RectInt32 work = monitor.WorkArea;
        RectInt32 anchor = _anchor;
        int x = anchor.X + anchor.Width / 2 - width / 2;
        bool roomAbove = anchor.Y - gap - height >= work.Y;
        bool roomBelow = anchor.Y + anchor.Height + gap + height <= work.Y + work.Height;
        bool above = _placement == PopoverPlacement.AboveOrBelow ? roomAbove || !roomBelow : !roomBelow && roomAbove;
        int y = above ? anchor.Y - gap - height : anchor.Y + anchor.Height + gap;
        x = Math.Clamp(x, work.X + gap, Math.Max(work.X + gap, work.X + work.Width - width - gap));
        y = Math.Clamp(y, work.Y + gap, Math.Max(work.Y + gap, work.Y + work.Height - height - gap));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    /// <summary>
    /// Closes the popover. <paramref name="restoreFocus"/> gives focus to <see cref="FocusTarget"/> first, while the
    /// popover is still active, so Windows does not activate some other window in between.
    /// </summary>
    public void Dismiss(bool restoreFocus)
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        ClosedByDeactivation = !restoreFocus && _deactivated;
        if (restoreFocus && PInvoke.GetForegroundWindow() == Hwnd)
        {
            HWND target = FocusTarget(_previousForeground);
            if (!target.IsNull && PInvoke.IsWindow(target))
            {
                PInvoke.SetForegroundWindow(target);
            }
        }
        Close();
    }

    /// <summary>True when the popover closed because another window became active.</summary>
    public bool ClosedByDeactivation { get; private set; }

    /// <summary>Where focus goes when the popover closes with Escape or a choice; by default the window active before it opened.</summary>
    protected virtual HWND FocusTarget(HWND previous) => previous;

    /// <summary>Moves keyboard focus to the first control when the popover opens.</summary>
    protected abstract void FocusFirst();

    /// <summary>Handles keys the popover defines; return true when handled.</summary>
    protected virtual bool OnKey(VirtualKey key, bool control) => false;

    /// <summary>Handles Escape before it closes the popover, for example to end a mode inside it; return true when handled.</summary>
    protected virtual bool OnEscape() => false;

    private bool _deactivated;

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // A reference capture is never shown active; if something activates it anyway (UI Automation scrolling its content),
        // losing that activation must not close it while the tool still reads it.
        if (args.WindowActivationState == WindowActivationState.Deactivated && _referenceOrigin is null)
        {
            // A click elsewhere: focus already went where the person clicked.
            _deactivated = true;
            Dismiss(restoreFocus: false);
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        bool control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (args.Key == VirtualKey.Escape && !control)
        {
            args.Handled = true;
            if (!OnEscape())
            {
                Dismiss(restoreFocus: true);
            }
            return;
        }
        if (OnKey(args.Key, control))
        {
            args.Handled = true;
        }
    }
}
