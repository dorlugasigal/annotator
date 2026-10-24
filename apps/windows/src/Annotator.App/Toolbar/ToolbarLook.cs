using System.Text;
using Annotator.Core;
using Annotator.Workspace;
using Annotator.Workspace.Shortcuts;
using Annotator.Workspace.Toolbar;
using Windows.Foundation;

namespace Annotator.App.Toolbar;

/// <summary>What the quick properties show for the current tool (the branches of ToolbarView.quickPreferences).</summary>
internal enum QuickKind
{
    Effect,
    Counter,
    Image,
    Cover,
    SelectHint,
    Eraser,
    Text,
    Pen,
    Stroke,
}

/// <summary>
/// The toolbar's computed layout, a port of ToolbarView's computed properties: metrics, orientation, properties mode,
/// width, the compact rail and the visible tool items. <see cref="Structure"/> changes only when the controls change,
/// so ordinary updates keep the existing controls (and keyboard focus).
/// </summary>
internal sealed record ToolbarLook
{
    public required ToolbarMetrics Metrics { get; init; }

    public required bool Vertical { get; init; }

    /// <summary>
    /// A vertical toolbar docked at the right edge lays out right to left, so its rail sits at the edge and its properties
    /// open inward (Mac layoutDirection .rightToLeft).
    /// </summary>
    public required bool RightDocked { get; init; }

    public required ToolbarPropertiesMode Properties { get; init; }

    public bool Expanded => Properties != ToolbarPropertiesMode.ToolsOnly;

    /// <summary>Full properties: the panel replaces the quick properties (Mac pinned).</summary>
    public bool Pinned => Properties == ToolbarPropertiesMode.Full;

    /// <summary>What the full properties panel shows, while it is pinned.</summary>
    public InspectorPlan? Plan { get; init; }

    public required Size Available { get; init; }

    public required double Width { get; init; }

    public required bool CompactRail { get; init; }

    public required ShortcutCommand[] Items { get; init; }

    public required ShortcutCommand[] Visible { get; init; }

    public required double RailWidth { get; init; }

    /// <summary>The diagram tool the Rectangle button stands for while it is chosen, or null.</summary>
    public required Tool? RectangleShows { get; init; }

    public required bool ShowsModeStrip { get; init; }

    public required Board Board { get; init; }

    /// <summary>The modes with a Stop button in the strip, in the Mac's order (Mac activeModes).</summary>
    public IReadOnlyList<ActiveToolbarMode> ActiveModes { get; init; } = [];

    /// <summary>The orange Cancel control while a zoom request starts (Mac pendingZoom), or null.</summary>
    public PendingZoomPresentation? PendingZoom { get; init; }

    /// <summary>The live zoom level below the modes while screen zoom is on and not pending.</summary>
    public bool ShowsZoomControls { get; init; }

    public required Tool EffectiveTool { get; init; }

    public required QuickKind Quick { get; init; }

    public required bool SupportsFill { get; init; }

    public required bool HasSelection { get; init; }

    public required bool SharedStyle { get; init; }

    /// <summary>The capture warning below the controls or on the rail (Mac needsCaptureRecovery), or null.</summary>
    public string? CaptureWarning { get; init; }

    public double VerticalPropertyWidth => Math.Min(300, Math.Max(240, Available.Width - 76 - (ShowsModeStrip ? 76 : 0)));

    /// <summary>
    /// Mac CompactInspectorViewport's height limit: below the horizontal rail, the available height less the rail, the
    /// compact actions row or the menus row below full properties, and the board strip; beside the vertical rail, the
    /// available height less the tool title.
    /// </summary>
    public double InspectorLimit => Vertical ? Math.Max(64, Available.Height - 50)
        : Math.Max(64, Available.Height - 100 - (CompactRail ? 48 : FullMenusHeight + 8) - (ShowsModeStrip ? 52 : 0));

    /// <summary>The menus row below horizontal full properties (Mac fullPropertiesMenus): the 16-point menu buttons.</summary>
    public const double FullMenusHeight = 16;

    /// <summary>A horizontal toolbar narrower than 620 points shows full properties in one column, as a vertical one does.</summary>
    public bool InspectorCompact => Vertical || Width < 620;

    public required string Structure { get; init; }

