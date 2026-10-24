using Annotator.App.Settings;
using Annotator.App.Toolbar;
using Annotator.Workspace.FirstUse;
using Annotator.Workspace.Settings;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.App.Shell;

/// <summary>
/// What's changed after an update (the Mac's WhatsChangedController and WhatsChangedView): a small window titled What's
/// changed, centered on the display under the pointer, with "Annotator is updated", the version, this version's notes,
/// Known limits closed, the coffee line and Close. It shows without taking focus, once per version (the host decides when),
/// and closing it releases it. The content rises and fades in unless Windows' animation effects are off.
/// </summary>
internal sealed class WhatsChangedWindow : Window
{
    private readonly FrameworkElement _root;
    private readonly WindowCloak _cloak;
    private ZoomPanel? _zoom;
    private bool _closed;

    /// <param name="animate">Plays the entrance; the UX reference captures the settled window.</param>
    public WhatsChangedWindow(ReleaseNotesEntry entry, SettingsActions actions, bool animate = true)
    {
        Title = WhatsChangedText.Title;
        OverlappedPresenter presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        Hwnd = new HWND(Win32Interop.GetWindowFromWindowId(AppWindow.Id));
        _root = Body(entry, actions.Environment.SupportLink is not null, actions.BuyCoffee, Close, animate && !ToolbarMotion.ReducesMotion());
        Content = _root;
        _zoom = ZoomPanel.Host(this, _root, null, Windows.Foundation.Size.Empty);
        _cloak = new WindowCloak(this, Hwnd, "What's changed");
        Closed += (_, _) =>
        {
            _closed = true;
            Dismissed?.Invoke();
        };
    }

    public HWND Hwnd { get; }

    /// <summary>Raised once when the window closed (Close, the close button or Quit).</summary>
    public event Action? Dismissed;

    public bool IsShown => !_closed;

    /// <summary>Shows the window centered without taking focus (the Mac's orderFrontRegardless).</summary>
    public void Present()
    {
        Place();
        _cloak.BeforeShow();
        AppWindow.Show(activateWindow: false);
        var notTopmost = new HWND((nint)(-2));
        const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
        if (!PInvoke.SetWindowPos(Hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0, flags) || !PInvoke.SetWindowPos(Hwnd, notTopmost, 0, 0, 0, 0, flags))
        {
            StartupLog.Note("whats-changed-raise", $"SetWindowPos failed with error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        }
    }

    /// <summary>
    /// The Mac's WhatsChangedView: a 480 by 560 point column. A scrolling area (24 points of padding) holds the 56-point app
    /// icon beside the headline (17-point bold) and the version (13-point secondary), then the notes in 12-point text with
    /// 11-point bold categories, 10 points apart, and Known limits closed; below a divider, the coffee box (16 points in, 14
    /// from the top) and Close at the bottom right (16 points in).
    /// </summary>
    internal static FrameworkElement Body(ReleaseNotesEntry entry, bool supportAvailable, Action buyCoffee, Action close, bool animate)
    {
        const double width = WhatsChangedText.Width;
        const double textWidth = width - 48;
        var notes = new MacStack { Spacing = 10, Margin = new Thickness(24) };
        var header = new MacStack { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new AppIconView(56));
        var titles = new MacStack { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new FormText(WhatsChangedText.Heading, 17, weight: FontWeights.Bold, heading: true));
        titles.Children.Add(new FormText(WhatsChangedText.Version(entry), 13, "AnnotatorSecondaryText", wrapWidth: textWidth - 70));
        header.Children.Add(titles);
        notes.Children.Add(header);
        foreach ((string category, IReadOnlyList<string> items) in entry.Changes)
        {
            notes.Children.Add(new FormText(category, 11, weight: FontWeights.Bold));
            foreach (string item in items)
            {
                notes.Children.Add(Selectable(new FormText(item, 12, wrapWidth: textWidth)));
            }
        }
        if (entry.KnownIssues.Count > 0)
        {
            notes.Children.Add(new SettingsDisclosure(WhatsChangedText.KnownLimits, WhatsChangedText.KnownLimitsIdentifier, 12, () =>
            {
                var limits = new MacStack { Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
                foreach (string item in entry.KnownIssues)
                {
                    limits.Children.Add(Selectable(new FormText(item, 12, wrapWidth: textWidth - 12)));
                }
                return limits;
            }, contentTop: 0));
        }
        var scroll = new ScrollViewer
        {
            Content = notes,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var closeButton = new SettingsButton(WhatsChangedText.Close, ButtonLook.Prominent, SettingsWidths.Button(WhatsChangedText.Close))
        {
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetAutomationId(closeButton, WhatsChangedText.CloseIdentifier);
        closeButton.Click += (_, _) => close();
        FrameworkElement coffee = GeneralPage.CoffeeView(supportAvailable, buyCoffee, width - 32);
        // The coffee box gets the column's exact width (480 less 16 on each side); its second line wraps to fit.
        coffee.Width = width - 32;

        var grid = new Grid { Width = width, Height = WhatsChangedText.Height, Background = SettingsLook.Window };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var divider = new Rectangle { Height = 1, HorizontalAlignment = HorizontalAlignment.Stretch, Style = AppResources.Style("AnnotatorDivider") };
        var coffeePad = new Pad(coffee, new Thickness(16, 14, 16, 0)) { HorizontalAlignment = HorizontalAlignment.Left };
        var closePad = new Pad(closeButton, new Thickness(16)) { HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(scroll, 0);
        Grid.SetRow(divider, 1);
        Grid.SetRow(coffeePad, 2);
        Grid.SetRow(closePad, 3);
        grid.Children.Add(scroll);
        grid.Children.Add(divider);
        grid.Children.Add(coffeePad);
        grid.Children.Add(closePad);
        AutomationProperties.SetAutomationId(grid, WhatsChangedText.Identifier);
        AutomationProperties.SetName(grid, WhatsChangedText.Title);
        if (animate)
        {
            Enter(notes, 18);
            Enter(header, 10);
            grid.Opacity = 0;
            grid.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(320) };
            grid.Loaded += (_, _) => grid.Opacity = 1;
        }
        return grid;
    }

    /// <summary>The Mac's spring entrance: the part rises the given points into place once the window has loaded.</summary>
    private static void Enter(FrameworkElement part, float rise)
    {
        part.Translation = new System.Numerics.Vector3(0, rise, 0);
        part.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(420) };
        part.Loaded += (_, _) => part.Translation = System.Numerics.Vector3.Zero;
    }

    private static FormText Selectable(FormText text)
    {
        text.Block.IsTextSelectionEnabled = true;
        return text;
    }

    /// <summary>Mac panel.center(): the 480 by 560 point content, centered in the work area of the display under the pointer.</summary>
    private void Place()
    {
        MonitorArea monitor = WindowChrome.MonitorAtCursor();
        double scale = monitor.Scale;
        RectInt32 work = monitor.WorkArea;
        double zoom = 1;
        if (_zoom is { } panel)
        {
            zoom = TextScaling.Fit(WhatsChangedText.Width, WhatsChangedText.Height,
                new Windows.Foundation.Size(work.Width / scale - 32, work.Height / scale - 90));
            panel.Zoom = zoom;
        }
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(WhatsChangedText.Width * zoom * scale),
                                             (int)Math.Round(WhatsChangedText.Height * zoom * scale)));
        SizeInt32 outer = AppWindow.Size;
        int x = work.X + Math.Max(0, (work.Width - outer.Width) / 2);
        int y = work.Y + Math.Max(0, (work.Height - outer.Height) / 2);
        AppWindow.Move(new PointInt32(x, y));
    }
}
