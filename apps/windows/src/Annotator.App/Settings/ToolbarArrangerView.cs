using System.Numerics;
using Annotator.App.Library;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Core;
using Annotator.Workspace;
using Annotator.Workspace.Motion;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using CorePoint = Annotator.Core.Point;
using CoreRect = Annotator.Core.Rect;
using Glyphs = Annotator.App.Icons.Glyphs;

namespace Annotator.App.Settings;

/// <summary>
/// Port of ToolbarArranger (ToolbarShortcutSettings.swift): "Your toolbar" above the "More tools" tray. Dragging moves an item
/// within the toolbar, into it or out of it: the item lifts and tilts, its neighbours slide out of the way, the tray turns red
/// while letting go would remove it, and the item settles into its slot after the drop. Hover badges, context menus, the
/// keyboard and the Reset menu do the same without a drag. Changes go through the workspace model, so the toolbar follows at
/// once.
/// </summary>
internal sealed class ToolbarArrangerView : IArrangerTileHost
{
    private readonly WorkspaceModel _model;
    private readonly ShortcutEditor _editor;
    private readonly Action<string> _say;
    private readonly DispatcherQueue _queue;
    private readonly AccessibleGroup _stripGroup = new("Your toolbar", "toolbar-arrangement") { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Grid _stripSurface = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _stripFill = new();
    private readonly Rectangle _stripOutline = new();
    private readonly FlowPanel _strip = new() { Spacing = 3, LineSpacing = 6 };
    private readonly AccessibleGroup _trayGroup = new("More tools") { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 60 };
    private readonly Border _trayFill = new() { CornerRadius = new CornerRadius(12) };
    private readonly Rectangle _trayOutline = new() { RadiusX = 12, RadiusY = 12, StrokeThickness = 1 };
    private readonly MacStack _trayStack = new() { Orientation = Orientation.Vertical, Spacing = 10, Margin = new Thickness(8) };
    private readonly Grid _trayMessage = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly List<FlowPanel> _trayFlows = [];
    private readonly MacStack _layout = new() { Orientation = Orientation.Vertical, Spacing = 14, Margin = new Thickness(16) };
    private readonly Dictionary<ShortcutCommand, ArrangerTile> _stripTiles = [];
    private readonly Dictionary<ShortcutCommand, Grid> _wrappers = [];
    private readonly Dictionary<ShortcutCommand, ArrangerTile> _trayTiles = [];
    private readonly Dictionary<UIElement, Slide> _slides = [];
    private ToolbarMetrics _metrics = new(ToolbarSize.Expanded);
    private IReadOnlyList<ShortcutCommand> _order = [];
    /// <summary>The toolbar's own buttons that are off, as last laid out.</summary>
    private IReadOnlySet<ToolbarControl> _hidden = new HashSet<ToolbarControl>();
    private string? _libraryKey;
    private WorkspacePreferences _preferences;
    private string _trayMessageKey = string.Empty;
    private ArrangerDrag? _drag;
    private ArrangerTile? _floating;
    private DispatcherQueueTimer? _settle;
    private Windows.Foundation.Point? _pressed;
    private (ShortcutCommand Item, bool InToolbar, Windows.Foundation.Rect Frame)? _pressedTile;
    private Dictionary<ShortcutCommand, Windows.Foundation.Point>? _before;
    private bool _built;

    public ToolbarArrangerView(WorkspaceModel model, ShortcutEditor editor, Action<string> say)
    {
        _model = model;
        _editor = editor;
        _say = say;
        _queue = DispatcherQueue.GetForCurrentThread();
        Root = new AccessibleGroup(string.Empty, "tool-shortcuts-list") { HorizontalAlignment = HorizontalAlignment.Stretch };
        var background = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = SettingsColors.Primary(0.03),
            BorderBrush = SettingsColors.Primary(0.06),
            BorderThickness = new Thickness(1),
        };
        var layout = _layout;
        layout.Children.Add(Header());
        _stripFill.Background = SettingsColors.Window;
        _stripSurface.Children.Add(_stripFill);
        _stripSurface.Children.Add(_stripOutline);
        _stripSurface.Children.Add(_strip);
        // Your toolbar is one line, as the toolbar is (Mac strip): Settings opens wide enough for 15 tools and every button,
        // and a narrower window scrolls the line sideways without a scroll bar instead of wrapping it. The margins leave
        // room for the remove badges, which the scroll area would otherwise cut.
        _stripSurface.Margin = new Thickness(2, 10, 2, 10);
        _stripGroup.Children.Add(new ScrollViewer
        {
            Content = _stripSurface,
            Margin = new Thickness(-2, -10, -2, -10),
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = false,
        });
        layout.Children.Add(_stripGroup);
        var trayHeader = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        trayHeader.Children.Add(SettingsText.Static("More tools", 13, "AnnotatorPrimaryText", FontWeights.SemiBold, SettingsText.MacWidth("More tools")));
        trayHeader.Children.Add(_trayMessage);
        layout.Children.Add(trayHeader);
        _trayGroup.Children.Add(_trayFill);
        _trayGroup.Children.Add(_trayOutline);
        _trayGroup.Children.Add(_trayStack);
        layout.Children.Add(_trayGroup);
        Root.Children.Add(background);
        Root.Children.Add(layout);
        Root.Children.Add(_overlay);
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), handledEventsToo: true);
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), handledEventsToo: true);
        Root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), handledEventsToo: true);
        Root.PointerCaptureLost += (_, _) => Drop();
        Root.PointerCanceled += (_, _) => Drop();
    }

    /// <summary>The arranger, read by UI Automation as the group "tool-shortcuts-list".</summary>
    public AccessibleGroup Root { get; }

    public IReadOnlyList<ShortcutCommand> Order => _order;

    /// <summary>Every key chip of the arranger, for the page's recording state.</summary>
    public IEnumerable<KeyChip> Chips => _stripTiles.Values.Concat(_trayTiles.Values).Select(tile => tile.Chip);

    /// <summary>Shows the workspace's toolbar, keys and size, unless a drag decides the order now.</summary>
    public void Apply(WorkspaceSnapshot snapshot)
    {
        _preferences = snapshot.Preferences;
        var metrics = new ToolbarMetrics(snapshot.Preferences.ToolbarSize ?? ToolbarSize.Expanded);
        IReadOnlyList<ShortcutCommand> order = _drag?.Order ?? [.. snapshot.ToolbarItems];
        IReadOnlySet<ToolbarControl> hidden = snapshot.HiddenToolbarControls;
        string? libraryKey = Annotator.App.Toolbar.ToolbarLook.LibraryKey(snapshot.Preferences);
        bool resized = metrics != _metrics;
        bool controls = !hidden.SetEquals(_hidden) || libraryKey != _libraryKey;
        _hidden = hidden;
        _libraryKey = libraryKey;
        if (!_built || resized || controls || !order.SequenceEqual(_order))
        {
            Layout(order, metrics, animate: _built && !resized);
        }
        RenderChips();
    }

    /// <summary>Shows each chip's key or its listening state.</summary>
    public void RenderChips()
    {
        foreach (KeyChip chip in Chips)
        {
            chip.Show(_preferences.ShortcutFor(chip.Target.Command!.Value), _editor.Recording == chip.Target);
        }
    }

    /// <summary>The page goes away: a drag ends without changes and the pulses stop.</summary>
    public void Detach()
    {
        _settle?.Stop();
        EndSettle();
        _drag = null;
        foreach (KeyChip chip in Chips)
        {
            chip.Detach();
        }
    }

    public void Toggle(RecordingTarget target) => _editor.Toggle(target);

    public void Add(ShortcutCommand item)
    {
        if (ToolbarArranger.Add(_model, item) is { } message)
        {
            _say(message);
        }
    }

    public void Remove(ShortcutCommand item) => _say(ToolbarArranger.Remove(_model, item));

    public void Move(ShortcutCommand item, int delta)
    {
        if (ToolbarArranger.Move(_model, item, delta) && _stripTiles.TryGetValue(item, out ArrangerTile? tile))
        {
            tile.Focus(FocusState.Keyboard);
        }
    }

    /// <summary>Mac Reset: the default tools and order, or the default keys for tools and buttons.</summary>
    private FrameworkElement Header()
    {
        // The titles' exact height (19 + 3 + 15), so the strip and the tray below keep the Mac's places.
        var header = new Grid { Height = SettingsText.Scaled ? double.NaN : 37 };
        var titles = new MacStack { Orientation = Orientation.Vertical, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Left };
        titles.Children.Add(SettingsText.Static("Your toolbar", 15, "AnnotatorPrimaryText", FontWeights.SemiBold));
        titles.Children.Add(SettingsText.Static(
            "Drag tools to arrange them. Drag any tool or button down to take it off. Click the key under one, then press a new key.",
            12, "AnnotatorSecondaryText"));
        header.Children.Add(titles);
        (DropDownButton reset, _, _, MenuFlyout menu) = LibraryParts.Menu("Reset", 54);
        reset.HorizontalAlignment = HorizontalAlignment.Right;
        reset.VerticalAlignment = VerticalAlignment.Top;
        // The menu's first baseline sits on the title's (HStack alignment .firstTextBaseline).
        reset.Margin = new Thickness(0, 2, 0, 0);
        AutomationProperties.SetAutomationId(reset, "reset-toolbar-menu");
        menu.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight;
        menu.Items.Add(LibraryParts.Item("Default tools and order", () =>
        {
            _model.ConfigureToolbar(WorkspacePreferences.DefaultToolbar);
            _say(ToolbarArranger.DefaultTools);
        }));
        menu.Items.Add(LibraryParts.Item("Show every toolbar button", () =>
        {
            _model.ShowAllToolbarControls();
            _say(ToolbarArranger.AllButtonsBack);
        }));
        menu.Items.Add(LibraryParts.Item("Default keys for tools and buttons", () =>
        {
            _model.ResetLocalShortcuts();
            _say(ToolbarArranger.DefaultKeys);
        }));
        header.Children.Add(reset);
        return header;
    }

    /// <summary>
    /// Lays the toolbar strip and the tray out for <paramref name="order"/>. With <paramref name="animate"/>, the items that
    /// moved slide from where they were on screen to their new places (Mac matchedGeometryEffect with Motion.snappy).
    /// </summary>
    private void Layout(IReadOnlyList<ShortcutCommand> order, ToolbarMetrics metrics, bool animate)
    {
        Dictionary<ShortcutCommand, Windows.Foundation.Point>? before = animate && Root.IsLoaded ? Positions() : null;
        if (metrics != _metrics)
        {
            _metrics = metrics;
            _stripTiles.Clear();
            _wrappers.Clear();
        }
        _order = order;
        _built = true;
        _stripFill.CornerRadius = new CornerRadius(metrics.CornerRadius);
        _stripOutline.RadiusX = _stripOutline.RadiusY = metrics.CornerRadius;
        _strip.Margin = new Thickness(metrics.Padding);
        _strip.Children.Clear();
        for (int index = 0; index < order.Count; index++)
        {
            ShortcutCommand item = order[index];
            bool familyBreak = index > 0 && ToolbarToolLayout.Family(order[index - 1]) != ToolbarToolLayout.Family(item);
            Grid wrapper = Wrapper(item, familyBreak);
            _strip.Children.Add(wrapper);
        }
        foreach (ShortcutCommand stale in _stripTiles.Keys.Where(item => !order.Contains(item)).ToList())
        {
            _stripTiles.Remove(stale);
            _wrappers.Remove(stale);
        }
        // The toolbar's own buttons that are on, small as on the toolbar: More tools beside the tools, then the others
        // together after a divider (Mac strip and cluster).
        ToolbarControl[] shownControls = [.. ToolbarControl.AllCases.Where(control => !_hidden.Contains(control))];
        if (shownControls.Contains(ToolbarControl.Library))
        {
            _strip.Children.Add(ControlTile(ToolbarControl.Library, inToolbar: true, metrics));
        }
        ToolbarControl[] rightSide = [.. shownControls.Where(control => control != ToolbarControl.Library)];
        if (rightSide.Length > 0)
        {
            var cluster = new MacStack
            {
                Orientation = Orientation.Horizontal,
                Spacing = metrics.Spacing,
                Height = metrics.Minimal ? 50 : 60,
                Margin = new Thickness(9, 0, 0, 0),
            };
            foreach (ToolbarControl control in rightSide)
            {
                FrameworkElement tile = ControlTile(control, inToolbar: true, metrics);
                tile.VerticalAlignment = VerticalAlignment.Center;
                cluster.Children.Add(tile);
            }
            var clusterHost = new Grid();
            clusterHost.Children.Add(new Rectangle
            {
                Width = 1,
                Height = 26,
                Fill = SettingsColors.Primary(0.12),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0),
                IsHitTestVisible = false,
            });
            clusterHost.Children.Add(cluster);
            _strip.Children.Add(clusterHost);
        }
        // The tiles move to new rows: the old rows let go of them first (a tile can have only one parent).
        foreach (FlowPanel old in _trayFlows)
        {
            old.Children.Clear();
        }
        _trayFlows.Clear();
        _trayStack.Children.Clear();
        var shown = new HashSet<ShortcutCommand>();
        foreach ((TrayGroup group, IReadOnlyList<ShortcutCommand> members) in ToolbarArranger.TrayGroups(order))
        {
            var section = new MacStack { Orientation = Orientation.Vertical, Spacing = 6 };
            TextBlock heading = SettingsText.Static(group.RawValue, 10, "AnnotatorSecondaryText", FontWeights.SemiBold);
            AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
            section.Children.Add(heading);
            var flow = new FlowPanel { Spacing = 6, LineSpacing = 6 };
            _trayFlows.Add(flow);
            foreach (ShortcutCommand item in members)
            {
                if (!_trayTiles.TryGetValue(item, out ArrangerTile? tile))
                {
                    tile = new ArrangerTile(this, item, inToolbar: false, metrics);
                    _trayTiles[item] = tile;
                }
                Detach(tile);
                flow.Children.Add(tile);
                shown.Add(item);
            }
            section.Children.Add(flow);
            _trayStack.Children.Add(section);
        }
        foreach (ShortcutCommand stale in _trayTiles.Keys.Where(item => !shown.Contains(item)).ToList())
        {
            _trayTiles.Remove(stale);
        }
        IReadOnlyList<ToolbarControl> hiddenControls = ToolbarArranger.HiddenControls(_hidden);
        if (hiddenControls.Count > 0)
        {
            var section = new MacStack { Orientation = Orientation.Vertical, Spacing = 6 };
            TextBlock heading = SettingsText.Static(ToolbarArranger.ButtonsHeading, 10, "AnnotatorSecondaryText", FontWeights.SemiBold);
            AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
            section.Children.Add(heading);
            var flow = new FlowPanel { Spacing = 6, LineSpacing = 6 };
            _trayFlows.Add(flow);
            foreach (ToolbarControl control in hiddenControls)
            {
                flow.Children.Add(ControlTile(control, inToolbar: false, metrics));
            }
            section.Children.Add(flow);
            _trayStack.Children.Add(section);
        }
        foreach (ArrangerTile tile in _stripTiles.Values.Concat(_trayTiles.Values))
        {
            tile.Placeholder = _drag?.Item == tile.Item;
            tile.Refresh();
        }
        ShowDragState();
        if (before is not null)
        {
            _before = before;
            Root.LayoutUpdated += OnLayoutUpdated;
        }
    }

    /// <summary>One of the toolbar's own buttons: small in the strip, or a tile in the tray's "Toolbar buttons" group.</summary>
    private ArrangerControlTile ControlTile(ToolbarControl control, bool inToolbar, ToolbarMetrics metrics) =>
        new(control, inToolbar, metrics, _libraryKey,
            add: item =>
            {
                if (ToolbarArranger.AddControl(_model, item) is { } message)
                {
                    _say(message);
                }
            },
            remove: item =>
            {
                if (ToolbarArranger.RemoveControl(_model, item) is { } message)
                {
                    _say(message);
                }
            });

    /// <summary>A toolbar item with its family break: 9 points of space and a 1-point divider before a new family.</summary>
    private Grid Wrapper(ShortcutCommand item, bool familyBreak)
    {
        if (!_stripTiles.TryGetValue(item, out ArrangerTile? tile))
        {
            tile = new ArrangerTile(this, item, inToolbar: true, _metrics);
            _stripTiles[item] = tile;
        }
        if (!_wrappers.TryGetValue(item, out Grid? wrapper))
        {
            wrapper = new Grid();
            _wrappers[item] = wrapper;
        }
        Detach(tile);
        wrapper.Children.Clear();
        tile.Margin = new Thickness(familyBreak ? 9 : 0, 0, 0, 0);
        tile.HorizontalAlignment = HorizontalAlignment.Left;
        if (familyBreak)
        {
            wrapper.Children.Add(new Rectangle
            {
                Width = 1,
                Height = 26,
                Fill = SettingsColors.Primary(0.12),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 0, 0),
                IsHitTestVisible = false,
            });
        }
        wrapper.Children.Add(tile);
        Detach(wrapper);
        return wrapper;
    }

    private static void Detach(FrameworkElement element)
    {
        if ((element.Parent ?? VisualTreeHelper.GetParent(element)) is Panel panel)
        {
            panel.Children.Remove(element);
        }
    }

    /// <summary>The drag's colors and words: the strip's accent outline while adding, the tray's red while removing.</summary>
    private void ShowDragState()
    {
        bool adding = _drag?.Adding == true;
        bool removing = _drag?.Removing == true;
        _stripOutline.Stroke = adding ? SettingsColors.AccentBrush() : SettingsColors.Primary(0.08);
        _stripOutline.StrokeThickness = adding ? 2 : 1;
        _trayFill.Background = removing ? SettingsColors.Brush(SettingsColors.Red, 0.06) : null;
        _trayOutline.Stroke = removing ? SettingsColors.Brush(SettingsColors.Red, 0.5) : SettingsColors.Primary(0.07);
        _trayOutline.StrokeDashArray = removing ? [5, 4] : null;
        (string Text, string Symbol, bool Red)? message = _drag?.TrayMessage;
        string key = message?.Text ?? ToolbarArranger.TrayHint(_order, _hidden);
        if (key == _trayMessageKey)
        {
            return;
        }
        bool fade = _trayMessageKey.Length > 0;
        _trayMessageKey = key;
        _trayMessage.Children.Clear();
        if (message is { } warning)
        {
            var label = new MacStack { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            Windows.UI.Color tint = warning.Red ? SettingsColors.Red : SettingsColors.Orange;
            FrameworkElement icon = SettingsSymbols.Filled(warning.Symbol, 11, tint);
            label.Children.Add(icon);
            TextBlock text = SettingsText.Static(warning.Text, 10);
            text.Foreground = new SolidColorBrush(tint);
            label.Children.Add(text);
            _trayMessage.Children.Add(label);
        }
        else
        {
            _trayMessage.Children.Add(SettingsText.Static(key, 10, "AnnotatorSecondaryText"));
        }
        if (fade)
        {
            PageMotion.Fade(_trayMessage, 0, 1, ShortcutEditorMotion.FadeIn);
        }
    }

    /// <summary>Where each item is on screen now, in the arranger's coordinates, including a slide that still plays.</summary>
    private Dictionary<ShortcutCommand, Windows.Foundation.Point> Positions()
    {
        var positions = new Dictionary<ShortcutCommand, Windows.Foundation.Point>();
        foreach ((ShortcutCommand item, Grid wrapper) in _wrappers)
        {
            if (wrapper.Parent is not null && Frame(wrapper) is { } frame)
            {
                positions[item] = Shifted(wrapper, new Windows.Foundation.Point(frame.X, frame.Y));
            }
        }
        foreach ((ShortcutCommand item, ArrangerTile tile) in _trayTiles)
        {
            if (tile.Parent is not null && !positions.ContainsKey(item) && Frame(tile) is { } frame)
            {
                positions[item] = Shifted(tile, new Windows.Foundation.Point(frame.X, frame.Y));
            }
        }
        return positions;
    }

    private Windows.Foundation.Point Shifted(UIElement element, Windows.Foundation.Point point)
    {
        if (_slides.TryGetValue(element, out Slide? slide) && slide.Offset() is { } offset)
        {
            return new Windows.Foundation.Point(point.X + offset.X, point.Y + offset.Y);
        }
        return point;
    }

    private Windows.Foundation.Rect? Frame(FrameworkElement element)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0)
        {
            return null;
        }
        try
        {
            return element.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void OnLayoutUpdated(object? sender, object args)
    {
        Root.LayoutUpdated -= OnLayoutUpdated;
        if (_before is not { } before)
        {
            return;
        }
        _before = null;
        MotionSamples samples = ShortcutEditorMotion.Reorder(PageMotion.ReducesMotion);
        foreach ((ShortcutCommand item, Windows.Foundation.Point was) in before)
        {
            FrameworkElement? element = _wrappers.TryGetValue(item, out Grid? wrapper) && wrapper.Parent is not null ? wrapper
                : _trayTiles.TryGetValue(item, out ArrangerTile? tile) && tile.Parent is not null ? tile : null;
            if (element is null || Frame(element) is not { } now)
            {
                continue;
            }
            var offset = new Vector3((float)(was.X - now.X), (float)(was.Y - now.Y), 0);
            if (offset.LengthSquared() < 0.25f)
            {
                continue;
            }
            PageMotion.Slide(element, offset, samples);
            _slides[element] = new Slide(offset, samples, ToolbarMotion.Now);
        }
    }

    private sealed record Slide(Vector3 From, MotionSamples Samples, double Started)
    {
        /// <summary>The offset on screen now, or null once the slide ended.</summary>
        public Vector3? Offset()
        {
            double elapsed = ToolbarMotion.Now - Started;
            return elapsed >= Samples.Duration ? null : From * (float)(1 - Samples.At(elapsed));
        }
    }

    // MARK: Drag

    private void OnPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_drag is not null || !args.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
        {
            return;
        }
        Windows.Foundation.Point point = args.GetCurrentPoint(Root).Position;
        _pressed = point;
        _pressedTile = Hit(point);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs args)
    {
        Windows.Foundation.Point point = args.GetCurrentPoint(Root).Position;
        if (_drag is null)
        {
            if (_pressed is not { } start || _pressedTile is not { } pressed || !args.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
            {
                return;
            }
            if (Math.Abs(point.X - start.X) < ToolbarArranger.DragStart && Math.Abs(point.Y - start.Y) < ToolbarArranger.DragStart)
            {
                return;
            }
            BeginDrag(pressed.Item, pressed.InToolbar, pressed.Frame, start, args);
        }
        if (_drag is not { Settling: false } drag)
        {
            return;
        }
        args.Handled = true;
        bool overStrip = Frame(_stripGroup) is { } strip && Inflate(strip, ToolbarArranger.StripReach).Contains(point);
        bool changed = drag.Update(Core(point), overStrip, item => _wrappers.TryGetValue(item, out Grid? wrapper) && Frame(wrapper) is { } frame
            ? new CoreRect(frame.X, frame.Y, frame.Width, frame.Height) : null);
        if (changed)
        {
            Layout(drag.Order, _metrics, animate: true);
        }
        else
        {
            ShowDragState();
        }
        PlaceFloating();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs args)
    {
        _pressed = null;
        _pressedTile = null;
        if (_drag is { Settling: false })
        {
            args.Handled = true;
            Drop();
        }
    }

    /// <summary>
    /// Mac dragChanged's first event: the item under the press lifts. A key that records stops, keyboard focus leaves the
    /// tiles, and a copy of the item follows the pointer, scaled to 1.1 and tilted, with a shadow.
    /// </summary>
    private void BeginDrag(ShortcutCommand item, bool inToolbar, Windows.Foundation.Rect frame, Windows.Foundation.Point start, PointerRoutedEventArgs args)
    {
        _settle?.Stop();
        EndSettle();
        if (_editor.Recording is not null)
        {
            _editor.Stop();
        }
        var grab = new CorePoint(start.X - (frame.X + frame.Width / 2), start.Y - (frame.Y + frame.Height / 2));
        _drag = ArrangerDrag.Start(item, inToolbar, Core(start), grab, _order);
        Root.CapturePointer(args.Pointer);
        Layout(_order, _metrics, animate: false);
        PlaceFloating();
    }

    /// <summary>The copy that follows the pointer (Mac floatingItem): the toolbar's look over the strip, the tray's elsewhere.</summary>
    private void PlaceFloating()
    {
        if (_drag is not { } drag)
        {
            return;
        }
        bool removable = drag.Item != ToolbarArranger.Select;
        bool toolbarLook = drag.OverStrip || !removable;
        if (_floating is null || _floating.InToolbar != toolbarLook)
        {
            if (_floating is not null)
            {
                _overlay.Children.Remove(_floating);
            }
            _floating = new ArrangerTile(this, drag.Item, toolbarLook, _metrics, floating: true);
            _floating.ShowLifted();
            _floating.Chip.Show(_preferences.ShortcutFor(drag.Item), recording: false);
            var shadow = new ThemeShadow();
            shadow.Receivers.Add(_layout);
            _floating.Shadow = shadow;
            _overlay.Children.Add(_floating);
        }
        bool reduce = PageMotion.ReducesMotion;
        _floating.ShowRemoving(!drag.OverStrip && drag.FromToolbar && removable && !drag.Settling);
        double width = _floating.Width;
        double height = double.IsNaN(_floating.Height) ? _floating.MinHeight : _floating.Height;
        Canvas.SetLeft(_floating, drag.Location.X - drag.GrabOffset.X - width / 2);
        Canvas.SetTop(_floating, drag.Location.Y - drag.GrabOffset.Y - height / 2);
        _floating.CenterPoint = new Vector3((float)(width / 2), (float)(height / 2), 0);
        float scale = drag.Settling || reduce ? 1 : (float)ShortcutEditorMotion.LiftScale;
        _floating.Scale = new Vector3(scale, scale, 1);
        _floating.Rotation = drag.Settling || reduce ? 0 : (float)(drag.OverStrip ? -ShortcutEditorMotion.LiftTilt : ShortcutEditorMotion.LiftTilt);
        _floating.Translation = new Vector3(0, 0, 32);
    }

    /// <summary>
    /// Mac dragEnded: saves the order, says what changed, and settles the copy into the item's slot over 260 ms on the snappy
    /// spring before it goes; with Reduce Motion it goes at once.
    /// </summary>
    private void Drop()
    {
        if (_drag is not { Settling: false } drag)
        {
            return;
        }
        // Ending first makes the drag settle, so the capture loss that releasing raises finds nothing to drop.
        string? message = drag.End(_model);
        Root.ReleasePointerCaptures();
        if (message is not null)
        {
            _say(message);
        }
        Apply(_model.Snapshot);
        if (PageMotion.ReducesMotion || _floating is null)
        {
            EndSettle();
            return;
        }
        Root.LayoutUpdated += OnSettleLayout;
    }

    private void OnSettleLayout(object? sender, object args)
    {
        Root.LayoutUpdated -= OnSettleLayout;
        if (_drag is not { Settling: true } drag || _floating is not { } floating)
        {
            return;
        }
        FrameworkElement? slot = drag.OverStrip
            ? (_wrappers.TryGetValue(drag.Item, out Grid? wrapper) ? wrapper.Children.OfType<ArrangerTile>().FirstOrDefault() : null)
            : _trayTiles.GetValueOrDefault(drag.Item);
        if (slot is not null && Frame(slot) is { } target)
        {
            double fromX = drag.Location.X - drag.GrabOffset.X, fromY = drag.Location.Y - drag.GrabOffset.Y;
            drag.SettleAt(new CoreRect(target.X, target.Y, target.Width, target.Height));
            double toX = drag.Location.X - drag.GrabOffset.X, toY = drag.Location.Y - drag.GrabOffset.Y;
            MotionSamples settle = ShortcutEditorMotion.Settle(reduceMotion: false)!;
            var move = new Vector3((float)(toX - fromX), (float)(toY - fromY), 0);
            float rotation = floating.Rotation;
            floating.Shadow = null;
            ToolbarMotion.Vector(floating, "Translation", settle, share => move * (float)share + new Vector3(0, 0, 32 * (float)(1 - share)));
            ToolbarMotion.Vector(floating, "Scale", settle,
                share => new Vector3((float)(ShortcutEditorMotion.LiftScale + (1 - ShortcutEditorMotion.LiftScale) * share),
                    (float)(ShortcutEditorMotion.LiftScale + (1 - ShortcutEditorMotion.LiftScale) * share), 1));
            ToolbarMotion.Scalar(floating, "Rotation", settle, share => rotation * (float)(1 - share));
            floating.ShowRemoving(false);
        }
        _settle ??= _queue.CreateTimer();
        _settle.Interval = TimeSpan.FromSeconds(ShortcutEditorMotion.SettleDuration);
        _settle.IsRepeating = false;
        _settle.Tick -= OnSettled;
        _settle.Tick += OnSettled;
        _settle.Start();
    }

    private void OnSettled(DispatcherQueueTimer timer, object args)
    {
        timer.Stop();
        EndSettle();
    }

    /// <summary>The drag is over: the copy goes and the item's own tile shows again.</summary>
    private void EndSettle()
    {
        if (_floating is not null)
        {
            _overlay.Children.Remove(_floating);
            _floating = null;
        }
        if (_drag is null)
        {
            return;
        }
        _drag = null;
        foreach (ArrangerTile tile in _stripTiles.Values.Concat(_trayTiles.Values))
        {
            tile.Placeholder = false;
        }
        ShowDragState();
        Apply(_model.Snapshot);
    }

    /// <summary>The toolbar item or tray tile under a point of the arranger.</summary>
    private (ShortcutCommand, bool, Windows.Foundation.Rect)? Hit(Windows.Foundation.Point point)
    {
        foreach ((ShortcutCommand item, Grid wrapper) in _wrappers)
        {
            if (wrapper.Parent is not null && Frame(wrapper) is { } frame && frame.Contains(point))
            {
                return (item, true, frame);
            }
        }
        foreach ((ShortcutCommand item, ArrangerTile tile) in _trayTiles)
        {
            if (tile.Parent is not null && Frame(tile) is { } frame && frame.Contains(point))
            {
                return (item, false, frame);
            }
        }
        return null;
    }

    private static Windows.Foundation.Rect Inflate(Windows.Foundation.Rect rect, double by) =>
        new(rect.X - by, rect.Y - by, rect.Width + by * 2, rect.Height + by * 2);

    private static CorePoint Core(Windows.Foundation.Point point) => new(point.X, point.Y);
}
