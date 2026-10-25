using Annotator.App.Shell;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Updates;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Annotator.App.Settings;

/// <summary>
/// The Settings window (WIN-UI-03; the Mac's Settings scene with SettingsWindowSurface): resizable and nonmodal, 840 by
/// 700 points at first, at least 720 by 620 and at most the work area, centered on the display with the toolbar. Like the
/// Mac's window it stays above other windows at the toolbar's level; it has a taskbar button and shows in Alt+Tab. The
/// close button hides it, keeping its size, place and tab. Report a problem and Share feedback open over it.
/// </summary>
internal sealed class SettingsWindow : Window
{
    public const double DefaultWidth = 840;
    public const double DefaultHeight = 700;
    public const double MinimumWidth = 720;
    public const double MinimumHeight = 620;

    /// <summary>The least size of the content at a larger text size: the tabs, the footer and a page that scrolls.</summary>
    private static readonly Windows.Foundation.Size ZoomedMinimum = new(MinimumWidth, 400);

    private readonly WindowRegistry _registry;
    private readonly SettingsActions _actions;
    private readonly Func<RectInt32?> _anchor;
    private readonly OverlappedPresenter _presenter;
    private readonly WindowRegistration _registration;
    private readonly Grid _host = new();
    private readonly Dictionary<SettingsTab, FrameworkElement> _pages = [];
    /// <summary>Shows the window cloaked until its content drew, and hides it by cloaking, so its first click works (WindowCloak).</summary>
    private readonly WindowCloak _cloak;
    private SettingsView _view;
    private FeedbackWindow? _feedback;
    private ZoomPanel? _zoom;
    private bool _placed;
    private bool _closing;

