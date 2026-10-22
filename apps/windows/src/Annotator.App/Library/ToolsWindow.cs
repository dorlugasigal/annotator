using Annotator.App.Shell;
using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Annotator.Workspace.Library;
using Annotator.Workspace.Settings;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Annotator.App.Library;

/// <summary>
/// The Tools window (WIN-UI-05, spec 005, a port of ToolLibraryWindowController): a resizable window titled "Tools" with
/// a close button, 440 by 500 points at first and at least 368 by 260, at most the work area. It opens 12 points below the
/// toolbar at its left edge, stays above other windows like the toolbar, and keeps its size and place while hidden.
/// Choosing a tool, inserting a saved shape, Escape and the close button hide it; hiding the workspace hides it too. It
/// has no taskbar button, like the Mac's panel has no Dock icon.
/// </summary>
internal sealed class ToolsWindow : Window
{
    private readonly WorkspaceModel _model;
    private readonly ShapeLibrary _library;
    private readonly ShapeThumbnails _thumbnails;
    private readonly Func<RectInt32?> _toolbarFrame;
    private readonly Func<HWND, HWND> _focusTarget;
    private readonly Action<SettingsTab>? _openSettings;
    private readonly Files.FileCommands? _files;
    private readonly WindowRegistration _registration;
    private readonly OverlappedPresenter _presenter;
    private readonly LibraryState _state = new();
    private readonly Grid _host = new();
    private static readonly Windows.Foundation.Size ZoomedMinimum = new(ToolLibraryLayout.MinimumWidth, ToolLibraryLayout.MinimumHeight);
    private ZoomPanel? _zoom;
    private ToolsPanel _panel;
    private HWND _previous;
    private bool _placed;
    private bool _closing;
    /// <summary>Shows the window cloaked until its content drew, and hides it by cloaking (WindowCloak).</summary>
    private readonly WindowCloak _cloak;

