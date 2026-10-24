using Annotator.App.Settings;
using Annotator.Platform.Windowing;
using Annotator.Workspace.Capture;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Annotator.App.Shell;

/// <summary>
/// The explanation's content: the Mac's Screen Recording card (a 44-point tile with the display icon in blue at 12%, the title
/// and the detail), what Windows asks next, and Not now and Allow screen access, 20 points from the edges and 460 points
/// wide. While Windows asks, the line says so and both buttons are unavailable.
/// </summary>
internal sealed class ScreenAccessContent
{
    public const double Width = 460;

    private readonly Grid _root = new() { Width = Width };
    private readonly FormText _next;

    public ScreenAccessContent(Action allow, Action notNow)
    {
        var tile = new Grid
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(10),
            Background = SettingsLook.Tint(SettingsLook.Blue, 0.12),
            VerticalAlignment = VerticalAlignment.Top,
        };
        FrameworkElement icon = SettingsIcons.Make("display", 17, SettingsLook.Blue);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        tile.Children.Add(icon);
        AutomationProperties.SetAccessibilityView(tile, AccessibilityView.Raw);
        var texts = new MacStack { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new FormText(ScreenAccessText.Title, 13, weight: FontWeights.SemiBold));
        texts.Children.Add(new FormText(ScreenAccessText.Detail, 12, "AnnotatorSecondaryText", wrapWidth: Width - 40 - 44 - 12));
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(tile);
        Grid.SetColumn(texts, 1);
        header.Children.Add(texts);

        _next = new FormText(ScreenAccessText.Ask, 12, wrapWidth: Width - 40);

        NotNow = new SettingsButton(ScreenAccessText.NotNow, ButtonLook.Bordered);
        NotNow.Click += (_, _) => notNow();
        Allow = new SettingsButton(ScreenAccessText.Allow, ButtonLook.Prominent);
        Allow.Click += (_, _) => allow();
        var buttons = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(NotNow);
        buttons.Children.Add(Allow);

        var stack = new MacStack { Spacing = 16, Margin = new Thickness(20, 20, 20, 18) };
        stack.Children.Add(header);
        stack.Children.Add(_next);
        stack.Children.Add(buttons);
        _root.Children.Add(stack);
        _root.Background = SettingsLook.Window;
        _root.ActualThemeChanged += (_, _) => _root.Background = SettingsLook.Window;
    }

    public FrameworkElement Root => _root;

    public SettingsButton Allow { get; }

    public SettingsButton NotNow { get; }

    /// <summary>Windows is asking: the line says to answer it, and the buttons wait.</summary>
    public bool Asking
    {
        get => !Allow.IsEnabled;
        set
        {
            _next.Text = value ? ScreenAccessText.Asking : ScreenAccessText.Ask;
            Allow.IsEnabled = !value;
            NotNow.IsEnabled = !value;
        }
    }

    /// <summary>The content's height in points for its width.</summary>
    public double ContentHeight()
    {
        _root.Measure(new Windows.Foundation.Size(Width, double.PositiveInfinity));
        return Math.Ceiling(_root.DesiredSize.Height);
    }
}

/// <summary>
/// The explanation before Windows' screen capture questions (WIN-CAP-04; Windows only). Windows asks a packaged app only while
/// one of its windows is in front, and the toolbar never takes the focus (DV-1), so this window comes to the front itself, on
/// the display with the toolbar, above Annotator's other windows and above the middle of the work area, where Windows centers
/// its questions. It has a taskbar button, so Windows can flash it when it may not take the focus. Allow screen access asks
/// Windows; Not now, Escape and the close button end it without asking, except while Windows asks, when it stays until Windows
/// has the answer.
/// </summary>
internal sealed class ScreenAccessWindow : Window
{
    private readonly Func<RectInt32?> _anchor;
    private readonly Action _allow;
    private readonly Action _notNow;
    private readonly WindowRegistration _registration;
    private readonly Grid _root = new();
    private ScreenAccessContent _content;
    /// <summary>The content at a larger text size (WIN-A11Y-03), zoomed; null at 100%.</summary>
    private ZoomPanel? _zoom;
    private bool _focusOnLoad;
    private bool _closing;
    /// <summary>Shows the window cloaked until its content drew, and hides it by cloaking, so its first click works (WindowCloak).</summary>
    private readonly WindowCloak _cloak;

