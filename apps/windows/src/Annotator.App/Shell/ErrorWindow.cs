using Annotator.Platform.Windowing;
using Annotator.Workspace;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Annotator.App.Shell;

/// <summary>
/// The error window's content (the Mac WorkspaceErrorController's stack, 20 points from the edges with 16 points between
/// its parts): "Could not complete this action" in bold 14-point text, the message in 13-point text that wraps at 420 points
/// and can be selected and copied, and OK. The window is 460 points wide and at least 160 high, taller for a long message.
/// Each text reports AppKit's label frame to UI Automation, one point outside its lines.
/// </summary>
internal sealed class ErrorContent
{
    public const double Width = 460;
    public const double MinimumHeight = 160;
    public const string Heading = "Could not complete this action";

    private readonly Grid _root = new() { Width = Width };
    private readonly TextBlock _heading;
    private readonly TextBlock _message;
    private readonly TextBlock _ok;
    private readonly Border _bezel;

    /// <param name="dismiss">What OK does.</param>
    public ErrorContent(Action dismiss)
    {
        _heading = Line(Heading, 14, FontWeights.Bold, 17);
        _message = Line(string.Empty, 13, FontWeights.Normal, 16);
        _message.Width = 422;
        _message.TextWrapping = TextWrapping.Wrap;
        _message.IsTextSelectionEnabled = true;
        // The Mac's push button: a 42.5 by 24-point bezel, with AppKit's accessibility frame one point outside it.
        _ok = new TextBlock
        {
            Text = "OK",
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Style = AppResources.Style("AnnotatorPrimaryText"),
        };
        AutomationProperties.SetAccessibilityView(_ok, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        _bezel = new Border { Margin = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _ok };
        Ok = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Width = 44.5,
            Height = 26,
            CornerRadius = new CornerRadius(7),
            Content = _bezel,
        };
        AutomationProperties.SetName(Ok, "OK");
        Ok.Click += (_, _) => dismiss();
        var stack = new MacStack
        {
            Spacing = 14,
            Margin = new Thickness(19, 19, 19, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        stack.Children.Add(_heading);
        stack.Children.Add(_message);
        stack.Children.Add(Ok);
        _root.Children.Add(stack);
        _root.ActualThemeChanged += (_, _) => ApplyTheme();
        ApplyTheme();
    }

    public FrameworkElement Root => _root;

    public ParityButton Ok { get; }

    public string Message
    {
        get => _message.Text;
        set => _message.Text = value;
    }

    /// <summary>The Mac's content height: at least 160 points, or the stack's height and 40 points for a long message.</summary>
    public double ContentHeight()
    {
        _message.Measure(new Windows.Foundation.Size(_message.Width, double.PositiveInfinity));
        // 52 points above the message, then 14, the button's 26 and 18 below it (AppKit's fitting height and 40).
        return Math.Max(MinimumHeight, Math.Ceiling(_message.DesiredSize.Height) + 110);
    }

    /// <summary>Windows turned a contrast theme on or off: the colors follow it (WIN-UI-07).</summary>
    public void Restyle() => ApplyTheme();

    /// <summary>The window's colors for the current theme: the heading and message in the Mac's opaque text color.</summary>
    private void ApplyTheme()
    {
        _root.Background = AppResources.Brush("AnnotatorWindowBackgroundBrush");
        _bezel.Background = Pickers.PickerColors.ControlFill;
        ContrastTheme.ButtonLook(_bezel);
        Brush text = ContrastTheme.IsOn
            ? AppResources.Brush("AnnotatorLabelBrush")
            : new SolidColorBrush(Pickers.PickerColors.Dark ? Colors.White : Colors.Black);
        _heading.Foreground = text;
        _message.Foreground = text;
    }

    private static TextBlock Line(string text, double size, Windows.UI.Text.FontWeight weight, double lineHeight) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        LineHeight = lineHeight,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        Padding = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Left,
    };
}

/// <summary>
/// The app-owned error window (spec 005, WIN-UI-05), a port of the Mac's WorkspaceErrorController: titled "Annotator", it
/// shows the workspace's current error until the person dismisses it with OK, Enter, Escape or the close button, and nothing
/// else presents that error. A newer error replaces the message first, so a dismissal never clears an error that was not
/// shown. It opens centered on the display with the toolbar, above the toolbar and canvases (WIN-OVR-04). Like the Mac's
/// non-activating panel, it takes the keyboard only while Annotator is the active app (DV-1); otherwise it appears without
/// taking focus from the person's app. Focus goes back to the canvas or the earlier window when it closes.
/// </summary>
internal sealed class ErrorWindow : Window
{
    private readonly WorkspaceModel _model;
    private readonly Func<RectInt32?> _toolbarFrame;
    private readonly Func<HWND, HWND> _focusTarget;
    private readonly WindowRegistration _registration;
    private readonly ErrorContent _content;
    private readonly Grid _root;
    /// <summary>Shows the window cloaked until its content drew, and hides it by cloaking, so its first click works (WindowCloak).</summary>
    private readonly WindowCloak _cloak;
    /// <summary>The content at a larger text size (WIN-A11Y-03), zoomed; null at 100%.</summary>
    private ZoomPanel? _zoom;
    private HWND _previous;
    private bool _focusOnLoad;
    private bool _closing;

