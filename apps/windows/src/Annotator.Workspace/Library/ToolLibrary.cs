using System.Collections.Immutable;
using Annotator.Core;
using Annotator.Workspace.Shortcuts;

namespace Annotator.Workspace.Library;

/// <summary>Port of ToolLibrarySection (ToolLibrary.swift): the Tools window's categories in the Mac's order.</summary>
public enum ToolLibrarySection
{
    Draw,
    Shapes,
    Focus,

    /// <summary>The Mac's <c>images</c> case, shown as "Extras".</summary>
    Extras,

    MyShapes,
}

public static class ToolLibrarySectionExtensions
{
    private static readonly ImmutableArray<ToolLibrarySection> s_all =
        [ToolLibrarySection.Draw, ToolLibrarySection.Shapes, ToolLibrarySection.Focus, ToolLibrarySection.Extras, ToolLibrarySection.MyShapes];

    private static readonly ImmutableArray<Tool> s_draw =
        [Tool.Select, Tool.Pen, Tool.Highlighter, Tool.Laser, Tool.Text, Tool.Arrow, Tool.Line, Tool.Eraser];

    private static readonly ImmutableArray<Tool> s_shapes =
    [
        Tool.Rectangle, Tool.Ellipse, Tool.Diamond, Tool.Triangle, Tool.Hexagon, Tool.Star, Tool.Person,
        .. Tool.SystemDesignShapes, Tool.Database, Tool.Queue, Tool.Grid, Tool.Axes,
    ];

    extension(ToolLibrarySection section)
    {
        /// <summary>The Mac's raw value, also the segment's title: Draw, Shapes, Focus, Extras or My shapes.</summary>
        public string RawValue => section switch
        {
            ToolLibrarySection.Draw => "Draw",
            ToolLibrarySection.Shapes => "Shapes",
            ToolLibrarySection.Focus => "Focus",
            ToolLibrarySection.Extras => "Extras",
            ToolLibrarySection.MyShapes => "My shapes",
            _ => throw new ArgumentOutOfRangeException(nameof(section)),
        };

        /// <summary>The tools the section's grid offers, in the Mac's order (ToolLibrarySection.tools).</summary>
        public ImmutableArray<Tool> Tools => section switch
        {
            ToolLibrarySection.Draw => s_draw,
            ToolLibrarySection.Shapes => s_shapes,
            ToolLibrarySection.Focus => [Tool.Cover],
            ToolLibrarySection.Extras => [Tool.Image, Tool.Ruler, Tool.Counter],
            ToolLibrarySection.MyShapes => [],
            _ => throw new ArgumentOutOfRangeException(nameof(section)),
        };
    }

    extension(ToolLibrarySection)
    {
        public static ImmutableArray<ToolLibrarySection> AllCases => s_all;

        /// <summary>Mac ToolLibrarySection.containing: the section that offers a tool; every effect is in Focus.</summary>
        public static ToolLibrarySection Containing(Tool tool)
        {
            if (tool.IsProtection)
            {
                return ToolLibrarySection.Focus;
            }
            foreach (ToolLibrarySection section in s_all)
            {
                if (section.Tools.Contains(tool))
                {
                    return section;
                }
            }
            return ToolLibrarySection.Draw;
        }
    }
}

/// <summary>What choosing a Tools window tile does.</summary>
public enum ToolLibraryAction
{
    /// <summary>Chooses <see cref="ToolLibraryTile.Tool"/> (Effects chooses the last effect, Mac chooseCover).</summary>
    ChooseTool,

    /// <summary>Mac importScreenshot: inserts an image file.</summary>
    InsertImage,

    PointerLens,
    ScreenZoom,
    Spotlight,
    CursorHalo,
    ShareRegion,
}

/// <summary>
/// One tile of the Tools window grid (Mac ToolLibrary.tile): its title, which is also its accessibility label and help, the
/// symbol or tool glyph, the key shown under the title, and whether it is the current tool or a feature that is on.
/// </summary>
/// <param name="Symbol">The Mac SF Symbol name; a tile with a <paramref name="Tool"/> draws the tool's glyph instead.</param>
/// <param name="Shortcut">The key under the title, as Windows shows keys, or null.</param>
/// <param name="Available">False while the tile's feature is not built on Windows; the tile is shown unavailable, never faked.</param>
/// <param name="Explanation">Why an unavailable tile cannot be used, when the toolbar says so too (Text), or null.</param>
public sealed record ToolLibraryTile(ToolLibraryAction Action, string Title, string Symbol, Tool? Tool, string? Shortcut,
                                     bool Selected, bool Available, string? Explanation = null);

