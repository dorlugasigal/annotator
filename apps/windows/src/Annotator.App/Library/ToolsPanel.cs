using Annotator.App.Files;
using Annotator.App.Icons;
using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Core;
using Annotator.Workspace;
using Annotator.Workspace.Canvas;
using Annotator.Workspace.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using WorkspaceBoard = Annotator.Workspace.Board;

namespace Annotator.App.Library;

/// <summary>
/// The Tools window's content, a port of ToolLibraryPanel with ToolLibrary and MyShapesLibrary (ToolLibrary.swift): the
/// headline and Paste image, the segmented sections, the tool grid or My shapes, Spotlight size in Focus, and the
/// Selection, File and Workspace menus. Tiles choose tools with the workspace's own calls; features that are not built on
/// Windows show unavailable, never faked. UI Automation reads the Mac's names, identifiers, values and help in its order.
/// </summary>
internal sealed class ToolsPanel
{
    private readonly WorkspaceModel _model;
    private readonly LibraryState _state;
    private readonly Action _dismiss;
    private readonly Action<Workspace.Settings.SettingsTab>? _openSettings;
    private readonly FileCommands? _files;
    private readonly Grid _root = new();
    private readonly ToolsLayout _layout;
    private readonly SegmentStrip _strip;
    private readonly TileGrid _grid = new();
    private readonly MyShapesView _shapes;
    private readonly ParityButton _paste;
    private readonly ParitySlider _spotlight;
    private readonly TextBlock _spotlightValue;
    private readonly (DropDownButton Button, TextBlock Title, MacStack Row, MenuFlyout Menu) _selection;
    private readonly List<(ToolLibraryTile Tile, ParityButton Button, Border Surface)> _tiles = [];
    private WorkspaceSnapshot _snapshot;
    private string _tileStructure = string.Empty;
    private bool _menusShown;
    private bool _applying;

