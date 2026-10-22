using Annotator.App.Introduction;
using Annotator.Platform.Windowing;
using Annotator.Workspace.FirstUse;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Annotator.App.Shell;

/// <summary>
/// The coffee reminder (Mac SupportReminderView in the toolbar's popover, spec 005): "Enjoying Annotator?", one line, and
/// Buy me a coffee and Not now. It opens next to the toolbar without taking focus, like the Mac's popover over a panel that
/// never activates, and stays until the person answers or the workspace stops allowing it. Once the person clicks into it,
/// a click elsewhere or Escape closes it, as the Mac's transient popover closes; <see cref="Closed"/> then says so, and the
/// host snoozes the reminder.
/// </summary>
internal sealed class SupportReminderPopover : Window
{
    private readonly WindowRegistration _registration;
    private readonly FrameworkElement _surface;
    /// <summary>The surface at a larger text size (WIN-A11Y-03), zoomed; null at 100%.</summary>
    private readonly ZoomPanel? _zoom;
    /// <summary>Shows the reminder cloaked until its content drew, so its first click works (WindowCloak).</summary>
    private readonly WindowCloak _cloak;
    private bool _activated;
    private bool _closing;

    /// <param name="support">Opens the support link; true when it opened (Mac openOptionalSupport).</param>
    /// <param name="notNow">Mac reminder.snooze() from Not now.</param>
    public SupportReminderPopover(WindowRegistry registry, Func<bool> support, Action notNow)
    {
        Title = SupportReminderText.Title;
        Hwnd = WindowChrome.Configure(this, noActivate: false);
        // Registered before the first show so capture can leave it out (S-1).
        _registration = registry.Register((nint)Hwnd, WindowLayer.Popover);
        _surface = new Border
        {
            Style = AppResources.Style("AnnotatorToolbarSurface"),
            Child = Body(support, notNow),
        };
        var root = new WindowRoot { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        root.Children.Add(_surface);
        AutomationFrames.Nudge(root);
        root.KeyDown += OnKeyDown;
        _zoom = TextScaling.Zoom > 1 ? new ZoomPanel(root, TextScaling.Zoom) : null;
        Content = _zoom ?? (UIElement)root;
        _cloak = new WindowCloak(this, Hwnd, "the coffee reminder");
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                _activated = true;
            }
            else if (_activated)
            {
                Dismiss(byPerson: true);
            }
        };
        Closed += (_, _) => _registration.Dispose();
    }

    public HWND Hwnd { get; }

    /// <summary>Raised once when the popover closed; true when the person closed it (Escape or a click elsewhere).</summary>
    public event Action<bool>? Dismissed;

    /// <summary>
    /// Mac SupportReminderView: 16 points of padding around a 300-point column, 10 points apart: the headline, the callout
    /// in the secondary color, and the small buttons, Buy me a coffee prominent in the coffee brown.
    /// </summary>
    public static FrameworkElement Body(Func<bool> support, Action notNow)
    {
        var column = new MacStack { Spacing = 10, Width = 300 - 32 };
        column.Children.Add(TourParts.Text(SupportReminderText.Title, 13, weight: FontWeights.SemiBold, wrap: false));
        column.Children.Add(TourParts.Text(SupportReminderText.Detail, 12, TourParts.Secondary));
        var buttons = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button coffee = Small(SupportReminderText.Support);
        Tint(coffee);
        coffee.Click += (_, _) => support();
        Button later = Small(SupportReminderText.NotNow);
        AutomationProperties.SetHelpText(later, SupportReminderText.NotNowHelp);
        ToolTipService.SetToolTip(later, SupportReminderText.NotNowHelp);
        later.Click += (_, _) => notNow();
        buttons.Children.Add(coffee);
        buttons.Children.Add(later);
        column.Children.Add(buttons);
        var group = new AccessibleGroup(string.Empty, SupportReminderText.Identifier);
        group.Children.Add(column);
        return new Border { Padding = new Thickness(16), Child = group };
    }

    /// <summary>
    /// Shows the reminder above the toolbar, or below it when there is no room (Mac popover arrow edge), without activation.
    /// </summary>
    public void ShowNear(RectInt32 anchor)
    {
        var unlimited = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        _surface.Measure(unlimited);
        if (_zoom is not null && ZoomPanel.FreezeText(_surface))
        {
            _surface.Measure(unlimited);
        }
        MonitorArea monitor = WindowChrome.MonitorFor(anchor);
        Windows.Foundation.Size desired = _surface.DesiredSize;
        double zoom = 1;
        if (_zoom is { } panel)
        {
            zoom = TextScaling.Fit(desired.Width, desired.Height,
                new Windows.Foundation.Size(monitor.WorkArea.Width / monitor.Scale - 16, monitor.WorkArea.Height / monitor.Scale - 16));
            panel.Zoom = zoom;
        }
        int width = (int)Math.Ceiling(desired.Width * zoom * monitor.Scale);
        int height = (int)Math.Ceiling(desired.Height * zoom * monitor.Scale);
        int gap = (int)Math.Round(8 * monitor.Scale);
        RectInt32 work = monitor.WorkArea;
        bool above = anchor.Y - gap - height >= work.Y;
        int x = Math.Clamp(anchor.X + anchor.Width / 2 - width / 2, work.X + gap, Math.Max(work.X + gap, work.X + work.Width - width - gap));
        int y = above ? anchor.Y - gap - height : anchor.Y + anchor.Height + gap;
        y = Math.Clamp(y, work.Y + gap, Math.Max(work.Y + gap, work.Y + work.Height - height - gap));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        _cloak.BeforeShow();
        AppWindow.Show(activateWindow: false);
        WindowChrome.KeepOnTop(Hwnd);
    }

    /// <summary>Closes the popover; <paramref name="byPerson"/> is passed to <see cref="Dismissed"/>.</summary>
    public void Dismiss(bool byPerson)
    {
        if (_closing)
        {
            return;
        }
        _closing = true;
        Close();
        Dismissed?.Invoke(byPerson);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            Dismiss(byPerson: true);
        }
    }

    private static Button Small(string title)
    {
        // SwiftUI's small control size: 11-point text on a 20-point button.
        var button = new Button
        {
            Content = title,
            FontSize = 11,
            Height = 20,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(8, 0, 8, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, title);
        return button;
    }

    /// <summary>
    /// borderedProminent tinted with the coffee brown (Mac CoffeeSupportView.accent), white text. A contrast theme shows the
    /// prominent button in its Highlight colors instead (WIN-UI-07).
    /// </summary>
    private static void Tint(Button button)
    {
        button.Style = AppResources.Style("AccentButtonStyle");
        button.Height = 20;
        button.MinHeight = 0;
        button.Padding = new Thickness(8, 0, 8, 1);
        if (ContrastTheme.IsOn)
        {
            return;
        }
        Color brown = Color.FromArgb(255, (byte)Math.Round(0.58 * 255), (byte)Math.Round(0.29 * 255), (byte)Math.Round(0.10 * 255));
        Color hover = Color.FromArgb(230, brown.R, brown.G, brown.B);
        Color pressed = Color.FromArgb(204, brown.R, brown.G, brown.B);
        var white = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        button.Resources["AccentButtonBackground"] = new SolidColorBrush(brown);
        button.Resources["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(hover);
        button.Resources["AccentButtonBackgroundPressed"] = new SolidColorBrush(pressed);
        button.Resources["AccentButtonForeground"] = white;
        button.Resources["AccentButtonForegroundPointerOver"] = white;
        button.Resources["AccentButtonForegroundPressed"] = white;
        button.Resources["AccentButtonBorderBrush"] = new SolidColorBrush(brown);
        button.Resources["AccentButtonBorderBrushPointerOver"] = new SolidColorBrush(hover);
        button.Resources["AccentButtonBorderBrushPressed"] = new SolidColorBrush(pressed);
    }
}