    /// <param name="anchor">The toolbar's rectangle in physical pixels, or null while it is hidden.</param>
    public ScreenAccessWindow(WindowRegistry registry, Func<RectInt32?> anchor, Action allow, Action notNow)
    {
        _anchor = anchor;
        _allow = allow;
        _notNow = notNow;
        Title = ScreenAccessText.WindowTitle;
        var presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppIcon.Apply(AppWindow);
        AppWindow.IsShownInSwitchers = true;
        Hwnd = new HWND(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        // Registered before the first show so capture can leave it out (S-1); above the toolbar, as the error window is.
        _registration = registry.Register((nint)Hwnd, WindowLayer.Error);
        _content = MakeContent(asking: false);
        _root.KeyDown += OnKeyDown;
        AppResources.FollowTheme(_root, AppWindow);
        AutomationFrames.Nudge(_root);
        Content = _root;
        _zoom = ZoomPanel.Host(this, _root, null, Windows.Foundation.Size.Empty);
        AppResources.SystemLookChanged += OnSystemLookChanged;
        _cloak = new WindowCloak(this, Hwnd, "the screen access explanation");
        AppWindow.Closing += (_, args) =>
        {
            if (!_closing)
            {
                args.Cancel = true;
                NotNow();
            }
        };
        Closed += (_, _) =>
        {
            AppResources.SystemLookChanged -= OnSystemLookChanged;
            _registration.Dispose();
        };
    }

    public HWND Hwnd { get; }

    public bool IsShown { get; private set; }

    public bool Asking => _content.Asking;

    /// <summary>
    /// Shows the explanation on the display with the toolbar (or the pointer), above where Windows will ask, and brings it to
    /// the front: the person's click or shortcut lets Annotator take the focus; otherwise Windows flashes its taskbar button.
    /// </summary>
    public void Present()
    {
        bool opening = !IsShown;
        if (opening)
        {
            _content.Asking = false;
            MonitorArea monitor = _anchor() is { } toolbar ? WindowChrome.MonitorFor(toolbar) : WindowChrome.MonitorAtCursor();
            // On the display first, so the size below uses its scale.
            AppWindow.Move(new PointInt32(monitor.WorkArea.X, monitor.WorkArea.Y));
            Place(monitor);
        }
        IsShown = true;
        _cloak.BeforeShow();
        AppWindow.Show(activateWindow: true);
        WindowChrome.KeepOnTop(Hwnd);
        PInvoke.SetForegroundWindow(Hwnd);
        if (!_content.Asking)
        {
            _focusOnLoad = !_content.Root.IsLoaded;
            if (!_focusOnLoad)
            {
                _content.Allow.Focus(FocusState.Programmatic);
            }
        }
    }

    /// <summary>Windows asks now: the explanation stays, says to answer, and waits.</summary>
    public void ShowAsking() => _content.Asking = true;

    /// <summary>
    /// Hides the explanation. It is cloaked rather than hidden, so it takes the first click as soon as a pixel feature shows it
    /// again (<see cref="WindowCloak"/>); Windows gives the keyboard to the window it chooses, as before.
    /// </summary>
    public void Hide()
    {
        if (!IsShown)
        {
            return;
        }
        IsShown = false;
        _content.Asking = false;
        _cloak.Hide();
    }

    /// <summary>Closes the window for good (Quit).</summary>
    public void Shutdown()
    {
        _closing = true;
        Close();
    }

    /// <summary>The content with the current colors, in the root; it takes the keyboard once loaded when asked to.</summary>
    private ScreenAccessContent MakeContent(bool asking)
    {
        var content = new ScreenAccessContent(_allow, NotNow) { Asking = asking };
        content.Root.HorizontalAlignment = HorizontalAlignment.Left;
        content.Root.VerticalAlignment = VerticalAlignment.Stretch;
        content.Root.Loaded += (_, _) =>
        {
            if (_focusOnLoad && ReferenceEquals(content, _content))
            {
                _focusOnLoad = false;
                content.Allow.Focus(FocusState.Programmatic);
            }
        };
        _root.Children.Clear();
        _root.Children.Add(content.Root);
        return content;
    }

    /// <summary>
    /// Sizes the window for its content, as large as the text size makes it while it fits in the work area, and places it above
    /// where Windows asks.
    /// </summary>
    private void Place(MonitorArea monitor)
    {
        double scale = monitor.Scale, zoom = 1;
        if (_zoom is not null)
        {
            // The text is measured at the size it shows at, zoomed.
            ZoomPanel.FreezeText(_root);
        }
        double height = _content.ContentHeight();
        if (_zoom is { } panel)
        {
            zoom = TextScaling.Fit(ScreenAccessContent.Width, height,
                new Windows.Foundation.Size(monitor.WorkArea.Width / scale - 32, monitor.WorkArea.Height / scale - 64));
            panel.Zoom = zoom;
        }
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(ScreenAccessContent.Width * zoom * scale), (int)Math.Round(height * zoom * scale)));
        RectInt32 work = monitor.WorkArea;
        SizeInt32 size = AppWindow.Size;
        (int x, int y) = ScreenAccessLayout.Origin(work.X, work.Y, work.Width, work.Height, size.Width, size.Height, scale);
        AppWindow.Move(new PointInt32(x, y));
    }

    /// <summary>
    /// Windows turned a contrast theme on or off or changed the text size: the content is made again with the new colors, and a
    /// shown explanation takes its new size where it is, without taking the focus (WIN-UI-07, WIN-A11Y-03).
    /// </summary>
    private void OnSystemLookChanged()
    {
        bool focused = IsShown && PInvoke.GetForegroundWindow() == Hwnd && !_content.Asking;
        _content = MakeContent(_content.Asking);
        _zoom = ZoomPanel.Host(this, _root, _zoom, Windows.Foundation.Size.Empty);
        if (IsShown)
        {
            Place(WindowChrome.MonitorFor(new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height)));
            _focusOnLoad = focused;
        }
    }

    private void NotNow()
    {
        // While Windows asks, its question belongs to this window: closing now could leave Windows without an answer.
        if (!_content.Asking)
        {
            _notNow();
        }
    }

    /// <summary>Enter is Allow screen access (the default button) where no button has the focus, and Escape is Not now.</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            NotNow();
        }
        else if (args.Key == VirtualKey.Enter && _content.Allow.IsEnabled && args.OriginalSource is not ButtonBase)
        {
            args.Handled = true;
            _allow();
        }
    }
}