    /// <param name="anchor">The toolbar's rectangle in physical pixels, or null while it is hidden.</param>
    public SettingsWindow(WindowRegistry registry, SettingsActions actions, Func<RectInt32?> anchor)
    {
        _registry = registry;
        _actions = actions;
        _anchor = anchor;
        Title = "Annotator Settings";
        _presenter = OverlappedPresenter.Create();
        _presenter.IsResizable = true;
        _presenter.IsMaximizable = true;
        _presenter.IsMinimizable = true;
        _presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(_presenter);
        Shell.AppIcon.Apply(AppWindow);
        AppWindow.IsShownInSwitchers = true;
        Hwnd = new HWND(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        // Registered before the first show so capture can leave it out (S-1); the Mac raises Settings to the toolbar's level.
        _registration = registry.Register((nint)Hwnd, WindowLayer.Toolbar);
        actions.Owner = () => (nint)Hwnd;
        actions.OpenTab = tab => Show(tab);
        actions.OpenFeedback = OpenFeedback;
        _view = new SettingsView(actions, SettingsTab.General, _pages);
        _host.Children.Add(_view.Root);
        _host.KeyDown += OnKeyDown;
        _host.ActualThemeChanged += (_, _) => Rebuild();
        AppResources.FollowTheme(_host, AppWindow);
        AutomationFrames.Nudge(_host);
        Content = _host;
        // A larger text size shows Settings larger as a whole (WIN-A11Y-03); a window too small for that zoom shows it less
        // zoomed, and its pages scroll. A contrast theme or text size turned on later builds the pages again.
        _zoom = ZoomPanel.Host(this, _host, null, ZoomedMinimum);
        AppResources.SystemLookChanged += OnSystemLookChanged;
        Closed += (_, _) => AppResources.SystemLookChanged -= OnSystemLookChanged;
        _cloak = new WindowCloak(this, Hwnd, "Settings");
        AppWindow.Closing += (_, args) =>
        {
            if (!_closing)
            {
                args.Cancel = true;
                Hide();
            }
        };
        Activated += (_, args) =>
        {
            // The Mac reads launch at login and permissions again when Settings becomes key or the app active.
            if (args.WindowActivationState != WindowActivationState.Deactivated && IsShown)
            {
                _view.Refresh();
            }
        };
        Closed += (_, _) =>
        {
            _view.Detach();
            _registration.Dispose();
        };
    }

    public HWND Hwnd { get; }

    public bool IsShown { get; private set; }

    /// <summary>The window showed or hid (the Mac's settingsVisible, which the coffee reminder waits for).</summary>
    public event Action? ShownChanged;

    public SettingsTab Tab => _view.Tab;

    /// <summary>
    /// The seam for the Toolbar &amp; Shortcuts and Pointer pages, which are built elsewhere: the page shows inside the tab
    /// box when its tab is chosen. A tab without a page shows nothing.
    /// </summary>
    public void SetPage(SettingsTab tab, FrameworkElement page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (tab is not (SettingsTab.Toolbar or SettingsTab.Pointer or SettingsTab.BreakTimer))
        {
            throw new ArgumentException("Only the Toolbar & Shortcuts, Pointer and Break timer pages are set from outside Settings.", nameof(tab));
        }
        _pages[tab] = page;
        _view.PageChanged(tab);
    }

    /// <summary>Shows the window on <paramref name="tab"/> (or the tab it showed last) and activates it.</summary>
    public void Show(SettingsTab? tab)
    {
        if (tab is { } chosen)
        {
            _view.Show(chosen);
        }
        if (_feedback is { IsClosed: false } feedback)
        {
            feedback.BringToFront();
            return;
        }
        if (!_placed)
        {
            Place();
        }
        else if (!IsShown)
        {
            KeepInsideWorkArea();
        }
        bool opening = !IsShown;
        IsShown = true;
        _presenter.IsAlwaysOnTop = true;
        _view.Apply(_actions.Model.Snapshot);
        _view.Refresh();
        _cloak.BeforeShow();
        AppWindow.Show(activateWindow: true);
        if (_presenter.State == OverlappedPresenterState.Minimized)
        {
            _presenter.Restore();
        }
        PInvoke.SetForegroundWindow(Hwnd);
        if (opening)
        {
            _view.FocusTabs();
            ShownChanged?.Invoke();
        }
    }

    /// <summary>
    /// Leaves the band above other windows while the tutorial card opened from Settings shows in front of it;
    /// <see cref="Float"/> or the next <see cref="Show"/> puts it back above them.
    /// </summary>
    public void StepBack() => _presenter.IsAlwaysOnTop = false;

    /// <summary>Back above other windows at the toolbar's level, where Settings stays (the Mac's level).</summary>
    public void Float()
    {
        if (IsShown)
        {
            _presenter.IsAlwaysOnTop = true;
        }
    }

    /// <summary>
    /// Hides the window (the close button, Alt+F4). It is cloaked rather than hidden, so it takes the first click as soon as it
    /// shows again (<see cref="WindowCloak"/>); Windows gives the keyboard to the window it chooses, as before.
    /// </summary>
    public void Hide()
    {
        if (!IsShown)
        {
            return;
        }
        IsShown = false;
        _cloak.Hide();
        ShownChanged?.Invoke();
    }

    /// <summary>Shows the workspace state while the window is shown; a hidden window catches up when it opens.</summary>
    public void Apply(WorkspaceSnapshot snapshot)
    {
        if (IsShown)
        {
            _view.Apply(snapshot);
        }
    }

    /// <summary>Closes the window and any feedback window for good (Quit).</summary>
    public void Shutdown()
    {
        _closing = true;
        if (_feedback is { IsClosed: false } feedback)
        {
            feedback.Close();
        }
        Close();
    }

    /// <summary>
    /// Shows Settings and asks Annotator's question before a Store install that could close it (WIN-SYS-03); true for
    /// Install update.
    /// </summary>
    public Task<bool> AskBeforeUpdateAsync(UpdateQuestion question)
    {
        Show(null);
        return UpdateDialog.AskAsync(_host, question);
    }

    /// <summary>
    /// Shows Settings on General and asks about a release a portable copy's automatic check found (WIN-SYS-03, Sparkle's
    /// update window on the Mac).
    /// </summary>
    public Task<UpdateDialog.ReleaseChoice> AskAboutReleaseAsync(ReleaseFeedEntry release)
    {
        Show(SettingsTab.General);
        return UpdateDialog.AskAboutReleaseAsync(_host, release);
    }

    /// <summary>Report a problem or Share feedback over Settings, which waits until it closes (the Mac's sheet).</summary>
    private void OpenFeedback(FeedbackKind kind)
    {
        if (_feedback is { IsClosed: false } open)
        {
            open.BringToFront();
            return;
        }
        var feedback = new FeedbackWindow(_registry, _actions, kind, Hwnd);
        _feedback = feedback;
        feedback.Open(new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height));
    }

