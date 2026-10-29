using System.Collections.Immutable;
using Annotator.Core;
using Annotator.Core.Geometry;

namespace Annotator.Workspace.Canvas;

public enum CanvasCursorKind
{
    /// <summary>The system arrow: Select, Use apps, and canvas controls.</summary>
    Arrow,

    /// <summary>The text cursor: the Text tool and the text being edited.</summary>
    IBeam,

    /// <summary>Shapes, lines, arrows, covers, counters and rulers.</summary>
    Crosshair,

    /// <summary>The tool footprint of the pen, highlighter and eraser, scaled by stroke width and zoom (WIN-OVR-11).</summary>
    Footprint,

    /// <summary>The rotation cursor over a rotation handle and while rotating.</summary>
    Rotation,

    /// <summary>
    /// The laser pointer (Mac ToolCursorFactory.laserCursor): a dot as wide as the trail's head in the laser's color, inside
    /// a white ring and a soft glow, scaled by width and zoom.
    /// </summary>
    Laser,
}

/// <summary>
/// The cursor a canvas asks for (Mac hoverCursor and ToolCursorFactory). For <see cref="CanvasCursorKind.Footprint"/>,
/// <see cref="Tool"/> and <see cref="Width"/> (stroke width times zoom, in points) describe the footprint; the platform
/// draws it as a real cursor at each monitor scale. <see cref="CanvasCursorKind.Laser"/> also has the laser's
/// <see cref="Color"/>, opaque.
/// </summary>
public readonly record struct CanvasCursor(CanvasCursorKind Kind, Tool Tool = Tool.Select, double Width = 0, RGBA Color = default)
{
    public static CanvasCursor Arrow => new(CanvasCursorKind.Arrow);

    public static CanvasCursor IBeam => new(CanvasCursorKind.IBeam, Tool.Text);

    public static CanvasCursor Rotation => new(CanvasCursorKind.Rotation);

    /// <summary>
    /// Mac ToolCursorFactory.cursor(tool:width:color:): Select uses the arrow, Text the text cursor, the laser pointer its dot
    /// in <paramref name="color"/> (the laser's default red without one), and other non-freehand tools a crosshair.
    /// </summary>
    public static CanvasCursor ForTool(Tool tool, double width, RGBA? color = null) =>
        tool == Tool.Select ? Arrow
        : tool == Tool.Text ? IBeam
        : tool == Tool.Laser ? Laser(width, color ?? LaserTrail.DefaultColor)
        : !tool.IsFreehand && tool != Tool.Eraser ? new CanvasCursor(CanvasCursorKind.Crosshair, tool, width)
        : new CanvasCursor(CanvasCursorKind.Footprint, tool, width);

    /// <summary>Mac ToolCursorFactory.laserCursor(width:color:): the color counts without its alpha.</summary>
    public static CanvasCursor Laser(double width, RGBA color) =>
        new(CanvasCursorKind.Laser, Tool.Laser, width, new RGBA(color.Red, color.Green, color.Blue));

    /// <summary>The laser dot's radius in points: half the width, between 2 and 250.</summary>
    public double LaserRadius => Math.Min(250, Math.Max(2, Width / 2));

    /// <summary>The footprint's size in points: five widths for the highlighter, two for the eraser, at most 500.</summary>
    public double FootprintSize => Math.Min(500, Tool == Tool.Highlighter ? Width * 5 : Tool == Tool.Eraser ? Width * 2 : Width);

    /// <summary>
    /// The square cursor image's side in points: the footprint plus 10, between 24 and 520; for the laser, its glow's
    /// diameter plus 2, rounded up.
    /// </summary>
    public double ImageSide => Kind == CanvasCursorKind.Laser
        ? Math.Ceiling(LaserRadius * 2 + 12)
        : Math.Min(520, Math.Max(24, Math.Ceiling(FootprintSize) + 10));

    /// <summary>
    /// The drawn nib, centered in the image: a rectangle 1.6 widths wide for the highlighter (a vertical chisel), else a
    /// circle. The eraser's nib has no fill or center cross.
    /// </summary>
    public Size Nib
    {
        get
        {
            double height = Math.Max(4, FootprintSize);
            return new Size(Tool == Tool.Highlighter ? Math.Min(500, Math.Max(4, Width * 1.6)) : height, height);
        }
    }
}

/// <summary>What changed in a <see cref="CanvasLiveState"/>.</summary>
[Flags]
public enum CanvasChangeKinds
{
    None = 0,

