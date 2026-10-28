using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Workspace.Motion;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Glyphs = Annotator.App.Icons.Glyphs;

namespace Annotator.App.Settings;

/// <summary>
/// Port of the Mac ArrangerControlTile: one of the toolbar's own buttons in the arranger. In the strip it is drawn small,
/// as on the toolbar (Undo, Redo, Clear all, Properties, Settings and Hide as icons, More tools with its key, Board and
/// Draw as their buttons), with a red minus on hover that takes it off. In "More tools" it is a tile with its name and a
/// green plus that puts it back. Its key, if it has one, stays in "Keys for toolbar buttons". Dragging these buttons is
/// not ported yet; their badges, context menu and keys work.
/// </summary>
internal sealed partial class ArrangerControlTile : UserControl
{
    private readonly Action<ToolbarControl> _add;
    private readonly Action<ToolbarControl> _remove;
    private readonly Grid _root = new();
    private readonly Border _fill;
    private readonly Border _focus;
    private readonly Canvas _badgeLayer = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Width = 0, Height = 0 };
    private FrameworkElement? _badge;
    private bool _hovering;

    public ArrangerControlTile(ToolbarControl control, bool inToolbar, ToolbarMetrics metrics, string? libraryKey,
                               Action<ToolbarControl> add, Action<ToolbarControl> remove)
    {
        Control = control;
        InToolbar = inToolbar;
        _add = add;
        _remove = remove;
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        double radius = inToolbar ? 7 : 10;
        _fill = new Border { CornerRadius = new CornerRadius(radius) };
        _focus = new Border { CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(2), Opacity = 0, IsHitTestVisible = false };
        if (inToolbar)
        {
            (Width, Height) = control switch
            {
                ToolbarControl.Library => (metrics.ButtonWidth, metrics.ButtonHeight),
                ToolbarControl.Board => (metrics.BoardWidth, metrics.ButtonHeight),
                ToolbarControl.Mode => (metrics.ModeWidth, metrics.ButtonHeight),
                _ => (metrics.ActionSize.Width, metrics.ActionSize.Height),
            };
        }
        else
        {
            Width = 78;
            Height = 84;
        }
        _focus.BorderBrush = SettingsColors.AccentBrush();
        _root.Children.Add(_fill);
        _root.Children.Add(inToolbar ? Face(control, metrics, libraryKey) : TrayFace(control));
        _root.Children.Add(_focus);
        _root.Children.Add(_badgeLayer);
        Content = _root;
        ShowFill();
        AutomationProperties.SetName(this, control.Label);
        AutomationProperties.SetAutomationId(this, ToolbarArranger.ControlIdentifier(control, inToolbar));
        AutomationProperties.SetHelpText(this, inToolbar
            ? "It keeps its place in your toolbar. Use the Remove from toolbar action to take it off."
            : ToolbarArranger.TrayHintText);
        AutomationProperties.SetItemStatus(this, inToolbar ? "In your toolbar" : ToolbarArranger.TrayValue);
        ToolTipService.SetToolTip(this, inToolbar ? ToolbarArranger.ControlHelp(control) : ToolbarArranger.ControlTrayHelp(control));
        var menu = new MenuFlyout();
        menu.Items.Add(Library.LibraryParts.Item(inToolbar ? "Remove from toolbar" : "Add to toolbar", Act));
        ContextFlyout = menu;
        PointerEntered += (_, _) => Hover(true);
        PointerExited += (_, _) => Hover(false);
        GotFocus += (_, _) => _focus.Opacity = FocusState == FocusState.Keyboard ? 1 : 0;
        LostFocus += (_, _) => _focus.Opacity = 0;
        KeyDown += OnKeyDown;
    }

    public ToolbarControl Control { get; }

    public bool InToolbar { get; }

    /// <summary>The control as the toolbar draws it (Mac ArrangerControlTile.face).</summary>
    private static FrameworkElement Face(ToolbarControl control, ToolbarMetrics metrics, string? libraryKey)
    {
        MacStack Stack(double spacing) => new()
        {
            Spacing = spacing,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        switch (control)
        {
            case ToolbarControl.Library:
            {
                MacStack stack = Stack(2);
                stack.Children.Add(Centered(Glyphs.ForSymbol(control.Symbol, metrics.Minimal ? 16 : 19), metrics.Minimal ? 20 : 24));
                if (metrics.ShowsKeyLabels)
                {
                    TextBlock key = SettingsText.Static(libraryKey ?? " ", 10, "AnnotatorSecondaryText");
                    key.HorizontalAlignment = HorizontalAlignment.Center;
                    stack.Children.Add(key);
                }
                return stack;
            }
            case ToolbarControl.Board:
            {
                MacStack stack = Stack(3);
                stack.Children.Add(Centered(Glyphs.ForSymbol(control.Symbol, metrics.Minimal ? 15 : 17), metrics.Minimal ? 15 : 17));
                if (!metrics.Minimal)
                {
                    TextBlock desk = SettingsText.Static("Desk", 9, "AnnotatorPrimaryText", FontWeights.Medium);
                    desk.HorizontalAlignment = HorizontalAlignment.Center;
                    stack.Children.Add(desk);
                }
                return stack;
            }
            case ToolbarControl.Mode:
            {
                MacStack stack = Stack(metrics.Minimal ? 1 : 3);
                stack.Children.Add(Centered(Glyphs.ForSymbol("pencil.tip", metrics.Minimal ? 14 : 17), metrics.Minimal ? 14 : 17));
                var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = metrics.Minimal ? 3 : 4, HorizontalAlignment = HorizontalAlignment.Center };
                row.Children.Add(new Ellipse { Width = 5, Height = 5, Style = AppResources.Style("AnnotatorDotDraw"), VerticalAlignment = VerticalAlignment.Center });
                TextBlock draw = SettingsText.Static("Draw", metrics.Minimal ? 8 : 9, "AnnotatorPrimaryText", FontWeights.SemiBold);
                draw.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(draw);
                stack.Children.Add(row);
                var surface = new Grid { IsHitTestVisible = false };
                surface.Children.Add(new Border { Style = AppResources.Style("AnnotatorModeDraw"), CornerRadius = new CornerRadius(7) });
                surface.Children.Add(stack);
                return surface;
            }
            default:
                return Centered(Annotator.App.Toolbar.ToolbarParts.SymbolGlyph(control.Symbol), metrics.ActionSize.Height);
        }
    }

    /// <summary>The tray tile: the symbol at 20 points over the name, as the other tiles of More tools.</summary>
    private static FrameworkElement TrayFace(ToolbarControl control)
    {
        var stack = new MacStack { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        stack.Children.Add(Centered(Glyphs.ForSymbol(control.Symbol, 20 * 0.82), 24));
        TextBlock title = SettingsText.Static(control.Label, 10, "AnnotatorPrimaryText", FontWeights.Medium);
        title.TextWrapping = TextWrapping.Wrap;
        title.Height = double.NaN;
        title.MaxLines = 2;
        title.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center;
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.MaxWidth = 78;
        stack.Children.Add(new Grid { Width = 78, Height = 26, Children = { title } });
        return stack;
    }

    private static Grid Centered(FrameworkElement glyph, double height)
    {
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { Height = height, IsHitTestVisible = false, Children = { glyph } };
    }

    private void Act()
    {
        if (InToolbar)
        {
            _remove(Control);
        }
        else
        {
            _add(Control);
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!ReferenceEquals(args.OriginalSource, this))
        {
            return;
        }
        if ((InToolbar && args.Key is VirtualKey.Delete or VirtualKey.Back) || (!InToolbar && args.Key == VirtualKey.Enter))
        {
            args.Handled = true;
            Act();
        }
    }

    private void ShowFill() => _fill.Background = InToolbar
        ? SettingsColors.Primary(_hovering ? 0.06 : 0)
        : SettingsColors.Primary(_hovering ? 0.08 : 0.045);

    private void Hover(bool hovering)
    {
        _hovering = hovering;
        ShowFill();
        if (_badge is not null)
        {
            _badgeLayer.Children.Remove(_badge);
            _badge = null;
        }
        if (hovering)
        {
            ShowBadge();
        }
    }

    /// <summary>The red minus (13 points in the strip) or green plus (16 points in the tray) that takes it off or puts it back.</summary>
    private void ShowBadge()
    {
        double size = InToolbar ? 13 : 16;
        var disc = new Grid { Width = size, Height = size };
        disc.Children.Add(new Ellipse { Fill = SettingsColors.Black(0.2), Margin = new Thickness(0.5, 1.5, 0.5, -0.5) });
        disc.Children.Add(new Ellipse { Fill = new SolidColorBrush(InToolbar ? SettingsColors.Red : SettingsColors.Green) });
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        disc.Children.Add(new Rectangle { Width = size / 2, Height = 2, RadiusX = 1, RadiusY = 1, Fill = white });
        if (!InToolbar)
        {
            disc.Children.Add(new Rectangle { Width = 2, Height = size / 2, RadiusX = 1, RadiusY = 1, Fill = white });
        }
        string title = InToolbar ? $"Remove {Control.Label} from toolbar" : $"Add {Control.Label} to toolbar";
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorMenuRowButton"),
            CornerRadius = new CornerRadius(size / 2),
            Content = disc,
            Width = size,
            Height = size,
            AllowFocusOnInteraction = false,
        };
        button.Resources["AnnotatorMenuHoverBrush"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetName(button, title);
        ToolTipService.SetToolTip(button, InToolbar ? $"Remove {Control.Label} from your toolbar" : $"Add {Control.Label} to your toolbar");
        button.Click += (_, _) => Act();
        // Mac .offset(x: 6, y: -6) from the top-right corner.
        Canvas.SetLeft(button, -size + 6);
        Canvas.SetTop(button, -6);
        _badge = button;
        _badgeLayer.Children.Add(button);
        if (PageMotion.ReducesMotion)
        {
            PageMotion.Fade(button, 0, 1, ShortcutEditorMotion.Badge(reduceMotion: true));
        }
        else
        {
            button.Loaded += (_, _) =>
            {
                PageMotion.Scale(button, ShortcutEditorMotion.Badge(reduceMotion: false), share => 0.3 + 0.7 * share);
                PageMotion.Fade(button, 0, 1, ShortcutEditorMotion.Badge(reduceMotion: false));
            };
        }
    }
}