    /// <param name="toolbarFrame">The toolbar's rectangle in physical pixels, or null while it is hidden.</param>
    /// <param name="focusTarget">Where focus goes when the window closes, given the window that was active before it opened.</param>
    public ErrorWindow(WindowRegistry registry, WorkspaceModel model, Func<RectInt32?> toolbarFrame, Func<HWND, HWND> focusTarget)
    {
        _model = model;
        _toolbarFrame = toolbarFrame;
        _focusTarget = focusTarget;
        Title = "Annotator";
        var presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppIcon.Apply(AppWindow);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        Hwnd = new HWND(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        // Registered before the first show so capture can leave it out (S-1); above the toolbar, as the Mac's panel is.
        _registration = registry.Register((nint)Hwnd, WindowLayer.Error);
        _content = new ErrorContent(Dismiss);
        var root = new Grid();
        _root = root;
        root.Children.Add(_content.Root);
        _content.Root.HorizontalAlignment = HorizontalAlignment.Left;
        _content.Root.VerticalAlignment = VerticalAlignment.Stretch;
        root.KeyDown += OnKeyDown;
        _content.Root.Loaded += (_, _) =>
        {
            if (_focusOnLoad)
            {
                _focusOnLoad = false;
                _content.Ok.Focus(FocusState.Programmatic);
            }
        };
        AutomationFrames.Nudge(root);
        Content = root;
        _zoom = ZoomPanel.Host(this, root, null, Windows.Foundation.Size.Empty);
        AppResources.SystemLookChanged += OnSystemLookChanged;
        _cloak = new WindowCloak(this, Hwnd, "the error window");
        AppWindow.Closing += (_, args) =>
        {
            // The close button acknowledges the error (Mac windowShouldClose), and the window is kept for the next one.
            if (!_closing)
            {
                args.Cancel = true;
                Dismiss();
            }
        };
        Closed += (_, _) =>
        {
            AppResources.SystemLookChanged -= OnSystemLookChanged;
            _registration.Dispose();
        };
    }

    /// <summary>
    /// Windows turned a contrast theme on or off or changed the text size: the shown message takes the new colors and size
    /// (WIN-UI-07, WIN-A11Y-03).
    /// </summary>
    private void OnSystemLookChanged()
    {
        _zoom = ZoomPanel.Host(this, _root, _zoom, Windows.Foundation.Size.Empty);
        _content.Restyle();
        if (IsShown)
        {
            Present();
        }
    }

    public HWND Hwnd { get; }

    /// <summary>The message the window shows, or null while it is hidden (Mac displayedMessage).</summary>
    public string? DisplayedMessage { get; private set; }

    public bool IsShown { get; private set; }

    /// <summary>
    /// Mac present(_:): shows <paramref name="message"/>, the workspace's current error, or hides the window when there is
    /// none. Called with every snapshot; only a new message changes anything.
    /// </summary>
    public void Apply(string? message)
    {
        if (string.Equals(message, DisplayedMessage, StringComparison.Ordinal))
        {
            return;
        }
        DisplayedMessage = message;
        if (message is null)
        {
            Hide();
            return;
        }
        _content.Message = message;
        Present();
    }

    /// <summary>
    /// Mac dismiss(): acknowledges the shown error. When a newer error arrived meanwhile, it is shown instead, so it is never
    /// cleared unseen.
    /// </summary>
    public void Dismiss()
    {
        string? current = _model.Snapshot.ErrorMessage;
        if (!string.Equals(current, DisplayedMessage, StringComparison.Ordinal))
        {
            Apply(current);
            return;
        }
        if (DisplayedMessage is { } shown)
        {
            _model.DismissError(shown);
        }
        Apply(_model.Snapshot.ErrorMessage);
    }

    /// <summary>Mac dismissIfShown(): Escape's first step while zoom runs; true when an error was shown.</summary>
    public bool DismissIfShown()
    {
        if (!IsShown)
        {
            return false;
        }
        Dismiss();
        return true;
    }

    /// <summary>Closes the window for good (Quit).</summary>
    public void Shutdown()
    {
        _closing = true;
        Close();
    }

    private void Present()
    {
        bool opening = !IsShown;
        MonitorArea monitor;
        if (opening)
        {
            monitor = _toolbarFrame() is { } toolbar ? WindowChrome.MonitorFor(toolbar) : WindowChrome.MonitorAtCursor();
            // On the display first, so the size below uses its scale.
            AppWindow.Move(new PointInt32(monitor.WorkArea.X, monitor.WorkArea.Y));
        }
        else
        {
            monitor = WindowChrome.MonitorFor(new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height));
        }
        double scale = monitor.Scale;
        if (_zoom is not null)
        {
            // The message is measured at the size it shows at, zoomed.
            ZoomPanel.FreezeText(_root);
        }
        double height = _content.ContentHeight(), zoom = 1;
        if (_zoom is { } panel)
        {
            // As large as the text size makes it, while it fits in the work area.
            zoom = TextScaling.Fit(ErrorContent.Width, height, new Windows.Foundation.Size(monitor.WorkArea.Width / scale - 32, monitor.WorkArea.Height / scale - 64));
            panel.Zoom = zoom;
        }
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(ErrorContent.Width * zoom * scale), (int)Math.Round(height * zoom * scale)));
        HWND foreground = PInvoke.GetForegroundWindow();
        if (opening)
        {
            // Mac center(): the middle of the display's work area.
            RectInt32 work = monitor.WorkArea;
            SizeInt32 size = AppWindow.Size;
            AppWindow.Move(new PointInt32(work.X + Math.Max(0, (work.Width - size.Width) / 2), work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
            _previous = foreground == Hwnd ? HWND.Null : foreground;
        }
        bool activate = IsAnnotator(foreground);
        IsShown = true;
        _cloak.BeforeShow();
        AppWindow.Show(activateWindow: activate);
        WindowChrome.KeepOnTop(Hwnd);
        if (activate)
        {
            PInvoke.SetForegroundWindow(Hwnd);
            _focusOnLoad = !_content.Root.IsLoaded;
            if (!_focusOnLoad)
            {
                _content.Ok.Focus(FocusState.Programmatic);
            }
        }
    }

    /// <summary>
    /// Hides the window after giving the keyboard back to where it was, then cloaks it rather than hiding it, so the next error
    /// takes its first click at once (<see cref="WindowCloak"/>).
    /// </summary>
    private void Hide()
    {
        if (!IsShown)
        {
            return;
        }
        IsShown = false;
        if (PInvoke.GetForegroundWindow() == Hwnd)
        {
            HWND target = _focusTarget(_previous);
            if (!target.IsNull && PInvoke.IsWindow(target))
            {
                PInvoke.SetForegroundWindow(target);
            }
        }
        _cloak.Hide();
    }

    /// <summary>The Mac panel's keys: Return is OK's key equivalent and Escape closes the panel; both acknowledge the error.</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Escape or VirtualKey.Enter)
        {
            args.Handled = true;
            Dismiss();
        }
    }

    private static bool IsAnnotator(HWND window)
    {
        if (window.IsNull)
        {
            return false;
        }
        PInvoke.GetWindowThreadProcessId(window, out uint process);
        return process == (uint)Environment.ProcessId;
    }
}