/// <summary>The Tools window's grid contents for a workspace state (Mac ToolLibrary.body).</summary>
public static class ToolLibraryTiles
{
    /// <summary>
    /// The tiles of <paramref name="section"/>: its tools, then Insert image in Extras and the presenting tools in Focus, which
    /// the Mac leaves out only for a reference image (Windows has none).
    /// </summary>
    public static ImmutableArray<ToolLibraryTile> For(ToolLibrarySection section, WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        WorkspacePreferences preferences = snapshot.Preferences;
        var tiles = ImmutableArray.CreateBuilder<ToolLibraryTile>();
        foreach (Tool tool in section.Tools)
        {
            string title = tool == Tool.Image ? "Paste image" : tool == Tool.Cover ? "Effects" : tool.Label;
            bool selected = snapshot.Tool == tool || (tool == Tool.Cover && snapshot.Tool.IsProtection);
            Tool chosen = tool == Tool.Cover ? PreferredEffect(preferences) : tool;
            bool textUnavailable = tool == Tool.Text && !snapshot.TextEditingAvailable;
            tiles.Add(new ToolLibraryTile(ToolLibraryAction.ChooseTool, title, ToolbarSymbol(tool), tool,
                Key(preferences, ShortcutCommand.ForTool(tool)), selected,
                WorkspaceModel.IsAvailable(chosen) && !textUnavailable,
                textUnavailable ? WorkspaceModel.TextUnavailableMessage : null));
        }
        if (section == ToolLibrarySection.Extras)
        {
            // Mac importScreenshot: the host shows the Open dialog for an image file.
            tiles.Add(new ToolLibraryTile(ToolLibraryAction.InsertImage, "Insert image", "folder", null, null, false, true));
        }
        if (section == ToolLibrarySection.Focus)
        {
            tiles.Add(Presenting(ToolLibraryAction.PointerLens, "Pointer lens", "plus.magnifyingglass", snapshot, ShortcutAction.PointerLens));
            tiles.Add(new ToolLibraryTile(ToolLibraryAction.ScreenZoom, "Screen zoom", "arrow.up.left.and.arrow.down.right", null,
                WindowsShortcuts.Label(GlobalShortcutAction.LiveZoom, preferences), snapshot.ScreenZoomEnabled, true));
            tiles.Add(Presenting(ToolLibraryAction.Spotlight, "Spotlight", "light.beacon.max", snapshot, ShortcutAction.Spotlight));
            tiles.Add(Presenting(ToolLibraryAction.CursorHalo, "Cursor halo", "cursorarrow.rays", snapshot, ShortcutAction.CursorHalo));
            tiles.Add(Presenting(ToolLibraryAction.ShareRegion, "Share region", "rectangle.on.rectangle", snapshot, ShortcutAction.ShareRegion));
        }
        return tiles.ToImmutable();
    }

    /// <summary>Mac preferredEffectTool: the last effect the person chose, else Blur. Effects chooses it.</summary>
    public static Tool PreferredEffect(in WorkspacePreferences preferences) =>
        preferences.LastCoverMode is { IsPixelEffect: true } last ? last : Tool.Blur;

    /// <summary>The Mac toolbarSymbol of a tool (ToolLibrary.swift).</summary>
    public static string ToolbarSymbol(Tool tool) => tool switch
    {
        Tool.Select => "cursorarrow",
        Tool.Rectangle => "rectangle",
        Tool.Diamond => "diamond",
        Tool.Ellipse => "circle",
        Tool.Triangle => "triangle",
        Tool.Hexagon => "hexagon",
        Tool.Star => "star",
        Tool.Arrow => "arrow.up.right",
        Tool.Line => "line.diagonal",
        Tool.Pen => "pencil",
        Tool.Text => "textformat",
        Tool.Highlighter => "highlighter",
        Tool.Laser => "laser.burst",
        Tool.Eraser => "eraser",
        Tool.Cover => "camera.filters",
        Tool.Blur => "aqi.medium",
        Tool.Pixelate => "square.grid.3x3.fill",
        Tool.Distort => "tornado",
        Tool.Ruler => "ruler",
        Tool.Counter => "1.circle",
        Tool.Image => "photo.on.rectangle",
        Tool.Database => "cylinder",
        Tool.DatabaseStack => "cylinder.split.1x2",
        Tool.Server => "server.rack",
        Tool.Messaging => "bubble.left.and.bubble.right",
        Tool.Web => "globe",
        Tool.Mobile => "iphone",
        Tool.Desktop => "desktopcomputer",
        Tool.Laptop => "laptopcomputer",
        Tool.Document => "doc.text",
        Tool.Configuration => "gearshape",
        Tool.LoadBalancer => "arrow.triangle.branch",
        Tool.ApiGateway => "arrow.right.square",
        Tool.Envelope => "envelope",
        Tool.Cart => "cart",
        Tool.Bell => "bell",
        Tool.Queue => "rectangle.stack",
        Tool.Person => "person",
        Tool.Grid => "grid",
        Tool.Axes => "chart.xyaxis.line",
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    /// <summary>
    /// A presenting tool's tile: current while its feature is on (Mac selected: model.spotlight, regionSharingState.isCapturing
    /// and the like).
    /// </summary>
    private static ToolLibraryTile Presenting(ToolLibraryAction action, string title, string symbol, WorkspaceSnapshot snapshot,
                                              ShortcutAction button) =>
        new(action, title, symbol, null, Key(snapshot.Preferences, ShortcutCommand.ForAction(button)), snapshot.IsOn(button),
            WorkspaceModel.IsAvailable(button));

    private static string? Key(in WorkspacePreferences preferences, ShortcutCommand command) =>
        preferences.ShortcutFor(command) is { } key ? WindowsShortcuts.Label(key) : null;
}