    /// <param name="toolbarFrame">The toolbar's rectangle in physical pixels, or null while it is hidden.</param>
    /// <param name="focusTarget">Where focus goes when the window hides, given the window that was active before it opened.</param>
    /// <param name="openSettings">Opens Settings on a tab (Ctrl+, Pointer settings and Workspace > Help); null leaves them out.</param>
    /// <param name="files">The File menu's and Insert image's dialogs, or null to leave them unavailable.</param>
    public ToolsWindow(WindowRegistry registry, WorkspaceModel model, ShapeLibrary library, ShapeThumbnails thumbnails,
                       Func<RectInt32?> toolbarFrame, Func<HWND, HWND> focusTarget, Action<SettingsTab>? openSettings = null,
                       Files.FileCommands? files = null)
    {
        _model = model;
        _library = library;
        _thumbnails = thumbnails;
        _toolbarFrame = toolbarFrame;
        _focusTarget = focusTarget;
        _openSettings = openSettings;
        _files = files;
        Title = "Tools";
        _presenter = OverlappedPresenter.Create();
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        _presenter.IsResizable = true;
        _presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(_presenter);
        AppWindow.IsShownInSwitchers = false;
        Hwnd = new HWND(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        // Registered before the first show so capture can leave it out (S-1); like the Mac panel, it shares the toolbar's level.
        _registration = registry.Register((nint)Hwnd, WindowLayer.Toolbar);
        _panel = NewPanel();
        _host.Children.Add(_panel.Root);
        _host.KeyDown += OnKeyDown;
        _host.ActualThemeChanged += (_, _) => Rebuild();
        AppResources.FollowTheme(_host, AppWindow);
        AutomationFrames.Nudge(_host);
        Content = _host;
        // A larger text size shows the window larger as a whole (WIN-A11Y-03); a window too small for that zoom shows it
        // less zoomed, and its sections scroll. A contrast theme or text size turned on later builds it again.
        _zoom = ZoomPanel.Host(this, _host, null, ZoomedMinimum);
        AppResources.SystemLookChanged += OnSystemLookChanged;
        _cloak = new WindowCloak(this, Hwnd, "the Tools window");
        AppWindow.Closing += (_, args) =>
        {
            // The close button hides the window; it keeps its size and place for the next time (Mac windowWillClose).
            if (!_closing)
            {
                args.Cancel = true;
                Hide(restoreFocus: true);
            }
        };
        Closed += (_, _) =>
        {
            AppResources.SystemLookChanged -= OnSystemLookChanged;
            _panel.Detach();
            _registration.Dispose();
        };
    }

    /// <summary>
    /// Windows turned a contrast theme on or off or changed the text size: the window is made again with the new colors and
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

    public HWND Hwnd { get; }

    public bool IsShown { get; private set; }

    /// <summary>
    /// Mac show(section:near:): shows <paramref name="section"/>, or the last section when null, and takes keyboard focus.
    /// The first time it opens below the toolbar.
    /// </summary>
    public void Show(ToolLibrarySection? section)
    {
        if (section is { } chosen && chosen != _panel.Section)
        {
            _panel.Show(chosen, reload: false);
        }
        if (!_placed)
        {
            Place();
        }
        else if (!IsShown)
        {
            KeepInsideWorkArea();
        }
        HWND foreground = PInvoke.GetForegroundWindow();
        if (!IsShown)
        {
            _previous = foreground == Hwnd ? HWND.Null : foreground;
        }
        _panel.Apply(_model.Snapshot);
        bool opening = !IsShown;
        IsShown = true;
        _cloak.BeforeShow();
        AppWindow.Show(activateWindow: true);
        PInvoke.SetForegroundWindow(Hwnd);
        if (opening || section == ToolLibrarySection.MyShapes)
        {
            _panel.Opened(focusName: section == ToolLibrarySection.MyShapes);
        }
    }

    /// <summary>
    /// Hides the window (Mac hide()). <paramref name="restoreFocus"/> first gives focus back to where it belongs, while the
    /// window is still active, so Windows does not activate some other window in between. Then the window is cloaked rather
    /// than hidden (<see cref="WindowCloak"/>), so its content takes the first click as soon as it shows again: a click about
    /// 0.1 s after Ctrl+L did nothing in some live checks while other work kept the GPU busy.
    /// </summary>
    public void Hide(bool restoreFocus)
    {
        if (!IsShown)
        {
            return;
        }
        IsShown = false;
        if (restoreFocus && PInvoke.GetForegroundWindow() == Hwnd)
        {
            HWND target = _focusTarget(_previous);
            if (!target.IsNull && PInvoke.IsWindow(target))
            {
                PInvoke.SetForegroundWindow(target);
            }
        }
        _cloak.Hide();
    }

    /// <summary>Shows the workspace state while the window is shown; a hidden window catches up when it opens.</summary>
    public void Apply(WorkspaceSnapshot snapshot)
    {
        if (IsShown)
        {
            _panel.Apply(snapshot);
        }
    }

    /// <summary>Closes the window for good (Quit).</summary>
    public void Shutdown()
    {
        _closing = true;
        Close();
    }

    private ToolsPanel NewPanel() => new(_model, _library, _thumbnails, _state, () => Hide(restoreFocus: true), openSettings: _openSettings,
        files: _files);

    /// <summary>The theme changed: the panel is made again with the new colors and keeps its state.</summary>
    private void Rebuild()
    {
        _panel.Detach();
        _panel = NewPanel();
        _host.Children.Clear();
        _host.Children.Add(_panel.Root);
    }

    /// <summary>
    /// The first placement (Mac show(section:near:)): the 440 by 500-point content at the toolbar's left edge, 12 points below
    /// it, inside the work area of the toolbar's display, or at the bottom left of the work area without a toolbar.
    /// </summary>
    private void Place()
    {
        RectInt32? toolbar = _toolbarFrame();
        MonitorArea monitor = toolbar is { } frame ? WindowChrome.MonitorFor(frame) : WindowChrome.MonitorAtCursor();
        double scale = monitor.Scale;
        RectInt32 work = monitor.WorkArea;
        // Larger at a larger text size, up to the work area (WIN-A11Y-03).
        double zoom = TextScaling.Zoom;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(ToolLibraryLayout.WindowWidth * zoom * scale), (int)Math.Round(ToolLibraryLayout.WindowHeight * zoom * scale)));
        SizeInt32 outer = AppWindow.Size;
        SizeInt32 client = AppWindow.ClientSize;
        int frameWidth = outer.Width - client.Width, frameHeight = outer.Height - client.Height;
        _presenter.PreferredMinimumWidth = (int)Math.Ceiling(ToolLibraryLayout.MinimumWidth * scale) + frameWidth;
        _presenter.PreferredMinimumHeight = (int)Math.Ceiling(ToolLibraryLayout.MinimumHeight * scale) + frameHeight;
        _presenter.PreferredMaximumWidth = work.Width;
        _presenter.PreferredMaximumHeight = work.Height;
        int width = Math.Min(outer.Width, work.Width), height = Math.Min(outer.Height, work.Height);
        int x = work.X, y = work.Y + work.Height - height;
        if (toolbar is { } anchor)
        {
            x = Math.Clamp(anchor.X, work.X, Math.Max(work.X, work.X + work.Width - width));
            y = Math.Clamp(anchor.Y + anchor.Height + (int)Math.Round(12 * scale), work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        }
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
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

    /// <summary>
    /// The Mac panel's key equivalents outside text fields: Ctrl+L opens Tools (it is already open), Ctrl+C, Ctrl+X and
    /// Ctrl+V copy, cut and paste drawings, as in the canvas, and Ctrl+, opens Settings. Global shortcuts work everywhere through Windows.
    /// </summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        bool control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (!control || FocusManager.GetFocusedElement(_host.XamlRoot) is TextBox)
        {
            return;
        }
        switch (args.Key)
        {
            case VirtualKey.L when Workspace.Shortcuts.WindowsShortcuts.ToolLibraryKeyIsFree(_model.Snapshot.Preferences):
                _model.ShowToolbar();
                Show(null);
                break;
            case VirtualKey.C:
                _model.CopySelection();
                break;
            case VirtualKey.X:
                _model.CopySelection(cut: true);
                break;
            case VirtualKey.V:
                _model.Paste();
                break;
            case (VirtualKey)0xBC when _openSettings is not null:
                // Ctrl+, opens Settings, as everywhere in the app (the Mac's Command-comma).
                _openSettings(SettingsTab.General);
                break;
            default:
                return;
        }
        args.Handled = true;
    }
}