    /// <summary><see cref="CanvasLiveState.Elements"/> or <see cref="CanvasLiveState.Draft"/>.</summary>
    Content = 1,

    Selection = 2,

    /// <summary>The marquee, attach hints, eraser targets or the Smart Draw candidate.</summary>
    Adornments = 4,

    Cursor = 8,

    /// <summary>The text being edited.</summary>
    Text = 16,

    /// <summary>The Smart Draw morph or the eraser fade started, moved on or ended, or the laser pointer's trail changed.</summary>
    Transitions = 32,

    /// <summary>An edit started or ended (<see cref="CanvasLiveState.HasActiveEdit"/>).</summary>
    Edit = 64,

    /// <summary>The canvas started or stopped taking drawing input.</summary>
    Input = 128,
}

/// <summary>
/// A change notification. <paramref name="Dirty"/> is the canvas area in points that needs drawing again, or null for
/// everything that changed (the Mac's needsDisplay); a pen or highlighter stroke reports only its new segments.
/// </summary>
public readonly record struct CanvasChange(CanvasChangeKinds Kinds, Rect? Dirty, ulong Revision);

/// <summary>Things a canvas asks the app to do; they need app windows or files.</summary>
public enum CanvasRequest
{
    /// <summary>Ctrl+S.</summary>
    SaveBoard,

    /// <summary>Ctrl+O.</summary>
    OpenBoard,

    /// <summary>Ctrl+L, or the context menu's More shapes and tools.</summary>
    ToolLibrary,

    /// <summary>The context menu's Save to My shapes...</summary>
    SaveToMyShapes,

    /// <summary>The context menu's Properties: pin and expand the properties.</summary>
    Properties,

    /// <summary>Ctrl+, (Settings).</summary>
    Settings,

    /// <summary>The canvas's accessibility action Focus drawing tools.</summary>
    FocusTools,
}

/// <summary>
/// The text a canvas edits, for the text editor host (spec 013 S-7) and the presenter. <see cref="Frame"/> is where the
/// editor goes, in canvas points; it rotates by <see cref="Rotation"/> around its center (shape labels follow their
/// shape). <see cref="Style"/> is the font, color, opacity and alignment to show.
/// </summary>
/// <param name="ElementId">The text object, or the shape whose label is edited.</param>
/// <param name="PaintFrame">The area a stroke gradient spans: the shape for a label that follows its stroke, else <see cref="Frame"/>.</param>
/// <param name="AccessibilityLabel">The editor's accessible name.</param>
public sealed record CanvasTextSession(Guid ElementId, bool IsLabel, string Text, Rect Frame, double Rotation, Style Style, Rect PaintFrame,
                                       string AccessibilityLabel)
{
    /// <summary>
    /// Where the scene draws the first baseline of <paramref name="text"/> in <see cref="Frame"/>, in canvas points before
    /// the rotation: a shape label's on the Mac's content-sized frame centered on its shape, other text one Mac ascender below
    /// the frame's top (<see cref="MacTextMetrics"/>). The live text, the caret and the selection all use it, so nothing
    /// moves when editing starts or ends.
    /// </summary>
    public double FirstBaseline(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        double size = TextFont.Resolve(Style).Size;
        return IsLabel ? MacTextMetrics.CenteredFirstBaseline(Frame, text, size) : MacTextMetrics.FirstBaseline(Frame, size);
    }
}