    /// <param name="dismiss">Hides the window, as the Mac's dismiss does after a choice.</param>
    /// <param name="files">Open, save and export boards and insert image files; null leaves those items unavailable.</param>
    /// <param name="maximumHeight">The panel's maximumHeight for an ideal-height capture; null fills the window.</param>
    /// <param name="openSettings">Opens Settings on a tab (Pointer settings and Workspace > Help); null leaves both unavailable.</param>
    public ToolsPanel(WorkspaceModel model, ShapeLibrary library, ShapeThumbnails? thumbnails, LibraryState state, Action dismiss,
                      double? maximumHeight = null, double preferredWidth = ToolLibraryLayout.DefaultWidth,
                      Action<Workspace.Settings.SettingsTab>? openSettings = null, FileCommands? files = null)
    {
        _model = model;
        _state = state;
        _dismiss = dismiss;
        _openSettings = openSettings;
        _files = files;
        _snapshot = model.Snapshot;

        // HStack { Text("Tools").font(.headline); Spacer; Button("Paste image", systemImage: "photo.on.rectangle").controlSize(.small) }
        TextBlock title = LibraryParts.Text("Tools", 13, "AnnotatorPrimaryText", Microsoft.UI.Text.FontWeights.SemiBold, macWidth: 34.5);
        _paste = PasteButton();

        var group = new SegmentGroup("Tool category");
        _strip = new SegmentStrip(group, section => Show(section));
        group.Children.Add(_strip);

        var tools = new ScrollViewer
        {
            Content = _grid,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = false,
        };
        _grid.HorizontalAlignment = HorizontalAlignment.Left;
        _grid.VerticalAlignment = VerticalAlignment.Top;
        _grid.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
        AutomationProperties.SetAutomationId(tools, "tool-library-scroll");

        _shapes = new MyShapesView(model, library, thumbnails, state, dismiss);
        var shapes = new ScrollViewer
        {
            Content = _shapes.Root,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = false,
        };

        // HStack(spacing: 10) { Text("Spotlight size"); Slider(50...350); Text(radius).frame(width: 30); Button("Pointer settings").buttonStyle(.link) }
        var spotlight = new FlexibleRow { Spacing = 10 };
        TextBlock sizeLabel = LibraryParts.Text("Spotlight size", 10, "AnnotatorPrimaryText");
        sizeLabel.Width = 66;
        spotlight.Children.Add(sizeLabel);
        // Mac: Slider(value: $model.spotlightRadius, in: 50...350); the slider moves the spotlight's size live.
        _spotlight = PickerText.Slider(double.NaN, 50, 350, 1);
        _spotlight.Margin = new Thickness(0, -8, 0, -8);
        AutomationProperties.SetName(_spotlight, "Spotlight size");
        _spotlight.ValueChanged += (_, args) =>
        {
            if (!_applying && args.NewValue != _model.Snapshot.Preferences.SpotlightRadius)
            {
                _model.SetSpotlightRadius(args.NewValue);
            }
        };
        spotlight.Children.Add(_spotlight);
        _spotlightValue = LibraryParts.Text(string.Empty, 10, "AnnotatorPrimaryText");
        _spotlightValue.HorizontalAlignment = HorizontalAlignment.Center;
        var value = new Grid { Width = 30, Height = ToolbarParts.LineHeight(10) };
        value.Children.Add(_spotlightValue);
        spotlight.Children.Add(value);
        var pointerSettings = new HyperlinkButton
        {
            // Mac: settingsTab = .pointer, then openPreferences; unavailable where nothing opens Settings.
            Content = LibraryParts.Label("Pointer settings", 10, openSettings is null ? "AnnotatorDisabledText" : "AnnotatorAccentText"),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Width = 76.5,
            Height = ToolbarParts.LineHeight(10),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsEnabled = openSettings is not null,
        };
        pointerSettings.Click += (_, _) => _openSettings?.Invoke(Workspace.Settings.SettingsTab.Pointer);
        AutomationProperties.SetName(pointerSettings, "Pointer settings");
        spotlight.Children.Add(pointerSettings);
        Rectangle divider = ToolbarParts.Divider(height: 1);

        var footer = new AccessibleGroup(string.Empty, "tool-library-footer");
        var menus = new MacStack { Orientation = Orientation.Horizontal, Spacing = 8 };
        _selection = SelectionMenu();
        menus.Children.Add(_selection.Button);
        menus.Children.Add(FileMenu());
        menus.Children.Add(WorkspaceMenu());
        footer.Children.Add(menus);

        _layout = new ToolsLayout
        {
            Title = title,
            Paste = _paste,
            Picker = group,
            Tools = tools,
            Grid = _grid,
            Shapes = shapes,
            Spotlight = spotlight,
            Divider = divider,
            Footer = footer,
            MaximumHeight = maximumHeight,
            PreferredWidth = preferredWidth,
        };
        _layout.Attach();
        _root.Background = AppResources.Brush("AnnotatorWindowBackgroundBrush");
        _root.Children.Add(_layout);
        _root.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Escape)
            {
                args.Handled = true;
                _dismiss();
            }
        };
        Show(state.Section, reload: false);
        Apply(_snapshot);
    }

    /// <summary>The panel, on the window's background.</summary>
    public FrameworkElement Root => _root;

    public ToolLibrarySection Section => _state.Section;

    /// <summary>Stops following the library; the window replaces this panel.</summary>
    public void Detach() => _shapes.Detach();

    /// <summary>
    /// Shows a section. My shapes reads the library again when it appears (the Mac's .task), unless
    /// <paramref name="reload"/> is false because the window reads it when it opens (<see cref="Opened"/>).
    /// </summary>
    public void Show(ToolLibrarySection section, bool reload = true)
    {
        bool appeared = reload && section == ToolLibrarySection.MyShapes && _state.Section != section;
        _state.Section = section;
        _strip.Select(section);
        _layout.Section = section;
        BuildTiles(_snapshot, force: true);
        if (appeared)
        {
            _shapes.Appeared();
        }
    }

    /// <summary>
    /// The window opened: My shapes reads the library, and the chosen section, or the name field for Save to My shapes,
    /// takes keyboard focus once the panel is in the window.
    /// </summary>
    public void Opened(bool focusName)
    {
        if (_state.Section == ToolLibrarySection.MyShapes)
        {
            _shapes.Appeared();
        }
        if (_root.IsLoaded)
        {
            Focus(focusName);
            return;
        }
        void OnLoaded(object sender, RoutedEventArgs args)
        {
            _root.Loaded -= OnLoaded;
            Focus(focusName);
        }
        _root.Loaded += OnLoaded;
    }

    private void Focus(bool focusName)
    {
        if (focusName && _state.Section == ToolLibrarySection.MyShapes)
        {
            _shapes.FocusName();
        }
        else
        {
            _strip.FocusSelected();
        }
    }

    /// <summary>Shows the workspace state: the current tool, keys, the selection and what is available.</summary>
    public void Apply(WorkspaceSnapshot snapshot)
    {
        _snapshot = snapshot;
        BuildTiles(snapshot, force: false);
        _paste.IsEnabled = WorkspaceModel.IsAvailable(Tool.Image);
        _applying = true;
        try
        {
            _spotlight.Value = snapshot.Preferences.SpotlightRadius;
        }
        finally
        {
            _applying = false;
        }
        _spotlightValue.Text = ((int)snapshot.Preferences.SpotlightRadius).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_selection.Button.IsEnabled != snapshot.SelectionCount > 0 || !_menusShown)
        {
            _menusShown = true;
            LibraryParts.ShowMenuEnabled(_selection.Button, _selection.Title, _selection.Row, snapshot.SelectionCount > 0);
        }
        _shapes.Apply(snapshot);
    }

    /// <summary>The section's tiles; they are made again only when their words, keys or availability change.</summary>
    private void BuildTiles(WorkspaceSnapshot snapshot, bool force)
    {
        if (_state.Section == ToolLibrarySection.MyShapes)
        {
            if (force)
            {
                _grid.Children.Clear();
                _tiles.Clear();
                _tileStructure = string.Empty;
            }
            return;
        }
        var tiles = ToolLibraryTiles.For(_state.Section, snapshot);
        string structure = _state.Section + "|" + string.Join("|", tiles.Select(tile => $"{tile.Title}/{tile.Shortcut}/{tile.Available}/{tile.Explanation}"));
        if (force || structure != _tileStructure)
        {
            _tileStructure = structure;
            _grid.Children.Clear();
            _tiles.Clear();
            foreach (ToolLibraryTile tile in tiles)
            {
                (FrameworkElement cell, ParityButton button, Border surface) = Tile(tile);
                _grid.Children.Add(cell);
                _tiles.Add((tile, button, surface));
            }
        }
        for (int index = 0; index < _tiles.Count; index++)
        {
            bool selected = tiles[index].Selected;
            _tiles[index].Button.AccessibleSelected = selected;
            _tiles[index].Surface.Background = selected ? LibraryColors.SelectedTile : LibraryColors.Tile;
        }
    }

    /// <summary>
    /// Mac ToolLibrary.tile: the glyph, the title in up to two lines and the key, centered in a 76-point rounded tile. The
    /// title is the tile's accessibility label and help. A tile whose feature is not built is unavailable; one that the
    /// toolbar explains too (Text) says why in a tooltip around it.
    /// </summary>
    private (FrameworkElement Cell, ParityButton Button, Border Surface) Tile(ToolLibraryTile tile)
    {
        string glyphStyle = tile.Available ? "AnnotatorGlyph" : "AnnotatorGlyphDisabled";
        FrameworkElement glyph = tile.Tool is { } tool ? Glyphs.ForTool(tool, 21, glyphStyle) : Glyphs.ForSymbol(tile.Symbol, 21, glyphStyle);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var glyphHost = new Grid { Height = 26 };
        glyphHost.Children.Add(glyph);
        TextBlock title = LibraryParts.Label(tile.Title, 10, tile.Available ? "AnnotatorPrimaryText" : "AnnotatorDisabledText", LibraryParts.Medium);
        title.TextWrapping = TextWrapping.Wrap;
        title.MaxLines = 2;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center;
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.VerticalAlignment = VerticalAlignment.Center;
        var titleHost = new Grid { Height = 26 };
        titleHost.Children.Add(title);
        TextBlock key = LibraryParts.Label(tile.Shortcut ?? " ", 9, tile.Available ? "AnnotatorSecondaryText" : "AnnotatorDisabledText");
        key.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = new MacStack { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        stack.Children.Add(glyphHost);
        stack.Children.Add(titleHost);
        stack.Children.Add(key);
        var surface = new Border { CornerRadius = new CornerRadius(9), Background = tile.Selected ? LibraryColors.SelectedTile : LibraryColors.Tile, Child = stack };
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            CornerRadius = new CornerRadius(9),
            Content = surface,
            AccessibleSelected = tile.Selected,
            IsEnabled = tile.Available,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        AutomationProperties.SetName(button, tile.Title);
        AutomationProperties.SetHelpText(button, tile.Explanation ?? tile.Title);
        button.Click += (_, _) => Choose(tile);
        if (tile.Explanation is not { } explanation)
        {
            ToolTipService.SetToolTip(button, tile.Title);
            return (button, button, surface);
        }
        // A disabled button shows no tooltip, so a transparent slot around it explains why the tool is off.
        var slot = new Border { Child = button, Background = LibraryParts.Transparent };
        ToolTipService.SetToolTip(slot, explanation);
        return (slot, button, surface);
    }

    /// <summary>
    /// The tile's action (Mac ToolLibrary.tile actions): a tool, the lens, screen zoom, the Share a region window or Insert image
    /// starts after the window hides (dismiss(), then choose, chooseCover, the lens, screen zoom, region sharing or
    /// importScreenshot); Spotlight and Cursor halo turn on or off and the window stays.
    /// </summary>
    private void Choose(ToolLibraryTile tile)
    {
        switch (tile.Action)
        {
            case ToolLibraryAction.Spotlight:
                _model.SetSpotlight(!_model.Snapshot.Spotlight);
                return;
            case ToolLibraryAction.CursorHalo:
                _model.SetCursorHalo(!_model.Snapshot.CursorHalo);
                return;
            case ToolLibraryAction.PointerLens:
                _dismiss();
                _model.ToggleMagnifier();
                return;
            case ToolLibraryAction.ScreenZoom:
                _dismiss();
                _model.ToggleScreenZoom();
                return;
            case ToolLibraryAction.ShareRegion:
                // Mac: the Share region tile closes Tools and opens the Share a region window.
                _dismiss();
                _model.OpenRegionSharing();
                return;
            case ToolLibraryAction.InsertImage when _files is { } files:
                _dismiss();
                files.InsertImage();
                return;
        }
        if (tile.Action != ToolLibraryAction.ChooseTool || tile.Tool is not { } tool)
        {
            return;
        }
        _dismiss();
        _model.Choose(tool == Tool.Cover ? ToolLibraryTiles.PreferredEffect(_model.Snapshot.Preferences) : tool);
    }

    /// <summary>Button("Paste image", systemImage: "photo.on.rectangle").controlSize(.small): 108.5 by 20 points.</summary>
    private ParityButton PasteButton()
    {
        FrameworkElement icon = Glyphs.ForSymbol("photo.on.rectangle", 14, "AnnotatorGlyph");
        icon.VerticalAlignment = VerticalAlignment.Center;
        TextBlock label = LibraryParts.Label("Paste image", 11, "AnnotatorPrimaryText");
        var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        row.Children.Add(label);
        var surface = new Border { CornerRadius = new CornerRadius(5), Background = PickerColors.ControlFill, Child = row };
        var button = new ParityButton
        {
            Style = AppResources.Style("AnnotatorPlainButton"),
            Width = 108.5,
            Height = 20,
            CornerRadius = new CornerRadius(5),
            Content = surface,
        };
        AutomationProperties.SetName(button, "Paste image");
        void ShowEnabled()
        {
            surface.Background = button.IsEnabled ? PickerColors.ControlFill : LibraryColors.DisabledControlFill;
            label.Style = AppResources.Style(button.IsEnabled ? "AnnotatorPrimaryText" : "AnnotatorDisabledText");
            row.Children[0] = Glyphs.ForSymbol("photo.on.rectangle", 14, button.IsEnabled ? "AnnotatorGlyph" : "AnnotatorGlyphDisabled");
            ((FrameworkElement)row.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        }
        // WinUI does not raise IsEnabledChanged before the button is in the window, so it also shows its state on loading.
        button.IsEnabledChanged += (_, _) => ShowEnabled();
        button.Loaded += (_, _) => ShowEnabled();
        button.Click += (_, _) =>
        {
            _dismiss();
            _model.PasteImage();
        };
        return button;
    }

    /// <summary>
    /// Menu("Selection"): Save selection to My shapes... and the selection commands without Delete, unavailable without a
    /// selection.
    /// </summary>
    private (DropDownButton Button, TextBlock Title, MacStack Row, MenuFlyout Menu) SelectionMenu()
    {
        var menu = LibraryParts.Menu("Selection", 76);
        menu.Menu.Items.Add(LibraryParts.Item("Save selection to My shapes...", () =>
        {
            Show(ToolLibrarySection.MyShapes);
            _shapes.FocusName();
        }));
        menu.Menu.Items.Add(new MenuFlyoutSeparator());
        void Command(string text, CanvasCommandKind kind) => menu.Menu.Items.Add(LibraryParts.Item(text, () =>
        {
            if (_model.Snapshot.ActiveDisplay is { } display)
            {
                _model.Perform(new CanvasCommand(kind, display));
            }
        }));
        Command("Duplicate", CanvasCommandKind.Duplicate);
        Command("Group", CanvasCommandKind.Group);
        Command("Ungroup", CanvasCommandKind.Ungroup);
        Command("Lock", CanvasCommandKind.Lock);
        Command("Unlock", CanvasCommandKind.Unlock);
        menu.Menu.Items.Add(new MenuFlyoutSeparator());
        Command("Bring to front", CanvasCommandKind.BringToFront);
        Command("Send to back", CanvasCommandKind.SendToBack);
        return menu;
    }

    /// <summary>Menu("File"): Open board..., Save board... and Export PNG..., after the window hides (Mac dismiss(), then the model's call).</summary>
    private DropDownButton FileMenu()
    {
        var menu = LibraryParts.Menu("File", 41);
        Action? File(Action<FileCommands> command) => _files is { } files ? () =>
        {
            _dismiss();
            command(files);
        } : null;
        menu.Menu.Items.Add(LibraryParts.Item("Open board...", File(static files => files.OpenBoard())));
        menu.Menu.Items.Add(LibraryParts.Item("Save board...", File(static files => files.SaveBoard())));
        menu.Menu.Items.Add(LibraryParts.Item("Export PNG...", File(static files => files.ExportPng())));
        return menu.Button;
    }

    /// <summary>Menu("Workspace"): the boards, then Help, which hides the window and opens Settings > Help.</summary>
    private DropDownButton WorkspaceMenu()
    {
        var menu = LibraryParts.Menu("Workspace", 87);
        foreach (WorkspaceBoard board in new[] { WorkspaceBoard.Desktop, WorkspaceBoard.Whiteboard, WorkspaceBoard.Blackboard })
        {
            menu.Menu.Items.Add(LibraryParts.Item(board.RawValue, () => _model.SetBoard(board)));
        }
        menu.Menu.Items.Add(new MenuFlyoutSeparator());
        menu.Menu.Items.Add(LibraryParts.Item("Help", _openSettings is not { } open ? null : () =>
        {
            _dismiss();
            open(Workspace.Settings.SettingsTab.Help);
        }));
        return menu.Button;
    }
}