    /// <summary>
    /// The look for <paramref name="available"/> points. <paramref name="orientation"/> and <paramref name="dockEdge"/>
    /// replace the saved ones, as the Mac's orientationOverride and dockEdgeOverride do when it measures the toolbar for a
    /// dock preview.
    /// </summary>
    public static ToolbarLook Of(WorkspaceSnapshot snapshot, Size available, ToolbarOrientation? orientation = null,
                                 ToolbarDockEdge? dockEdge = null)
    {
        WorkspacePreferences preferences = snapshot.Preferences;
        ToolbarSize size = preferences.ToolbarSize ?? ToolbarSize.Expanded;
        var metrics = new ToolbarMetrics(size);
        bool vertical = (orientation ?? preferences.ToolbarOrientation) == ToolbarOrientation.Vertical;
        bool rightDocked = vertical && (dockEdge ?? preferences.ToolbarDockEdge) == ToolbarDockEdge.Right;
        ToolbarPropertiesMode properties = ToolbarPropertiesMode.From(preferences);
        bool expanded = properties != ToolbarPropertiesMode.ToolsOnly;
        ShortcutCommand[] items = [.. snapshot.ToolbarItems];
        IReadOnlyList<ActiveToolbarMode> modes = ActiveToolbarModes.Active(snapshot);
        PendingZoomPresentation? pending = PendingZoomPresentation.For(snapshot.ZoomStart);
        bool zoomControls = ActiveToolbarModes.ShowsZoomControls(snapshot);
        bool strip = pending is not null || modes.Count > 0;
        double width = vertical
            ? metrics.VerticalRailWidth + (strip ? 76 : 0) + (expanded ? Math.Min(300, Math.Max(240, available.Width - 76 - (strip ? 76 : 0))) + 8 : 0)
            : ToolbarToolLayout.HorizontalWidth(items, available.Width, expanded, size);
        bool compact = vertical || width < metrics.CompactThreshold;
        Tool current = snapshot.Tool;
        Tool identity = ToolbarToolLayout.Identity(current, items);
        string? warning = snapshot.CaptureWarning;
        ShortcutCommand[] visible = vertical
            ? VerticalItems(items, identity, available.Height - (warning is null ? 0 : 64), metrics)
            : ToolbarToolLayout.Visible(items, ShortcutCommand.ForTool(identity),
                Math.Max(metrics.ButtonWidth, width - (compact ? metrics.CompactRailReserve : metrics.RailReserve)), size);
        bool diagram = current.IsShape && current is not (Tool.Rectangle or Tool.Ellipse or Tool.Diamond);
        Tool? rectangleShows = diagram && !Array.Exists(items, item => item.Tool == current) ? current : null;
        Tool effective = snapshot.EffectiveTool;
        bool shared = preferences.ShareToolStyles == true && effective.SharesStyle;
        QuickKind quick = Kind(effective, snapshot.SelectionCount);
        InspectorPlan? plan = properties == ToolbarPropertiesMode.Full ? InspectorPlan.For(snapshot) : null;
        var structure = new StringBuilder();
        structure.Append(size).Append('|').Append(vertical).Append('|').Append(rightDocked).Append('|').Append(properties).Append('|').Append(width)
            .Append('|').Append(compact).Append('|').Append(available.Height).Append('|').Append(rectangleShows).Append('|')
            .Append(string.Join(',', modes)).Append('|').Append(pending?.Phase).Append('|').Append(zoomControls).Append('|')
            .Append(effective).Append('|').Append(quick).Append('|')
            .Append(snapshot.SelectionSupportsFill).Append('|').Append(snapshot.SelectionCount > 0).Append('|').Append(shared)
            .Append('|').Append(plan?.Key).Append('|').Append(warning);
        foreach (ShortcutCommand item in visible)
        {
            structure.Append('|').Append(item.Id).Append('=').Append(Key(preferences, item, rectangleShows));
        }
        structure.Append('|').Append(LibraryKey(preferences));
        return new ToolbarLook
        {
            Metrics = metrics,
            Vertical = vertical,
            RightDocked = rightDocked,
            Properties = properties,
            Available = available,
            Width = width,
            CompactRail = compact,
            Items = items,
            Visible = visible,
            RailWidth = ToolbarToolLayout.Width(visible, size),
            RectangleShows = rectangleShows,
            ShowsModeStrip = strip,
            Board = snapshot.Board,
            ActiveModes = modes,
            PendingZoom = pending,
            ShowsZoomControls = zoomControls,
            EffectiveTool = effective,
            Quick = quick,
            SupportsFill = snapshot.SelectionSupportsFill,
            HasSelection = snapshot.SelectionCount > 0,
            SharedStyle = shared,
            CaptureWarning = warning,
            Plan = plan,
            Structure = structure.ToString(),
        };
    }

    /// <summary>The tool or button key under its glyph: "7", "Shift+7" or "S" (Mac shortcutLabel).</summary>
    public static string Key(in WorkspacePreferences preferences, ShortcutCommand item, Tool? rectangleShows)
    {
        ShortcutCommand command = item.Tool == Tool.Rectangle && rectangleShows is { } shown ? ShortcutCommand.ForTool(shown) : item;
        return preferences.ShortcutFor(command) is { } key ? WindowsShortcuts.Label(key) : string.Empty;
    }

    /// <summary>
    /// Mac toolLibraryShortcut: Ctrl+L opens the Tools window unless a global shortcut uses the same keys. Null then.
    /// </summary>
    public static string? LibraryKey(in WorkspacePreferences preferences)
    {
        foreach (GlobalShortcutAction action in Enum.GetValues<GlobalShortcutAction>())
        {
            if (WindowsShortcuts.Chord(action, preferences) is { Modifiers: ChordModifiers.Ctrl, ScanCode: 0x26 })
            {
                return null;
            }
        }
        return "Ctrl+L";
    }

    private static QuickKind Kind(Tool effective, int selectionCount) => effective switch
    {
        _ when effective.IsPixelEffect => QuickKind.Effect,
        Tool.Cover => QuickKind.Cover,
        Tool.Counter => QuickKind.Counter,
        Tool.Image => QuickKind.Image,
        Tool.Select when selectionCount == 0 => QuickKind.SelectHint,
        Tool.Eraser => QuickKind.Eraser,
        Tool.Text => QuickKind.Text,
        Tool.Pen => QuickKind.Pen,
        _ => QuickKind.Stroke,
    };

    /// <summary>Mac verticalItems: as many tools as fit the height, scrolled so the current one shows.</summary>
    private static ShortcutCommand[] VerticalItems(ShortcutCommand[] items, Tool identity, double availableHeight, ToolbarMetrics metrics)
    {
        int count = Math.Max(1, Math.Min(items.Length, (int)(Math.Max(48, availableHeight - metrics.VerticalReserve) / metrics.VerticalToolPitch)));
        int active = Math.Max(0, Array.IndexOf(items, ShortcutCommand.ForTool(identity)));
        int start = Math.Max(0, Math.Min(active - count + 1, items.Length - count));
        return items[start..(start + count)];
    }
}