/// <summary>
/// Everything a presenter needs to draw one display's canvas now, as an immutable value. Draw
/// <see cref="Elements"/> with <see cref="Draft"/> in place (see <see cref="ComposeElements"/>), the selection, faded
/// and hidden-label sets, then the adornments. The struct copies cheaply and allocates nothing.
/// </summary>
public readonly struct CanvasLiveState
{
    private readonly ImmutableHashSet<Guid>? _selection;
    private readonly ImmutableHashSet<Guid>? _faded;
    private readonly ImmutableHashSet<Guid>? _eraserTargets;
    private readonly ImmutableHashSet<Guid>? _hiddenLabels;

    /// <summary>Increases with every change.</summary>
    public ulong Revision { get; init; }

    /// <summary>The canvas takes drawing input: Draw mode, shown, and the display is prepared.</summary>
    public bool IsDrawing { get; init; }

    /// <summary>
    /// The objects to draw: the display's document with the current edit applied, without the text being edited
    /// (the editor shows it) and without a shape that is morphing (see <see cref="Morph"/>). Objects dragged here
    /// from another display are included.
    /// </summary>
    public ElementList Elements { get; init; }

    /// <summary>
    /// The object being drawn (a stroke, a new shape, or a clicked arrow with its next point). It replaces the object
    /// with its identity in <see cref="Elements"/>, or is new: then it draws after the drawings and below pixel effects
    /// and covers. A presenter that draws it on a separate layer above the scene must draw covers again above it, as
    /// the Mac's selection layer does.
    /// </summary>
    public Element? Draft { get; init; }

    /// <summary>
    /// The objects to draw as selected (outline and handles): empty outside Draw and while the text editor is open,
    /// and without the eraser's targets (Mac SelectionOverlayView).
    /// </summary>
    public ImmutableHashSet<Guid> Selection { get => _selection ?? ImmutableHashSet<Guid>.Empty; init => _selection = value; }

    /// <summary>Objects drawn faded: eraser targets and, with Smart Draw, the stroke under a candidate.</summary>
    public ImmutableHashSet<Guid> FadedIds { get => _faded ?? ImmutableHashSet<Guid>.Empty; init => _faded = value; }

    /// <summary>Objects the eraser will delete on release; draw a dashed accent outline 4 points outside each.</summary>
    public ImmutableHashSet<Guid> EraserTargets { get => _eraserTargets ?? ImmutableHashSet<Guid>.Empty; init => _eraserTargets = value; }

    /// <summary>Shapes whose label is hidden because it is being edited.</summary>
    public ImmutableHashSet<Guid> HiddenLabelIds { get => _hiddenLabels ?? ImmutableHashSet<Guid>.Empty; init => _hiddenLabels = value; }

    /// <summary>The marquee rectangle (accent, 1 point, dashed 4-3), or null.</summary>
    public Rect? Marquee { get; init; }

    /// <summary>The shape points a line or arrow end will attach to if released now (18-point rule).</summary>
    public ImmutableArray<Anchor> AttachHints { get; init; }

    /// <summary>The Smart Draw candidate, already dashed at 75% opacity, or null.</summary>
    public Element? SmartPreview { get; init; }

    /// <summary>A selection region being chosen (Area): dim everything else 40% black.</summary>
    public Rect? RegionSelection { get; init; }

    public CanvasCursor Cursor { get; init; }

    /// <summary>The text or shape label being edited, or null.</summary>
    public CanvasTextSession? Text { get; init; }

    /// <summary>The ink-to-shape morph after Smart Draw recognition, or null.</summary>
    public SnapMorph? Morph { get; init; }

    /// <summary>Linear time of <see cref="Morph"/> from 0 to 1.</summary>
    public double MorphProgress { get; init; }

    /// <summary>Erased drawings fading out after the eraser released, or null.</summary>
    public EraseFade? EraseFade { get; init; }

    /// <summary>Linear time of <see cref="EraseFade"/> from 0 to 1.</summary>
    public double EraseFadeProgress { get; init; }

    /// <summary>
    /// The laser pointer's trail on this display (Mac DrawingCanvas.laserTrail), empty without one: draw it as it shows at
    /// <see cref="LaserTime"/>, above the drawings and covers (<see cref="Annotator.Core.Scene.LaserTrailRenderer"/>). It is
    /// never part of the document.
    /// </summary>
    public LaserTrail Laser { get; init; }

    /// <summary>When the trail is drawn, in seconds on <see cref="CanvasOptions.Clock"/>.</summary>
    public double LaserTime { get; init; }

    /// <summary>A gesture, text edit, clicked arrow or region choice is in progress (Escape cancels or finishes it).</summary>
    public bool HasActiveEdit { get; init; }

    /// <summary>A clicked arrow is being built: Enter or a double-click finishes it.</summary>
    public bool ConstructingArrow { get; init; }

    /// <summary>
    /// A transition runs, or the laser pointer's trail moves or fades: call <see cref="CanvasController.AdvanceTransitions"/>
    /// on each frame until it returns false. A held laser resting on one spot needs no frames.
    /// </summary>
    public bool HasTransitions => Morph is not null || EraseFade is not null || (!Laser.IsEmpty && !Laser.IsResting);

    /// <summary><see cref="Elements"/> with <see cref="Draft"/> in place: one list for a scene builder. Allocates.</summary>
    public ElementList ComposeElements()
    {
        if (Draft is not { } draft)
        {
            return Elements;
        }
        int index = Elements.IndexOf(draft.Id);
        return index >= 0 ? Elements.SetItem(index, draft) : Elements.Add(draft);
    }
}