    /// <summary>
    /// Windows turned a contrast theme on or off or changed the text size: the pages are made again with the new colors and
    /// zoom, and an open window takes its new size (WIN-UI-07, WIN-A11Y-03).
    /// </summary>
    private void OnSystemLookChanged()
    {
        _zoom = ZoomPanel.Host(this, _host, _zoom, ZoomedMinimum);
        Rebuild();
        if (IsShown)
        {
            Place();
        }
    }

    /// <summary>The theme changed: the pages are made again with its colors, on the same tab.</summary>
    private void Rebuild()
    {
        SettingsTab tab = _view.Tab;
        _view.Detach();
        _host.Children.Clear();
        _view = new SettingsView(_actions, tab, _pages);
        _host.Children.Add(_view.Root);
        if (IsShown)
        {
            _view.Apply(_actions.Model.Snapshot);
        }
    }

    /// <summary>
    /// The first placement: 840 by 700 points, or the work area when it is smaller, centered on the display with the
    /// toolbar (or the pointer); at least 720 by 620 points.
    /// </summary>
    private void Place()
    {
        MonitorArea monitor = _anchor() is { } frame ? WindowChrome.MonitorFor(frame) : WindowChrome.MonitorAtCursor();
        double scale = monitor.Scale;
        RectInt32 work = monitor.WorkArea;
        // Larger at a larger text size, up to the work area (WIN-A11Y-03).
        double zoom = TextScaling.Zoom;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(DefaultWidth * zoom * scale), (int)Math.Round(DefaultHeight * zoom * scale)));
        SizeInt32 outer = AppWindow.Size;
        SizeInt32 client = AppWindow.ClientSize;
        int frameWidth = outer.Width - client.Width, frameHeight = outer.Height - client.Height;
        _presenter.PreferredMinimumWidth = Math.Min(work.Width, (int)Math.Ceiling(MinimumWidth * scale) + frameWidth);
        _presenter.PreferredMinimumHeight = Math.Min(work.Height, (int)Math.Ceiling(MinimumHeight * scale) + frameHeight);
        _presenter.PreferredMaximumWidth = work.Width;
        _presenter.PreferredMaximumHeight = work.Height;
        int width = Math.Min(outer.Width, work.Width), height = Math.Min(outer.Height, work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        _placed = true;
    }

    /// <summary>A display change can leave the hidden window outside every work area; it comes back inside the nearest one.</summary>
    private void KeepInsideWorkArea()
    {
        RectInt32 frame = new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        RectInt32 work = WindowChrome.MonitorFor(frame).WorkArea;
        int width = Math.Min(frame.Width, work.Width), height = Math.Min(frame.Height, work.Height);
        int x = Math.Clamp(frame.X, work.X, work.X + work.Width - width);
        int y = Math.Clamp(frame.Y, work.Y, work.Y + work.Height - height);
        if (x != frame.X || y != frame.Y || width != frame.Width || height != frame.Height)
        {
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
    }

    /// <summary>Ctrl+, shows Settings (it is already open), and Ctrl+Tab and Ctrl+Shift+Tab move between tabs.</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        bool control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (!control)
        {
            return;
        }
        if (args.Key == (VirtualKey)0xBC)
        {
            args.Handled = true;
            Show(null);
        }
        else if (args.Key == VirtualKey.Tab)
        {
            args.Handled = true;
            int count = SettingsTab.AllCases.Count;
            Show(SettingsTab.AllCases[((int)_view.Tab + (shift ? count - 1 : 1)) % count]);
        }
    }
}
