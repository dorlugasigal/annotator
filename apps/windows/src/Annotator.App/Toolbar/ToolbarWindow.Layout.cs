using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Workspace;
using Annotator.Workspace.Motion;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Glyphs = Annotator.App.Icons.Glyphs;

namespace Annotator.App.Toolbar;

internal sealed partial class ToolbarWindow
{
    /// <summary>
    /// Replaces the toolbar's controls. Values that change without a new structure go to <see cref="_updates"/>.
    /// <paramref name="revealedFrom"/> is the properties layout the person just left, so the new properties fade in.
    /// </summary>
    private void Build(ToolbarLook look, WorkspaceSnapshot snapshot, ToolbarPropertiesMode? revealedFrom)
    {
        _updates.Clear();
        _root.Children.Clear();
        _root.Children.Add(look.Collapsed ? Collapsed(look)
            : look.Vertical ? Vertical(look, snapshot, revealedFrom) : Horizontal(look, snapshot, revealedFrom));
    }

    /// <summary>
    /// ToolbarView's horizontal body: the main row, the narrow toolbar's second row, the active board and the properties
    /// row. The toolbar's own buttons that are off in Settings are left out. The properties are one row; the toolbar grows
    /// sideways to fit it (Mac ToolbarWidthLayout), and the room goes between the tools and the buttons.
    /// </summary>
    private FrameworkElement Horizontal(ToolbarLook look, WorkspaceSnapshot snapshot, ToolbarPropertiesMode? revealedFrom)
    {
        ToolbarMetrics metrics = look.Metrics;
        double width = look.Width;
        FrameworkElement? properties = null;
        FrameworkElement? link = null;
        if (look.Expanded)
        {
            properties = PropertiesPanel.Row(this, snapshot, _updates, metrics.PropertiesScale);
            properties.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            if (look.SharedStyle)
            {
                link = LinkMark();
                link.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            }
            double ideal = properties.DesiredSize.Width + FullRowGaps + MenusWidth(look, link) + metrics.Padding * 2;
            width = ToolbarToolLayout.GrownWidth(look.Width, look.WidthLimit, ideal);
        }
        double inner = width - metrics.Padding * 2;
        var content = new MacStack { Spacing = 8, Width = inner };
        var main = new MacStack { Orientation = Orientation.Horizontal, Spacing = metrics.Spacing, Width = inner, HorizontalAlignment = HorizontalAlignment.Left };
        main.Children.Add(Rail(look, snapshot));
        if (look.Shows(ToolbarControl.Library))
        {
            main.Children.Add(Library(look, snapshot));
        }
        // When full properties make the toolbar wider, the room goes between the tools and the buttons.
        main.Children.Add(new MacSpacer());
        if (look.Shows(ToolbarControl.Board) || look.Shows(ToolbarControl.Mode))
        {
            main.Children.Add(ToolbarParts.Divider(width: 1, height: metrics.Minimal ? 22 : 28));
        }
        if (look.Shows(ToolbarControl.Board))
        {
            main.Children.Add(BoardButton(look));
        }
        if (look.Shows(ToolbarControl.Mode))
        {
            main.Children.Add(ModeButton(look));
        }
        if (!look.CompactRail)
        {
            foreach (FrameworkElement action in Actions(look, snapshot, history: true, rest: true))
            {
                main.Children.Add(action);
            }
        }
        content.Children.Add(main);
        if (look.CompactRail && look.HasTrailingControls)
        {
            var row = new Grid { ColumnSpacing = metrics.Spacing };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var actions = new MacStack { Orientation = Orientation.Horizontal, Spacing = metrics.Spacing, VerticalAlignment = VerticalAlignment.Center };
            foreach (FrameworkElement action in Actions(look, snapshot, history: true, rest: true))
            {
                actions.Children.Add(action);
            }
            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);
            content.Children.Add(row);
        }
        if (look.ShowsModeStrip)
        {
            content.Children.Add(ModeStrip(look));
        }
        if (properties is not null)
        {
            content.Children.Add(ToolbarParts.Divider(height: 1));
            FrameworkElement full = FullPropertiesRow(look, properties, link, inner);
            content.Children.Add(full);
            if (revealedFrom is not null)
            {
                PropertiesReveal.Start(full);
            }
        }
        if (look.CaptureWarning is { } warning)
        {
            content.Children.Add(CaptureWarning(warning));
        }
        return Surface(content, metrics.Padding, metrics.CornerRadius, width, marker: true);
    }

    /// <summary>The gaps around the spacer between the properties and the selection's menu (Mac HStack(spacing: 12)).</summary>
    private const double FullRowGaps = 24;

    /// <summary>The selection's menu button at the end of the full properties row.</summary>
    private const double SelectionMenuWidth = 37;

    /// <summary>The end of the properties row: the shared-style link mark, then the selection's menu, 10 points apart.</summary>
    private static double MenusWidth(ToolbarLook look, FrameworkElement? link)
    {
        double width = look.HasSelection ? SelectionMenuWidth : 0;
        if (link is not null)
        {
            width += link.DesiredSize.Width + (look.HasSelection ? 10 : 0);
        }
        return width;
    }

    /// <summary>
    /// Mac fullPropertiesRow, "Tool properties": the properties on one row with the shared-style link mark and the
    /// selection's menu at its end, 5 points above the row's bottom. On a screen too narrow for the row, it scrolls
    /// sideways without a scroll bar instead of wrapping.
    /// </summary>
    private FrameworkElement FullPropertiesRow(ToolbarLook look, FrameworkElement properties, FrameworkElement? link, double inner)
    {
        properties.VerticalAlignment = VerticalAlignment.Bottom;
        var menus = new MacStack
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Height = 26,
            Margin = new Thickness(0, 0, 0, 5),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        if (link is not null)
        {
            menus.Children.Add(link);
        }
        if (look.HasSelection)
        {
            menus.Children.Add(SelectionMenu());
        }
        double natural = properties.DesiredSize.Width + FullRowGaps + MenusWidth(look, link);
        bool fits = natural <= inner + 0.5;
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 12, Width = fits ? inner : natural };
        row.Children.Add(properties);
        row.Children.Add(new MacSpacer());
        row.Children.Add(menus);
        var group = new AccessibleGroup("Tool properties", "contextual-inspector-viewport");
        if (fits)
        {
            group.Children.Add(row);
            return group;
        }
        group.Children.Add(new ScrollViewer
        {
            Width = inner,
            Content = row,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = false,
        });
        return group;
    }

    /// <summary>
    /// Mac needsCaptureRecovery, horizontal: Label(warning, systemImage: "exclamationmark.shield") in the caption font and the
    /// secondary color, on its own line. UI Automation reads the label's words over its whole frame, then the symbol.
    /// </summary>
    private static FrameworkElement CaptureWarning(string warning)
    {
        const double Gap = 9;
        var row = new Grid { Height = 13, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1, 0, 0, 0) };
        var text = new AccessibleText(warning) { HorizontalAlignment = HorizontalAlignment.Left };
        var line = new MacStack { Orientation = Orientation.Horizontal, Spacing = Gap, IsHitTestVisible = false };
        FrameworkElement shield = ToolbarParts.GlyphBox("exclamationmark.shield", 8.5, 10, 10, "AnnotatorGlyphSecondary");
        shield.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(shield);
        line.Children.Add(ToolbarParts.Label(warning, 10, "AnnotatorSecondaryText"));
        text.Children.Add(line);
        row.Children.Add(text);
        var image = new AccessibleImage("Exclamation Mark In A Shield", "exclamationmark.shield", null)
        {
            Width = 8.5,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        row.Children.Add(image);
        return row;
    }

    /// <summary>
    /// Mac needsCaptureRecovery on the vertical rail: the orange warning triangle, 44 by 28 points, whose help says why zoom
    /// failed to start, or else names capture as unavailable.
    /// </summary>
    private static FrameworkElement RailCaptureWarning(WorkspaceSnapshot snapshot)
    {
        string help = snapshot.ZoomStart is { Phase: Annotator.Workspace.Zoom.ZoomStartPhase.Failed, Message: { } failure }
            ? failure : WorkspaceModel.CaptureUnavailableWarning;
        var image = new AccessibleImage("Live pixels unavailable", null, help)
        {
            Width = 44,
            Height = 28,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        FrameworkElement triangle = Glyphs.ForSymbol("exclamationmark.triangle.fill", 12, "AnnotatorGlyphOrange");
        triangle.HorizontalAlignment = HorizontalAlignment.Center;
        triangle.VerticalAlignment = VerticalAlignment.Center;
        image.Children.Add(triangle);
        ToolbarParts.SetToolTip(image, help);
        return image;
    }

    /// <summary>ToolbarView's vertical body: the rail, the active board and the attached properties side by side.</summary>
    private FrameworkElement Vertical(ToolbarLook look, WorkspaceSnapshot snapshot, ToolbarPropertiesMode? revealedFrom)
    {
        ToolbarMetrics metrics = look.Metrics;
        var row = new MacStack
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Top,
            FlowDirection = look.RightDocked ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
        };
        var rail = new MacStack { Spacing = look.Available.Height < 500 || metrics.Minimal ? 3 : 5, Width = metrics.ButtonWidth };
        var tools = new MacStack { Spacing = 3, Width = metrics.ButtonWidth };
        foreach (ShortcutCommand item in look.Visible)
        {
            tools.Children.Add(Item(look, snapshot, item));
        }
        rail.Children.Add(tools);
        rail.Children.Add(ToolbarParts.Divider(height: 1));
        foreach (FrameworkElement element in Actions(look, snapshot, history: false, rest: false, properties: true))
        {
            rail.Children.Add(element);
        }
        if (look.Shows(ToolbarControl.Board))
        {
            rail.Children.Add(BoardButton(look));
        }
        if (look.Shows(ToolbarControl.Mode))
        {
            rail.Children.Add(ModeButton(look));
        }
        var history = new MacStack
        {
            Orientation = metrics.Minimal ? Orientation.Vertical : Orientation.Horizontal,
            Spacing = metrics.Minimal ? 3 : 0,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        foreach (FrameworkElement element in Actions(look, snapshot, history: true, rest: false, clear: false))
        {
            history.Children.Add(element);
        }
        if (history.Children.Count == 0)
        {
            // Undo and Redo are both off.
        }
        else if (metrics.Minimal || history.Children.Count == 1)
        {
            rail.Children.Add(history);
        }
        else
        {
            // Two 24-point buttons overhang the 44-point rail by 2 points on each side, as on the Mac. A canvas lets
            // them, where a negative margin would clip them to the rail.
            Canvas.SetLeft(history, -2);
            var overhang = new Canvas { Width = metrics.ButtonWidth, Height = metrics.ActionSize.Height };
            overhang.Children.Add(history);
            rail.Children.Add(overhang);
        }
        if (look.Shows(ToolbarControl.Library))
        {
            rail.Children.Add(Library(look, snapshot));
        }
        if (look.CaptureWarning is not null)
        {
            rail.Children.Add(RailCaptureWarning(snapshot));
        }
        foreach (FrameworkElement element in Actions(look, snapshot, history: false, rest: true, clear: true))
        {
            rail.Children.Add(element);
        }
        foreach (UIElement child in rail.Children)
        {
            if (child is FrameworkElement element && element != tools && element != history && element is not Rectangle)
            {
                element.HorizontalAlignment = HorizontalAlignment.Center;
            }
        }
        var railGroup = new AccessibleGroup("Drawing tool rail") { FlowDirection = FlowDirection.LeftToRight, VerticalAlignment = VerticalAlignment.Top };
        railGroup.Children.Add(Surface(rail, metrics.Padding, 15, metrics.VerticalRailWidth, marker: true));
        row.Children.Add(railGroup);
        if (look.ShowsModeStrip)
        {
            FrameworkElement strip = ModeStrip(look);
            strip.Width = 44;
            row.Children.Add(Surface(strip, 12, 15, 68));
        }
        if (look.Expanded)
        {
            double width = look.VerticalPropertyWidth;
            var panel = new MacStack { Spacing = 8, Width = width - 24 };
            panel.Children.Add(ToolTitle(look));
            ScrollViewer viewport = PropertiesPanel.Viewport(this, snapshot, width - 24, look.InspectorLimit, _updates);
            panel.Children.Add(viewport);
            var group = new AccessibleGroup("Attached tool properties", "attached-tool-properties") { FlowDirection = FlowDirection.LeftToRight };
            group.Children.Add(Surface(panel, 12, 15, width));
            group.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(group);
            if (revealedFrom == ToolbarPropertiesMode.Hidden)
            {
                PropertiesReveal.Start(group);
            }
        }
        return row;
    }

    private static FrameworkElement Centered(FrameworkElement element)
    {
        var host = new Grid { Width = 44 };
        element.HorizontalAlignment = HorizontalAlignment.Center;
        host.Children.Add(element);
        return host;
    }

    /// <summary>
    /// The Mac's window surface: the window background with a 7% outline. The outline is an overlay that takes no
    /// layout space, like the Mac's strokeBorder, and the padding is an exact margin: a 1-point border is 1.25 pixels
    /// at 125%, and WinUI's rounding of it shortened the surface. The tool rail's surface holds the selection marker
    /// between its background and its controls.
    /// </summary>
    private Grid Surface(FrameworkElement content, double padding, double cornerRadius, double width, bool marker = false)
    {
        var surface = new Grid { Width = width, VerticalAlignment = VerticalAlignment.Top };
        surface.Children.Add(new Border
        {
            Background = AppResources.Brush("AnnotatorWindowBackgroundBrush"),
            CornerRadius = new CornerRadius(cornerRadius),
        });
        if (marker)
        {
            surface.Children.Add(_markers.Layer());
        }
        content.Margin = new Thickness(padding);
        surface.Children.Add(content);
        surface.Children.Add(new Border
        {
            BorderBrush = AppResources.Brush("AnnotatorSurfaceStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(cornerRadius - 1),
            IsHitTestVisible = false,
        });
        Watch(surface);
        return surface;
    }

    /// <summary>The horizontal tool rail: the visible items, with a divider between families.</summary>
    private FrameworkElement Rail(ToolbarLook look, WorkspaceSnapshot snapshot)
    {
        ToolbarMetrics metrics = look.Metrics;
        var rail = new MacStack
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Width = look.RailWidth,
            Height = metrics.ButtonHeight,
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (int index = 0; index < look.Visible.Length; index++)
        {
            if (index > 0 && ToolbarToolLayout.Family(look.Visible[index]) != ToolbarToolLayout.Family(look.Visible[index - 1]))
            {
                Rectangle divider = ToolbarParts.Divider(width: 1, height: metrics.Minimal ? 20 : 24);
                divider.Margin = new Thickness(metrics.DividerPadding, 0, metrics.DividerPadding, 0);
                rail.Children.Add(divider);
            }
            rail.Children.Add(Item(look, snapshot, look.Visible[index]));
        }
        return rail;
    }

    private FrameworkElement Item(ToolbarLook look, WorkspaceSnapshot snapshot, ShortcutCommand item) =>
        item.Tool is { } tool ? ToolButton(look, snapshot, tool)
        : item.Action is { IsMenu: true } menu ? MenuButton(look, snapshot, menu)
        : ActionButton(look, snapshot, item.Action!.Value);

    /// <summary>Mac toolButton: the glyph, the key and the selection marker; Effects stands for every effect without its own button.</summary>
    private FrameworkElement ToolButton(ToolbarLook look, WorkspaceSnapshot snapshot, Tool tool)
    {
        ToolbarMetrics metrics = look.Metrics;
        Tool represented = tool == Tool.Rectangle && look.RectangleShows is { } shown ? shown : tool;
        string key = ToolbarLook.Key(snapshot.Preferences, ShortcutCommand.ForTool(tool), look.RectangleShows);
        Tool choice = tool == Tool.Cover ? WorkspaceModel.PreferredEffect(snapshot.Preferences) : represented;
        // Text needs the Text Services Framework editor (spike S-7); until it exists the tool says why it is off.
        bool textUnavailable = choice == Tool.Text && !snapshot.TextEditingAvailable;
        bool available = WorkspaceModel.IsAvailable(choice) && !textUnavailable;
        double radius = metrics.Minimal ? 8 : 10;
        var grid = new Grid { Width = metrics.ButtonWidth, Height = metrics.ButtonHeight };
        // A tool that is not built yet (Paste image) looks unavailable, like the Tool library.
        grid.Children.Add(Stacked(Glyphs.ForTool(represented, metrics.GlyphSize, available ? "AnnotatorGlyph" : "AnnotatorGlyphDisabled"), metrics, key));
        string name = tool == Tool.Cover ? "Effects" : represented.Label;
        ParityButton button = ToolbarParts.Button(metrics.ButtonWidth, metrics.ButtonHeight, grid, name, radius);
        button.AccessibleSelected = false;
        if (textUnavailable)
        {
            ToolbarParts.Help(button, WorkspaceModel.TextUnavailableMessage, WorkspaceModel.TextUnavailableMessage);
        }
        else
        {
            ToolbarParts.Help(button, key.Length == 0 ? null : $"Shortcut {key}",
                tool == Tool.Cover ? $"Effects ({key}) — Blur, Pixelate or Distort" : key.Length == 0 ? represented.Label : $"{represented.Label} ({key})");
        }
        button.IsEnabled = available;
        button.Click += (_, _) =>
        {
            _interacted();
            // Effects (Cover) chooses the last effect in the workspace, as the Mac's chooseCover does.
            _model.Choose(represented);
        };
        ShortcutCommand[] items = look.Items;
        _updates.Add(state =>
        {
            bool current = state.Tool == represented ||
                (tool == Tool.Cover && state.Tool.IsProtection && !Array.Exists(items, item => item.Tool == state.Tool));
            button.AccessibleSelected = current;
            _markers.Update(grid, current, state.Mode == WorkspaceMode.Draw, radius);
        });
        if (!textUnavailable)
        {
            return button;
        }
        // A disabled button shows no tooltip, so a transparent slot around it explains why Text is off.
        var slot = new Border { Child = button, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        ToolbarParts.SetToolTip(slot, WorkspaceModel.TextUnavailableMessage);
        return slot;
    }

    /// <summary>The glyph over the key label, as the Mac's VStack(spacing: 2) of a tool or button.</summary>
    private static MacStack Stacked(FrameworkElement glyph, ToolbarMetrics metrics, string key)
    {
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var host = new Grid { Height = metrics.Minimal ? 22 : 24 };
        host.Children.Add(glyph);
        var stack = new MacStack { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        stack.Children.Add(host);
        if (metrics.ShowsKeyLabels)
        {
            TextBlock label = ToolbarParts.Label(key.Length == 0 ? " " : key, 10, "AnnotatorSecondaryText");
            label.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(label);
        }
        return stack;
    }

    /// <summary>
    /// Mac actionButton: a toolbar button such as Spotlight, Cursor halo or Smart draw, On or Off. Buttons whose feature is not
    /// built are unavailable.
    /// </summary>
    private ToggleButton ActionButton(ToolbarLook look, WorkspaceSnapshot snapshot, ShortcutAction action)
    {
        ToolbarMetrics metrics = look.Metrics;
        string key = snapshot.Preferences.ShortcutFor(ShortcutCommand.ForAction(action)) is { } shortcut
            ? Annotator.Workspace.Shortcuts.WindowsShortcuts.Label(shortcut) : string.Empty;
        double radius = metrics.Minimal ? 8 : 10;
        var marker = new Border { CornerRadius = new CornerRadius(radius), Background = AppResources.Brush("AnnotatorSelectionFillBrush") };
        var grid = new Grid { Width = metrics.ButtonWidth, Height = metrics.ButtonHeight };
        grid.Children.Add(marker);
        var glyph = new Grid { IsHitTestVisible = false };
        grid.Children.Add(Stacked(glyph, metrics, key));
        var button = new ToggleButton
        {
            Style = AppResources.Style("AnnotatorToolButton"),
            Width = metrics.ButtonWidth,
            Height = metrics.ButtonHeight,
            CornerRadius = new CornerRadius(radius),
            Content = grid,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = WorkspaceModel.IsAvailable(action),
        };
        // Mac: the on state's fill and accent symbol change with selectionAnimation.
        double size = metrics.Minimal ? 15 : 17;
        FrameworkElement plain = Glyphs.ForSymbol(action.Symbol, size, button.IsEnabled ? "AnnotatorGlyph" : "AnnotatorGlyphDisabled");
        FrameworkElement accent = Glyphs.ForSymbol(action.Symbol, size, "AnnotatorGlyphAccent");
        glyph.Children.Add(plain);
        glyph.Children.Add(accent);
        Tween fill = Tween.Opacity(marker, 0);
        Tween plainShown = Tween.Opacity(plain, 1);
        Tween accentShown = Tween.Opacity(accent, 0);
        AutomationProperties.SetName(button, action.Label);
        AutomationProperties.SetAutomationId(button, $"toolbar-action-{action.RawValue}");
        ToolbarParts.Help(button, key.Length == 0 ? action.Detail : $"{action.Detail} Key {key}.",
            key.Length == 0 ? $"{action.Label}. {action.Detail}" : $"{action.Label} ({key}). {action.Detail}");
        button.Click += (_, _) =>
        {
            _interacted();
            _model.PerformShortcutAction(action);
        };
        bool? shown = null;
        _updates.Add(state =>
        {
            bool on = state.IsOn(action);
            button.IsChecked = on;
            if (shown == on)
            {
                return;
            }
            MotionSamples? motion = shown is null ? null : _controlMotion;
            shown = on;
            fill.To(on ? 1 : 0, motion);
            plainShown.To(on ? 0 : 1, motion);
            accentShown.To(on ? 1 : 0, motion);
        });
        return button;
    }

    /// <summary>Mac moreMenu: the Tool library button, which opens the Tools window (<see cref="ToolLibrary"/>).</summary>
    private ParityButton Library(ToolbarLook look, WorkspaceSnapshot snapshot)
    {
        ToolbarMetrics metrics = look.Metrics;
        string? key = ToolbarLook.LibraryKey(snapshot.Preferences);
        bool available = ToolLibrary is not null;
        FrameworkElement glyph = Glyphs.ForSymbol("square.grid.2x2", metrics.Minimal ? 16 : 19, available ? "AnnotatorGlyph" : "AnnotatorGlyphDisabled");
        ParityButton button = ToolbarParts.Button(metrics.ButtonWidth, metrics.ButtonHeight, Stacked(glyph, metrics, key ?? string.Empty), "Tool library");
        ToolbarParts.Help(button, key is null ? "More shapes and tools. Ctrl+L is assigned to a global shortcut." : $"More shapes and tools ({key})");
        if (metrics.Minimal)
        {
            AutomationProperties.SetAutomationId(button, "square.grid.2x2");
        }
        button.IsEnabled = available;
        button.Click += (_, _) =>
        {
            _interacted();
            ToolLibrary?.Invoke();
        };
        return button;
    }

    /// <summary>Mac boardMenu: cycles Desktop, Whiteboard and Blackboard on the drawing display.</summary>
    private ParityButton BoardButton(ToolbarLook look)
    {
        ToolbarMetrics metrics = look.Metrics;
        string symbol = look.Board switch
        {
            Board.Desktop => "desktopcomputer",
            Board.Whiteboard => "rectangle",
            _ => "rectangle.fill",
        };
        var stack = new MacStack { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement glyph = ToolbarParts.SymbolGlyph(symbol, metrics.Minimal, look.Board == Board.Blackboard ? "AnnotatorGlyphFilled" : "AnnotatorGlyph");
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(glyph);
        if (!metrics.Minimal)
        {
            TextBlock text = ToolbarParts.Label(look.Board switch { Board.Desktop => "Desk", Board.Whiteboard => "White", _ => "Black" },
                9, "AnnotatorPrimaryText", FontWeights.Medium);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(text);
        }
        var surface = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = look.Board == Board.Desktop ? null : AppResources.Brush("AnnotatorBoardFillBrush"),
            // A contrast theme outlines the active board instead of tinting it.
            BorderBrush = AppResources.Brush("AnnotatorSelectedOutlineBrush"),
            BorderThickness = new Thickness(look.Board != Board.Desktop && ContrastTheme.IsOn ? 2 : 0),
            Child = stack,
        };
        double height = look.Vertical ? (metrics.Minimal ? 30 : 36) : metrics.ButtonHeight;
        ParityButton button = ToolbarParts.Button(metrics.BoardWidth, height, surface, "Board background");
        button.AccessibleValue = look.Board.RawValue;
        button.BoundsElement = look.Board == Board.Desktop ? (metrics.Minimal ? glyph : stack) : null;
        if (metrics.Minimal)
        {
            AutomationProperties.SetAutomationId(button, symbol);
        }
        string help = "Cycle Desktop, Whiteboard and Blackboard on this drawing display. Ctrl+W: whiteboard. Ctrl+B: blackboard.";
        _updates.Add(state => ToolbarParts.Help(button, state.Preferences.ShortcutFor(ShortcutCommand.ForAction(ShortcutAction.Board)) is { } key
            ? $"{help} Key: {Annotator.Workspace.Shortcuts.WindowsShortcuts.Label(key)}." : help));
        button.Click += (_, _) =>
        {
            _interacted();
            _model.PerformShortcutAction(ShortcutAction.Board);
        };
        return button;
    }

    /// <summary>
    /// Mac modeButton: Draw or Use apps, with the green dot while drawing. Its colors change with selectionAnimation, its
    /// symbol is replaced after a pause, and the dot pulses when drawing resumes; its words change at once.
    /// </summary>
    private ParityButton ModeButton(ToolbarLook look)
    {
        ToolbarMetrics metrics = look.Metrics;
        double iconSize = metrics.Minimal ? 14 : 17;
        FrameworkElement drawIcon = Glyphs.ForSymbol("pencil.tip", iconSize);
        FrameworkElement appsIcon = Glyphs.ForSymbol("cursorarrow", iconSize);
        var icon = new Grid { HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        icon.Children.Add(drawIcon);
        icon.Children.Add(appsIcon);
        var drawDot = new Ellipse { Width = 5, Height = 5, Style = AppResources.Style("AnnotatorDotDraw"), CenterPoint = new System.Numerics.Vector3(2.5f, 2.5f, 0) };
        var appsDot = new Ellipse { Width = 5, Height = 5, Style = AppResources.Style("AnnotatorDotApps") };
        var dot = new Grid { Width = 5, Height = 5, VerticalAlignment = VerticalAlignment.Center };
        dot.Children.Add(appsDot);
        dot.Children.Add(drawDot);
        TextBlock label = ToolbarParts.Label(string.Empty, metrics.Minimal ? 8 : 9, "AnnotatorPrimaryText", FontWeights.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        var dotRow = new MacStack { Orientation = Orientation.Horizontal, Spacing = metrics.Minimal ? 3 : 4, HorizontalAlignment = HorizontalAlignment.Center };
        dotRow.Children.Add(dot);
        dotRow.Children.Add(label);
        var stack = new MacStack { Spacing = metrics.Minimal ? 1 : 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(icon);
        stack.Children.Add(dotRow);
        var drawSurface = new Border { Style = AppResources.Style("AnnotatorModeDraw") };
        var appsSurface = new Border { Style = AppResources.Style("AnnotatorModeApps") };
        var surface = new Grid();
        surface.Children.Add(appsSurface);
        surface.Children.Add(drawSurface);
        surface.Children.Add(stack);
        Tween drawShown = Tween.Opacity(drawSurface, 0);
        Tween appsShown = Tween.Opacity(appsSurface, 1);
        Tween drawDotShown = Tween.Opacity(drawDot, 0);
        Tween appsDotShown = Tween.Opacity(appsDot, 1);
        Tween drawDotScale = Tween.Scale(drawDot, 1);
        Tween drawIconShown = Tween.Opacity(drawIcon, 0);
        Tween appsIconShown = Tween.Opacity(appsIcon, 1);
        ParityButton button = ToolbarParts.Button(look.Vertical ? metrics.ButtonWidth : metrics.ModeWidth,
            look.Vertical ? 36 : metrics.ButtonHeight, surface, string.Empty);
        button.Click += (_, _) =>
        {
            _interacted();
            if (_model.Snapshot.Mode == WorkspaceMode.Draw)
            {
                _model.UseApps();
            }
            else
            {
                _model.ResumeDrawing();
            }
        };
        WorkspaceMode? shown = null;
        _updates.Add(state =>
        {
            bool drawing = state.Mode == WorkspaceMode.Draw;
            string resume = Annotator.Workspace.Shortcuts.WindowsShortcuts.Label(GlobalShortcutAction.ResumeDrawing, state.Preferences);
            ToolbarParts.Help(button, drawing
                ? $"Drawing is active. Click Use apps to pass clicks through. Escape finishes text or cancels an unfinished drawing first. {resume} returns to drawing."
                : $"Your apps receive input. Click Draw or press {resume}.");
            if (shown == state.Mode)
            {
                return;
            }
            MotionSamples? motion = shown is null ? null : _controlMotion;
            shown = state.Mode;
            label.Text = drawing ? "Draw" : "Apps";
            drawShown.To(drawing ? 1 : 0, motion);
            appsShown.To(drawing ? 0 : 1, motion);
            drawDotShown.To(drawing ? 1 : 0, motion);
            appsDotShown.To(drawing ? 0 : 1, motion);
            Tween arriving = drawing ? drawIconShown : appsIconShown;
            Tween leaving = drawing ? appsIconShown : drawIconShown;
            if (motion is not null && ToolbarControlMotion.SymbolOut(_reducesMotion) is { } symbolOut
                && ToolbarControlMotion.SymbolIn(_reducesMotion) is { } symbolIn)
            {
                leaving.Play(symbolOut);
                arriving.Play(symbolIn);
            }
            else
            {
                leaving.Set(0);
                arriving.Set(1);
            }
            if (drawing && motion is not null && ToolbarControlMotion.DotPulse(_reducesMotion) is { } pulse)
            {
                drawDotScale.Play(pulse);
            }
            else
            {
                drawDotScale.Set(1);
            }
            AutomationProperties.SetName(button, drawing ? "Use apps" : "Resume drawing");
            button.AccessibleValue = drawing ? "Drawing is active" : "Apps receive input";
        });
        return button;
    }

    /// <summary>
    /// The small actions in the Mac's order: Undo, Redo and Clear (historyActions), then Properties, Settings and Hide,
    /// without the ones turned off in Settings (Mac trailingActions). Settings opens on General (Mac openPreferences());
    /// without a <see cref="Settings"/> action it is unavailable.
    /// </summary>
    private IEnumerable<FrameworkElement> Actions(ToolbarLook look, WorkspaceSnapshot snapshot, bool history, bool rest,
                                                  bool properties = false, bool clear = true)
    {
        ToolbarMetrics metrics = look.Metrics;
        if (history)
        {
            if (look.Shows(ToolbarControl.Undo))
            {
                yield return Small(metrics, "Undo", "arrow.uturn.backward", () => _model.Undo(), state => state.CanUndo);
            }
            if (look.Shows(ToolbarControl.Redo))
            {
                yield return Small(metrics, "Redo", "arrow.uturn.forward", () => _model.Redo(), state => state.CanRedo);
            }
            if (clear && look.Shows(ToolbarControl.ClearAll))
            {
                yield return Clear(metrics);
            }
        }
        else if (rest && clear && look.Shows(ToolbarControl.ClearAll))
        {
            yield return Clear(metrics);
        }
        if ((properties || (rest && !look.Vertical)) && look.Shows(ToolbarControl.Properties))
        {
            yield return PropertiesButton(look);
        }
        if (rest)
        {
            if (look.Shows(ToolbarControl.Settings))
            {
                yield return Small(metrics, "Settings and shortcuts", "gearshape", () => Settings?.Invoke(), static _ => true, available: Settings is not null);
            }
            if (look.Shows(ToolbarControl.Hide))
            {
                yield return Small(metrics, "Hide drawings and tools", "xmark", () => _model.HideTools(), static _ => true);
            }
        }
    }

    private ParityButton Clear(ToolbarMetrics metrics)
    {
        ParityButton clear = Small(metrics, "Clear all drawings — undoable", "trash", () => _model.ClearCanvas(), state => state.CanClearCanvas);
        _updates.Add(state => ToolbarParts.Help(clear, "Clear all drawings — undoable" +
            (state.Preferences.ShortcutFor(ShortcutCommand.ForAction(ShortcutAction.ClearAll)) is { } key
                ? $" ({Annotator.Workspace.Shortcuts.WindowsShortcuts.Label(key)})" : string.Empty)));
        return clear;
    }

    /// <summary>Mac action(_:icon:): a plain icon button of the action size; accessibility reports the symbol's bounds and name.</summary>
    private ParityButton Small(ToolbarMetrics metrics, string name, string symbol, Action perform, Func<WorkspaceSnapshot, bool> enabled,
                               bool available = true)
    {
        var host = new ContentControl { IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        (double width, double height) = metrics.ActionSize;
        ParityButton button = ToolbarParts.Button(width, height, host, name);
        AutomationProperties.SetAutomationId(button, symbol);
        ToolbarParts.Help(button, name);
        button.Click += (_, _) =>
        {
            _interacted();
            perform();
        };
        bool? shown = null;
        void Show(bool on)
        {
            button.IsEnabled = on;
            if (shown == on)
            {
                return;
            }
            shown = on;
            FrameworkElement glyph = ToolbarParts.SymbolGlyph(symbol, style: on ? "AnnotatorGlyph" : "AnnotatorGlyphDisabled");
            host.Content = glyph;
            button.BoundsElement = glyph;
        }
        Show(available);
        _updates.Add(state => Show(available && enabled(state)));
        return button;
    }

    /// <summary>Mac propertiesButton: shows or hides the properties below or beside the tools.</summary>
    private ParityButton PropertiesButton(ToolbarLook look)
    {
        (double width, double height) = look.Metrics.ActionSize;
        FrameworkElement glyph = ToolbarParts.SymbolGlyph("slider.horizontal.3", style: look.Expanded ? "AnnotatorGlyphAccent" : "AnnotatorGlyph");
        var host = new ContentControl { Content = glyph, IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        // The Mac button takes clicks over its whole cell, so its accessible frame is the cell, not the glyph.
        ParityButton button = ToolbarParts.Button(width, height, host, "Properties");
        AutomationProperties.SetAutomationId(button, "toolbar-properties");
        ToolbarPropertiesMode mode = look.Properties;
        ToolbarPropertiesMode next = mode.Next;
        button.AccessibleValue = mode.RawValue;
        _updates.Add(state =>
        {
            string key = state.Preferences.ShortcutFor(ShortcutCommand.ForAction(ShortcutAction.Properties)) is { } shortcut
                ? $" Key: {Annotator.Workspace.Shortcuts.WindowsShortcuts.Label(shortcut)}." : string.Empty;
            ToolbarParts.Help(button, look.Expanded ? "Hide the properties" : "Show the properties",
                (look.Expanded ? "Hide the properties below the tools." : "Show the properties below the tools.") + key);
        });
        button.Click += (_, _) =>
        {
            _interacted();
            _model.ApplyPropertiesMode(next);
        };
        return button;
    }

    /// <summary>
    /// Mac activeModeStrip, "Active and pending modes": the orange Cancel control of a starting zoom, a Stop button for each
    /// active mode (screen zoom, the pointer lens, Spotlight, Cursor halo, the whiteboard or blackboard) and the live zoom
    /// level, in a row below a horizontal toolbar or a column beside a vertical one.
    /// </summary>
    private FrameworkElement ModeStrip(ToolbarLook look)
    {
        var strip = new MacStack { Orientation = look.Vertical ? Orientation.Vertical : Orientation.Horizontal, Spacing = 6 };
        if (look.PendingZoom is { } pending)
        {
            strip.Children.Add(ZoomControls.Pending(_model, pending, look.CompactRail, _interacted));
        }
        foreach (ActiveToolbarMode mode in look.ActiveModes)
        {
            strip.Children.Add(StopButton(look, mode));
        }
        if (look.ShowsZoomControls)
        {
            FrameworkElement level = ZoomControls.Level(_model, look.CompactRail, _updates, _interacted);
            level.VerticalAlignment = VerticalAlignment.Center;
            strip.Children.Add(level);
        }
        var group = new AccessibleGroup("Active and pending modes") { HorizontalAlignment = HorizontalAlignment.Left };
        group.Children.Add(strip);
        return group;
    }

    /// <summary>
    /// One mode's Stop button: its symbol, title and a cross in the accent color on a 13% accent fill, 130 by 44 points, or
    /// 44 by 44 with "Spot on" and the like on a compact rail.
    /// </summary>
    private ParityButton StopButton(ToolbarLook look, ActiveToolbarMode mode)
    {
        // The Windows glyph for rectangle.fill is the outline that rectangle uses; the filled style fills it.
        string glyphStyle = mode == ActiveToolbarMode.Whiteboard ? "AnnotatorGlyphAccentFilled" : "AnnotatorGlyphAccent";
        FrameworkElement content;
        if (look.CompactRail)
        {
            var icons = new MacStack { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            icons.Children.Add(Glyphs.ForSymbol(mode.Symbol, 14, glyphStyle));
            icons.Children.Add(Glyphs.ForSymbol("xmark", 8, "AnnotatorGlyphAccent"));
            var stack = new MacStack { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(icons);
            TextBlock caption = ToolbarParts.Label($"{mode.ShortTitle} on", 9, "AnnotatorAccentText", FontWeights.Medium);
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(caption);
            content = stack;
        }
        else
        {
            var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            FrameworkElement glyph = Glyphs.ForSymbol(mode.Symbol, 16, glyphStyle);
            glyph.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(glyph);
            TextBlock text = ToolbarParts.Label(mode.Title, 11, "AnnotatorAccentText", FontWeights.Medium);
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            FrameworkElement close = Glyphs.ForSymbol("xmark", 9, "AnnotatorGlyphAccent");
            close.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(close);
            content = row;
        }
        var surface = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = AppResources.Brush("AnnotatorModeStripFillBrush"),
            // A contrast theme outlines the active mode instead of tinting it.
            BorderBrush = AppResources.Brush("AnnotatorSelectedOutlineBrush"),
            BorderThickness = new Thickness(ContrastTheme.IsOn ? 2 : 0),
            Child = content,
        };
        ParityButton stop = ToolbarParts.Button(look.CompactRail ? 44 : 130, 44, surface, $"Stop {mode.Title.ToLowerInvariant()}", 8);
        stop.AccessibleValue = "Active";
        AutomationProperties.SetAutomationId(stop, $"stop-active-{mode.RawValue}");
        ToolbarParts.Help(stop, mode.StopHint, $"{mode.Title} is active. Click to stop.");
        stop.Click += (_, _) =>
        {
            _interacted();
            mode.Stop(_model);
        };
        return stop;
    }

    /// <summary>Mac selectionMenu: duplicate, group, lock, order and delete the selection.</summary>
    private FrameworkElement SelectionMenu()
    {
        var menu = new MenuFlyout();
        void Add(string text, CanvasCommandKind kind)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) =>
            {
                _interacted();
                if (_model.Snapshot.ActiveDisplay is { } display)
                {
                    _model.Perform(new CanvasCommand(kind, display));
                }
            };
            menu.Items.Add(item);
        }
        Add("Duplicate", CanvasCommandKind.Duplicate);
        Add("Group", CanvasCommandKind.Group);
        Add("Ungroup", CanvasCommandKind.Ungroup);
        Add("Lock", CanvasCommandKind.Lock);
        Add("Unlock", CanvasCommandKind.Unlock);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Bring to front", CanvasCommandKind.BringToFront);
        Add("Send to back", CanvasCommandKind.SendToBack);
        Add("Delete selected", CanvasCommandKind.DeleteSelected);
        var content = new MacStack { Orientation = Orientation.Horizontal, Spacing = 3 };
        content.Children.Add(ToolbarParts.GlyphBox("square.on.square", 18, 16, 17));
        content.Children.Add(ToolbarParts.GlyphBox("chevron.down", 9, 16, 10));
        var button = new DropDownButton
        {
            Style = AppResources.Style("AnnotatorMenuButton"),
            Content = content,
            Flyout = menu,
            Width = 37,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, "Selection actions");
        return button;
    }

    /// <summary>
    /// Mac menuButton: the Spotlight or Zoom menu as a toolbar item the size of a tool, with a chevron where a tool shows
    /// its key. It lights up while something in it is on (Spotlight or Cursor halo; the pointer lens or screen zoom).
    /// </summary>
    private FrameworkElement MenuButton(ToolbarLook look, WorkspaceSnapshot snapshot, ShortcutAction action)
    {
        ToolbarMetrics metrics = look.Metrics;
        double radius = metrics.Minimal ? 8 : 10;
        var marker = new Border { CornerRadius = new CornerRadius(radius), Background = AppResources.Brush("AnnotatorSelectionFillBrush") };
        var grid = new Grid { Width = metrics.ButtonWidth, Height = metrics.ButtonHeight };
        grid.Children.Add(marker);
        double size = metrics.Minimal ? 15 : 17;
        FrameworkElement plain = Glyphs.ForSymbol(action.Symbol, size);
        FrameworkElement accent = Glyphs.ForSymbol(action.Symbol, size, "AnnotatorGlyphAccent");
        var glyph = new Grid { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center };
        glyph.Children.Add(plain);
        glyph.Children.Add(accent);
        var host = new Grid { Height = metrics.Minimal ? 22 : 24 };
        host.Children.Add(glyph);
        var stack = new MacStack { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        stack.Children.Add(host);
        if (metrics.ShowsKeyLabels)
        {
            FrameworkElement chevron = ToolbarParts.GlyphBox("chevron.down", 9, 12, 9, "AnnotatorGlyphSecondary");
            chevron.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(chevron);
        }
        grid.Children.Add(stack);
        ParityButton button = ToolbarParts.Button(metrics.ButtonWidth, metrics.ButtonHeight, grid, action.Label, radius);
        AutomationProperties.SetAutomationId(button, $"toolbar-action-{action.RawValue}");
        ToolbarParts.Help(button, action.Detail, $"{action.Label}. {action.Detail}");
        button.Flyout = action == ShortcutAction.FocusMenu ? FocusMenuItems() : ZoomMenuItems();
        button.Click += (_, _) => _interacted();
        Tween fill = Tween.Opacity(marker, 0);
        Tween plainShown = Tween.Opacity(plain, 1);
        Tween accentShown = Tween.Opacity(accent, 0);
        bool? shown = null;
        _updates.Add(state =>
        {
            bool on = state.IsOn(action);
            button.AccessibleValue = on ? "On" : "Off";
            if (shown == on)
            {
                return;
            }
            MotionSamples? motion = shown is null ? null : _controlMotion;
            shown = on;
            fill.To(on ? 1 : 0, motion);
            plainShown.To(on ? 0 : 1, motion);
            accentShown.To(on ? 1 : 0, motion);
        });
        return button;
    }

    /// <summary>
    /// Mac focusMenuItems: Toggle("Spotlight") and Toggle("Cursor halo"), then Clear drawings (keep effects) and Remove all
    /// censors.
    /// </summary>
    private MenuFlyout FocusMenuItems()
    {
        var menu = new MenuFlyout();
        void Toggle(string text, Func<WorkspaceSnapshot, bool> isOn, Action<bool> set)
        {
            var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = isOn(_model.Snapshot) };
            item.Click += (_, _) =>
            {
                _interacted();
                set(item.IsChecked);
            };
            menu.Opening += (_, _) => item.IsChecked = isOn(_model.Snapshot);
            menu.Items.Add(item);
        }
        Toggle("Spotlight", state => state.Spotlight, _model.SetSpotlight);
        Toggle("Cursor halo", state => state.CursorHalo, _model.SetCursorHalo);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(menu, "Clear drawings (keep effects)", () => _model.ClearDrawingsKeepingProtection());
        AddItem(menu, "Remove all censors", () => _model.ClearProtection());
        return menu;
    }

    /// <summary>
    /// Mac zoomMenuItems: the pointer lens, full-screen zoom, the 2×, 3× and 4× levels and, while zoom or the lens is on,
    /// Stop zoom. The items follow the current state each time the menu opens.
    /// </summary>
    private MenuFlyout ZoomMenuItems()
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            WorkspaceSnapshot state = _model.Snapshot;
            menu.Items.Clear();
            AddItem(menu, state.MagnifierEnabled ? "Stop lens" : "Pointer lens", () => _model.ToggleMagnifier());
            AddItem(menu, state.ScreenZoomEnabled ? "Stop screen zoom" : "Full-screen zoom", () => _model.ToggleScreenZoom());
            foreach (int level in new[] { 2, 3, 4 })
            {
                AddItem(menu, $"{level}×", () => _model.SetMagnification(level));
            }
            if (state.MagnifierEnabled || state.ScreenZoomEnabled)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                AddItem(menu, "Stop zoom", () => _model.StopZoom());
            }
        };
        return menu;
    }

    private void AddItem(MenuFlyout menu, string text, Action perform)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) =>
        {
            _interacted();
            perform();
        };
        menu.Items.Add(item);
    }

    /// <summary>
    /// Mac CollapsedToolbarButton, "Annotator toolbar": the toolbar shrunk to one capsule with the Annotator mark and Draw
    /// or Apps, in the Draw button's colors. A click or a rest of the pointer opens the toolbar again; its context menu
    /// shows the toolbar or switches between drawing and apps.
    /// </summary>
    private FrameworkElement Collapsed(ToolbarLook look)
    {
        ToolbarMetrics metrics = look.Metrics;
        bool vertical = look.Vertical;
        (double width, double height) = vertical ? (metrics.Minimal ? 50.0 : 68.0, metrics.Minimal ? 74.0 : 88.0)
            : (metrics.Minimal ? 98.0 : 112.0, metrics.Minimal ? 38.0 : 44.0);
        double markSize = metrics.Minimal ? 18 : 22;
        PathGeometry geometry = Glyphs.Geometry(Annotator.Workspace.Artwork.MarkGlyph.PathData);
        geometry.FillRule = FillRule.EvenOdd;
        (double scale, Annotator.Core.Point offset) = Annotator.Workspace.Artwork.MarkGlyph.Fit(markSize);
        var mark = new Canvas { Width = markSize, Height = markSize, IsHitTestVisible = false };
        mark.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Style = AppResources.Style("AnnotatorGlyphFilled"),
            RenderTransform = new MatrixTransform { Matrix = new Matrix(scale, 0, 0, scale, offset.X, offset.Y) },
        });
        AutomationProperties.SetAccessibilityView(mark, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        var drawDot = new Ellipse { Width = 6, Height = 6, Style = AppResources.Style("AnnotatorDotDraw") };
        var appsDot = new Ellipse { Width = 6, Height = 6, Style = AppResources.Style("AnnotatorDotApps") };
        var dot = new Grid { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        dot.Children.Add(appsDot);
        dot.Children.Add(drawDot);
        TextBlock word = ToolbarParts.Label(string.Empty, metrics.Minimal ? 10 : 11, "AnnotatorPrimaryText", FontWeights.SemiBold);
        word.VerticalAlignment = VerticalAlignment.Center;
        word.HorizontalAlignment = HorizontalAlignment.Center;
        var status = new MacStack
        {
            Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
            Spacing = vertical ? 3 : 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        status.Children.Add(dot);
        status.Children.Add(word);
        var drawSurface = new Border { Style = AppResources.Style("AnnotatorModeDraw"), CornerRadius = new CornerRadius(vertical ? 7 : 14) };
        var appsSurface = new Border { Style = AppResources.Style("AnnotatorModeApps"), CornerRadius = new CornerRadius(vertical ? 7 : 14) };
        var badge = new Grid
        {
            Width = vertical ? (metrics.Minimal ? 36 : 46) : (metrics.Minimal ? 52 : 58),
            Height = vertical ? (metrics.Minimal ? 30 : 36) : (metrics.Minimal ? 24 : 28),
        };
        badge.Children.Add(appsSurface);
        badge.Children.Add(drawSurface);
        badge.Children.Add(status);
        var face = new MacStack
        {
            Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
            Spacing = vertical ? (metrics.Minimal ? 5 : 7) : (metrics.Minimal ? 6 : 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        face.Children.Add(mark);
        face.Children.Add(badge);
        var capsule = new Grid { Width = width, Height = height };
        double corner = Math.Min(width, height) / 2;
        capsule.Children.Add(new Border { Background = AppResources.Brush("AnnotatorWindowBackgroundBrush"), CornerRadius = new CornerRadius(corner) });
        capsule.Children.Add(face);
        capsule.Children.Add(new Border
        {
            BorderBrush = AppResources.Brush("AnnotatorSurfaceStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(corner),
            IsHitTestVisible = false,
        });
        ParityButton button = ToolbarParts.Button(width, height, capsule, "Annotator toolbar", corner);
        AutomationProperties.SetAutomationId(button, "collapsed-toolbar");
        button.Click += (_, _) =>
        {
            _interacted();
            ExpandToolbar();
        };
        var menu = new MenuFlyout();
        AddItem(menu, "Show Toolbar", ExpandToolbar);
        var modeItem = new MenuFlyoutItem();
        modeItem.Click += (_, _) =>
        {
            _interacted();
            if (_model.Snapshot.Mode == WorkspaceMode.Draw)
            {
                _model.UseApps();
            }
            else
            {
                _model.ResumeDrawing();
            }
        };
        menu.Items.Add(modeItem);
        menu.Opening += (_, _) => modeItem.Text = _model.Snapshot.Mode == WorkspaceMode.Draw ? "Use Apps" : "Draw";
        button.ContextFlyout = menu;
        Tween drawShown = Tween.Opacity(drawSurface, 0);
        Tween appsShown = Tween.Opacity(appsSurface, 1);
        Tween drawDotShown = Tween.Opacity(drawDot, 0);
        Tween appsDotShown = Tween.Opacity(appsDot, 1);
        WorkspaceMode? shown = null;
        _updates.Add(state =>
        {
            bool drawing = state.Mode == WorkspaceMode.Draw;
            button.AccessibleValue = drawing ? "Drawing is active" : "Apps receive input";
            ToolbarParts.Help(button, "Shows all tools",
                drawing ? "Drawing is on. Click to show your tools." : "Your apps get the clicks. Click to show your tools.");
            if (shown == state.Mode)
            {
                return;
            }
            MotionSamples? motion = shown is null ? null : _controlMotion;
            shown = state.Mode;
            word.Text = drawing ? "Draw" : "Apps";
            drawShown.To(drawing ? 1 : 0, motion);
            appsShown.To(drawing ? 0 : 1, motion);
            drawDotShown.To(drawing ? 1 : 0, motion);
            appsDotShown.To(drawing ? 0 : 1, motion);
        });
        return button;
    }

    /// <summary>Mac toolTitle: the tool's name, with the link mark while every drawing tool shares the style.</summary>
    private static FrameworkElement ToolTitle(ToolbarLook look)
    {
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        TextBlock title = ToolbarParts.Text(look.EffectiveTool.Label, 11, "AnnotatorPrimaryText", FontWeights.SemiBold);
        // What follows starts where the Mac's 14-point line ends, not where its height rounded to pixels ends.
        title.MinHeight = ToolbarParts.LineHeight(11);
        row.Children.Add(title);
        if (look.SharedStyle)
        {
            row.Children.Add(LinkMark());
        }
        return row;
    }

    /// <summary>Mac sharedStyleMark: the link mark while every drawing tool shares the style.</summary>
    private static FrameworkElement LinkMark()
    {
        var link = new AccessibleImage("Style shared by all drawing tools", "link",
            "All drawing tools share this style. You can turn this off in Settings > General.")
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        link.Children.Add(ToolbarParts.SymbolGlyph("link", style: "AnnotatorGlyphSecondary"));
        ToolbarParts.SetToolTip(link, "All drawing tools share this style. You can turn this off in Settings > General.");
        return link;
    }

}
