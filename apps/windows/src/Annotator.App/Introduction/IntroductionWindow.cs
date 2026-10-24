using Annotator.App.Shell;
using Annotator.Workspace;
using Annotator.Workspace.FirstUse;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.App.Introduction;

/// <summary>
/// The tour window (WIN-UI-04, a port of the Mac's IntroductionPanel): "Welcome to Annotator" with a close button, around
/// the <see cref="TourView"/>, centered on the display under the pointer. It is a normal window with a taskbar button, so a
/// tour that opened without focus can be found again. Closing it releases the window and its drawing devices; the
/// navigator keeps the page for the next time.
/// </summary>
internal sealed class IntroductionWindow : Window
{
    private readonly TourView _view;
    /// <summary>The tour at a larger text size (WIN-A11Y-03), zoomed; null at 100%.</summary>
    private ZoomPanel? _zoom;
    private readonly Action _close;
    /// <summary>Shows the tour cloaked until its content drew, so its first click works (WindowCloak).</summary>
    private readonly WindowCloak _cloak;
    private bool _closing;

    /// <param name="close">Hides the tour (Got it and the close button): the controller's Close.</param>
    public IntroductionWindow(IntroductionNavigator navigator, IntroductionPreferences preferences, Func<WorkspaceSnapshot> snapshot,
                              Annotator.Workspace.Metrics.UsageMetrics usage, Action close)
    {
        _close = close;
        Title = IntroductionText.WindowTitle;
        OverlappedPresenter presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        AppWindow.SetPresenter(presenter);
        Shell.AppIcon.Apply(AppWindow);
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        Hwnd = new HWND(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        _view = new TourView(navigator, preferences, snapshot, usage, close);
        // A larger text size shows the tour larger as a whole (WIN-A11Y-03).
        Content = _view.Root;
        _zoom = ZoomPanel.Host(this, _view.Root, null, Windows.Foundation.Size.Empty);
        AppResources.SystemLookChanged += OnSystemLookChanged;
        _cloak = new WindowCloak(this, Hwnd, "the tour");
        AppWindow.Closing += (_, args) =>
        {
            // The close button ends the tour like Got it (Mac windowWillClose calls close()), after this event.
            if (!_closing)
            {
                args.Cancel = true;
                DispatcherQueue.TryEnqueue(() => _close());
            }
        };
        Closed += (_, _) =>
        {
            AppResources.SystemLookChanged -= OnSystemLookChanged;
            _view.Detach();
        };
    }

    public HWND Hwnd { get; }

    public bool IsShown { get; private set; }

    public TourView View => _view;

    /// <summary>
    /// Shows the window: activated and focused when the person asked for it, otherwise without taking focus (Mac
    /// orderFrontRegardless for the automatic presentation).
    /// </summary>
    public void Present(bool takeFocus)
    {
        if (!IsShown)
        {
            Place();
        }
        IsShown = true;
        _view.Shown();
        _cloak.BeforeShow();
        AppWindow.Show(activateWindow: takeFocus);
        if (takeFocus)
        {
            PInvoke.SetForegroundWindow(Hwnd);
        }
        else
        {
            RaiseWithoutFocus();
        }
    }

    /// <summary>
    /// Mac orderFrontRegardless: in front of the other apps' windows without taking focus. A window shown without activation
    /// stays below the active window, so it passes through the topmost band once and returns to the normal one at its top.
    /// </summary>
    private void RaiseWithoutFocus()
    {
        var NotTopmost = new HWND((nint)(-2));
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
        if (!PInvoke.SetWindowPos(Hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0, flags) || !PInvoke.SetWindowPos(Hwnd, NotTopmost, 0, 0, 0, 0, flags))
        {
            StartupLog.Note("introduction-raise", $"SetWindowPos failed with error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        }
    }

    /// <summary>Hides the window; the story ends and the drawing devices are released until it shows again.</summary>
    public void Conceal()
    {
        if (!IsShown)
        {
            return;
        }
        IsShown = false;
        AppWindow.Hide();
        _view.Hidden();
    }

    /// <summary>Follows the workspace: keycaps and the shortcuts card change with the shortcuts.</summary>
    public void Apply(WorkspaceSnapshot snapshot) => _view.Apply(snapshot);

    /// <summary>Closes the window for good (Quit).</summary>
    public void Shutdown()
    {
        _closing = true;
        Close();
    }

    /// <summary>
    /// Windows turned a contrast theme on or off or changed the text size: the tour is made again with the new colors and
    /// zoom, and an open tour takes its new size (WIN-UI-07, WIN-A11Y-03).
    /// </summary>
    private void OnSystemLookChanged()
    {
        _zoom = ZoomPanel.Host(this, _view.Root, _zoom, Windows.Foundation.Size.Empty);
        _view.Restyle();
        if (IsShown)
        {
            Place();
        }
    }

    /// <summary>Mac: content 520 by min(530, max(340, the screen's visible height - 90)), centered (NSWindow.center).</summary>
    private void Place()
    {
        MonitorArea monitor = WindowChrome.MonitorAtCursor();
        double scale = monitor.Scale;
        RectInt32 work = monitor.WorkArea;
        double zoom = 1;
        if (_zoom is { } panel)
        {
            // As large as the text size makes it, while 520 by 340 points fit in the work area; its pages scroll.
            zoom = TextScaling.Fit(520, 340, new Windows.Foundation.Size(work.Width / scale - 32, work.Height / scale - 90));
            panel.Zoom = zoom;
        }
        double height = Math.Min(530, Math.Max(340, (work.Height / scale - 90) / zoom));
        _view.ContentHeight = height;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(520 * zoom * scale), (int)Math.Round(height * zoom * scale)));
        SizeInt32 outer = AppWindow.Size;
        int x = work.X + Math.Max(0, (work.Width - outer.Width) / 2);
        int y = work.Y + Math.Max(0, (work.Height - outer.Height) / 2);
        AppWindow.Move(new PointInt32(x, y));
    }
}
