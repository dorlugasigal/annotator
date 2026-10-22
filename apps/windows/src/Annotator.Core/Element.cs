// Port of apps/macos/Sources/AnnotationCore/Element.swift.
//
// Value semantics design:
// - Swift structs stay value types. Small ones (Point, Rect, RGBA, Anchor, ColorGradient) are readonly
//   record structs; change them with `with`.
// - Larger mutable Swift structs (Element, Style, ActivationShortcut, CursorHighlightSettings,
//   WorkspacePreferences, DrawingFile) are mutable C# structs. A Swift stored `var` is a public field, so
//   `element.Style.Width = 6` changes the variable's own copy, exactly as in Swift. Computed Swift
//   properties are C# properties; Swift `mutating func`s are struct methods.
// - Every reference held inside a value is immutable: strings, ValueList<T> (Swift arrays and Data),
//   ElementList ([Element]), immutable dictionaries and sets, and the immutable ShapeLabel class.
//   Copying a value therefore never shares mutable state.
// - Call mutating methods on variables, fields or `ref` locals. A value returned by a property or an
//   indexer is a copy; mutating it changes only the copy (CanvasDocument.Update takes a `ref` delegate).
namespace Annotator.Core;

public enum Tool
{
    Select, Pen, Highlighter, Line, Arrow, Rectangle, Ellipse, Diamond, Text, Eraser, Cover, Blur,
    Pixelate, Distort, Ruler, Counter, Image,
    Database, Queue, Person, Grid, Axes,
    Triangle, Hexagon, Star,
    DatabaseStack, Server, Messaging, Web, Mobile, Desktop, Laptop, Document, Configuration,
    LoadBalancer, ApiGateway, Envelope, Cart, Bell,
}

public static class ToolExtensions
{
    internal static readonly RawValueMap<Tool> RawValues = new(
        "select", "pen", "highlighter", "line", "arrow", "rectangle", "ellipse", "diamond", "text", "eraser", "cover", "blur",
        "pixelate", "distort", "ruler", "counter", "image",
        "database", "queue", "person", "grid", "axes",
        "triangle", "hexagon", "star",
        "databaseStack", "server", "messaging", "web", "mobile", "desktop", "laptop", "document", "configuration",
        "loadBalancer", "apiGateway", "envelope", "cart", "bell");

    private static readonly ValueList<Tool> SystemDesign =
    [
        Tool.DatabaseStack, Tool.Server, Tool.Messaging, Tool.Web, Tool.Mobile, Tool.Desktop, Tool.Laptop,
        Tool.Document, Tool.Configuration, Tool.LoadBalancer, Tool.ApiGateway, Tool.Envelope, Tool.Cart, Tool.Bell,
    ];

    extension(Tool)
    {
        public static ValueList<Tool> AllCases => RawValues.AllCases;

        public static ValueList<Tool> SystemDesignShapes => SystemDesign;

        /// <summary>Swift `Tool(rawValue:)`.</summary>
        public static Tool? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }

    extension(Tool tool)
    {
        public string RawValue => RawValues.RawValue(tool);

        public string Label => tool switch
        {
            Tool.Select => "Select",
            Tool.Pen => "Pen",
            Tool.Highlighter => "Highlighter",
            Tool.Line => "Line",
            Tool.Arrow => "Arrow",
            Tool.Rectangle => "Rectangle",
            Tool.Ellipse => "Ellipse",
            Tool.Diamond => "Diamond",
            Tool.Text => "Text",
            Tool.Eraser => "Eraser",
            Tool.Cover => "Censor (solid)",
            Tool.Blur => "Blur region",
            Tool.Pixelate => "Pixelate region",
            Tool.Distort => "Distort region",
            Tool.Ruler => "Pixel ruler",
            Tool.Counter => "Numbered counter",
            Tool.Image => "Paste image",
            Tool.Database => "Cylinder",
            Tool.Queue => "Queue",
            Tool.Person => "Person",
            Tool.Grid => "Grid",
            Tool.Axes => "Axes",
            Tool.Triangle => "Triangle",
            Tool.Hexagon => "Hexagon",
            Tool.Star => "Star",
            Tool.DatabaseStack => "Database",
            Tool.Server => "Server",
            Tool.Messaging => "Messaging",
            Tool.Web => "Web",
            Tool.Mobile => "Mobile phone",
            Tool.Desktop => "Desktop",
            Tool.Laptop => "Laptop",
            Tool.Document => "Document",
            Tool.Configuration => "Configuration",
            Tool.LoadBalancer => "Load balancer",
            Tool.ApiGateway => "API gateway",
            Tool.Envelope => "Envelope",
            Tool.Cart => "Cart",
            Tool.Bell => "Bell",
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };

        public bool IsPixelEffect => tool is Tool.Blur or Tool.Pixelate or Tool.Distort;

        public bool IsProtection => tool == Tool.Cover || tool.IsPixelEffect;

        public bool IsFreehand => tool is Tool.Pen or Tool.Highlighter;

        public bool IsConnector => tool is Tool.Line or Tool.Arrow;

        public bool IsShape =>
            tool is Tool.Rectangle or Tool.Ellipse or Tool.Diamond or Tool.Database or Tool.Queue or Tool.Person
                or Tool.Grid or Tool.Axes or Tool.Triangle or Tool.Hexagon or Tool.Star
            || SystemDesign.Contains(tool);

        public PolygonShapeKind? PolygonKind => tool switch
        {
            Tool.Triangle => PolygonShapeKind.Triangle,
            Tool.Hexagon => PolygonShapeKind.Hexagon,
            Tool.Star => PolygonShapeKind.Star,
            _ => null,
        };

        public bool SupportsFill => tool.IsShape && tool != Tool.Axes;
    }
}

/// <summary>A color with channels from 0 to 1. Equality uses `==` on each channel, like Swift.</summary>
/// <remarks>The Swift presets `Palette.ink`, `.coral`, `.yellow`, `.blue` and `.white` are in <see cref="Palette"/>,
/// because C# cannot name a static color `Blue` next to the `Blue` channel.</remarks>
public readonly record struct RGBA(double Red, double Green, double Blue, double Alpha = 1)
{
    public bool IsValid => IsUnit(Red) && IsUnit(Green) && IsUnit(Blue) && IsUnit(Alpha);

    public bool Equals(RGBA other) => Red == other.Red && Green == other.Green && Blue == other.Blue && Alpha == other.Alpha;

    public override int GetHashCode() => HashCode.Combine(Red, Green, Blue, Alpha);

    private static bool IsUnit(double value) => double.IsFinite(value) && value >= 0 && value <= 1;
}

/// <summary>Swift's static `RGBA` presets.</summary>
public static class Palette
{
    public static RGBA Ink => new(0.12, 0.15, 0.16);

    public static RGBA Coral => new(0.94, 0.27, 0.22);

    public static RGBA Yellow => new(0.96, 0.89, 0.22);

    public static RGBA Blue => new(0.17, 0.45, 0.88);

    public static RGBA White => new(1, 1, 1);
}

public enum StrokePattern { Solid, Dashed, Dotted }

public enum FillPattern { Solid, Hatch, Crosshatch }

public enum ArrowHead
{
    None, Open, Filled, Circle, Square, Bar,
    TriangleOutline, CircleOutline, Diamond, DiamondOutline,
    CardinalityOne, CardinalityMany, CardinalityOneOrMany,
    CardinalityExactlyOne, CardinalityZeroOrOne, CardinalityZeroOrMany,
}

public enum TextAlignment { Left, Center, Right }

public static class StrokePatternExtensions
{
    internal static readonly RawValueMap<StrokePattern> RawValues = new("solid", "dashed", "dotted");

    extension(StrokePattern pattern)
    {
        public string RawValue => RawValues.RawValue(pattern);
    }

    extension(StrokePattern)
    {
        public static ValueList<StrokePattern> AllCases => RawValues.AllCases;

        public static StrokePattern? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

public static class FillPatternExtensions
{
    internal static readonly RawValueMap<FillPattern> RawValues = new("solid", "hatch", "crosshatch");

    extension(FillPattern pattern)
    {
        public string RawValue => RawValues.RawValue(pattern);
    }

    extension(FillPattern)
    {
        public static ValueList<FillPattern> AllCases => RawValues.AllCases;

        public static FillPattern? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

public static class TextAlignmentExtensions
{
    internal static readonly RawValueMap<TextAlignment> RawValues = new("left", "center", "right");

    extension(TextAlignment alignment)
    {
        public string RawValue => RawValues.RawValue(alignment);
    }

    extension(TextAlignment)
    {
        public static ValueList<TextAlignment> AllCases => RawValues.AllCases;

        public static TextAlignment? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

public static class ArrowHeadExtensions
{
    internal static readonly RawValueMap<ArrowHead> RawValues = new(
        "none", "open", "filled", "circle", "square", "bar",
        "triangleOutline", "circleOutline", "diamond", "diamondOutline",
        "cardinalityOne", "cardinalityMany", "cardinalityOneOrMany",
        "cardinalityExactlyOne", "cardinalityZeroOrOne", "cardinalityZeroOrMany");

    private static readonly ValueList<ArrowHead> Shapes =
    [
        ArrowHead.None, ArrowHead.Open, ArrowHead.Filled, ArrowHead.TriangleOutline, ArrowHead.Circle,
        ArrowHead.CircleOutline, ArrowHead.Diamond, ArrowHead.DiamondOutline, ArrowHead.Bar, ArrowHead.Square,
    ];

    private static readonly ValueList<ArrowHead> Relationships =
    [
        ArrowHead.CardinalityOne, ArrowHead.CardinalityMany, ArrowHead.CardinalityOneOrMany,
        ArrowHead.CardinalityExactlyOne, ArrowHead.CardinalityZeroOrOne, ArrowHead.CardinalityZeroOrMany,
    ];

    extension(ArrowHead head)
    {
        public string RawValue => RawValues.RawValue(head);

        public string Label => head switch
        {
            ArrowHead.None => "None",
            ArrowHead.Open => "Arrow",
            ArrowHead.Filled => "Filled triangle",
            ArrowHead.TriangleOutline => "Outlined triangle",
            ArrowHead.Circle => "Filled circle",
            ArrowHead.CircleOutline => "Outlined circle",
            ArrowHead.Diamond => "Filled diamond",
            ArrowHead.DiamondOutline => "Outlined diamond",
            ArrowHead.Square => "Square",
            ArrowHead.Bar => "Bar",
            ArrowHead.CardinalityOne => "One",
            ArrowHead.CardinalityMany => "Many",
            ArrowHead.CardinalityOneOrMany => "One or many",
            ArrowHead.CardinalityExactlyOne => "Exactly one",
            ArrowHead.CardinalityZeroOrOne => "Zero or one",
            ArrowHead.CardinalityZeroOrMany => "Zero or many",
            _ => throw new ArgumentOutOfRangeException(nameof(head)),
        };
    }

    extension(ArrowHead)
    {
        public static ValueList<ArrowHead> AllCases => RawValues.AllCases;

        public static ValueList<ArrowHead> ShapeChoices => Shapes;

        public static ValueList<ArrowHead> RelationshipChoices => Relationships;

        public static ArrowHead? FromRawValue(string rawValue) => RawValues.Parse(rawValue);
    }
}

/// <summary>Swift `Style`. Stored properties are public fields; see the design note at the top of this file.</summary>
public partial struct Style : IEquatable<Style>
{
    public RGBA Color = Palette.Coral;
    public RGBA? Fill;
    public double Width = 3;
    public double Opacity = 1;
    public StrokePattern Pattern = StrokePattern.Solid;
    public FillPattern FillPattern = FillPattern.Solid;
    public double Roughness;
    public double CornerRadius;
    public double FontSize = 28;
    public string FontName = "system";
    public bool Bold;
    public TextAlignment Alignment = TextAlignment.Left;
    public bool Curved;
    public ArrowHead StartHead = ArrowHead.None;
    public ArrowHead EndHead = ArrowHead.Open;
    public bool Pressure;
    public double? HeadScale;
    public double? EffectAmount;
    public long? GridRows;
    public long? GridColumns;
    public bool? NegativeAxes;
    public long? AxisTickCount;
    public ColorGradient? StrokeGradient;
    public ColorGradient? FillGradient;

    public Style()
    {
    }

    public readonly void Validate()
    {
        StrokeGradient?.Validate();
        FillGradient?.Validate();
        if (FillGradient is not null && Fill is null)
        {
            throw CanvasError.InvalidStyle;
        }
        if (AxisTickCount is { } ticks && !(ticks >= 0 && ticks <= 24))
        {
            throw CanvasError.InvalidStyle;
        }
        if (GridRows is { } rows && !(rows >= 1 && rows <= 24))
        {
            throw CanvasError.InvalidStyle;
        }
        if (GridColumns is { } columns && !(columns >= 1 && columns <= 24))
        {
            throw CanvasError.InvalidStyle;
        }
        if (HeadScale is { } headScale && (!double.IsFinite(headScale) || !(headScale >= 0.5 && headScale <= 2)))
        {
            throw CanvasError.InvalidStyle;
        }
        if (EffectAmount is { } amount && (!double.IsFinite(amount) || !(amount >= 0 && amount <= 100)))
        {
            throw CanvasError.InvalidStyle;
        }
        if (!(Color.IsValid && Fill?.IsValid != false
              && double.IsFinite(Width) && Width >= 0.25 && Width <= 256
              && double.IsFinite(Opacity) && Opacity >= 0 && Opacity <= 1
              && double.IsFinite(Roughness) && Roughness >= 0 && Roughness <= 12
              && double.IsFinite(CornerRadius) && CornerRadius >= 0 && CornerRadius <= 1_000
              && double.IsFinite(FontSize) && FontSize >= 4 && FontSize <= 1_024
              && SwiftText.Utf8Count(FontName) <= 256))
        {
            throw CanvasError.InvalidStyle;
        }
    }

    public readonly bool Equals(Style other) =>
        Color == other.Color && Fill == other.Fill && Width == other.Width && Opacity == other.Opacity
        && Pattern == other.Pattern && FillPattern == other.FillPattern && Roughness == other.Roughness
        && CornerRadius == other.CornerRadius && FontSize == other.FontSize
        && SwiftText.Equals(FontName, other.FontName) && Bold == other.Bold
        && Alignment == other.Alignment && Curved == other.Curved && StartHead == other.StartHead
        && EndHead == other.EndHead && Pressure == other.Pressure && HeadScale == other.HeadScale
        && EffectAmount == other.EffectAmount && GridRows == other.GridRows && GridColumns == other.GridColumns
        && NegativeAxes == other.NegativeAxes && AxisTickCount == other.AxisTickCount
        && StrokeGradient == other.StrokeGradient && FillGradient == other.FillGradient;

    public override readonly bool Equals(object? obj) => obj is Style other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Color, Fill, Width, Opacity, FontSize, SwiftText.GetHashCode(FontName), StartHead, EndHead);

    public static bool operator ==(Style left, Style right) => left.Equals(right);

    public static bool operator !=(Style left, Style right) => !left.Equals(right);
}

/// <summary>A connector end attached to a shape at a relative position (0...1 on each axis).</summary>
public readonly record struct Anchor(Guid ElementId, Point Relative);

/// <summary>Swift `Element`. Stored properties are public fields; see the design note at the top of this file.</summary>
public partial struct Element : IEquatable<Element>
{
    // Swift's estimatedBytes uses max(512, MemoryLayout<Element>.stride); the Mac corpus (platform/layout.json)
    // measures a stride of 528 bytes, so history budgets count 528 bytes per object, as on the Mac.
    internal const long MinimumEstimatedBytes = 528;

    public Guid Id;
    public Tool Tool;
    public Rect Frame;
    public ValueList<Point> Points;
    public ValueList<double> Pressures;
    public string Text;
    public Style Style;
    public double Rotation;
    public Guid? GroupId;
    public bool Locked;
    public Anchor? StartAnchor;
    public Anchor? EndAnchor;
    public ulong Seed;
    public ulong Revision;
    public Guid GeometryToken;
    public Guid AppendEpoch;
    public ValueList<byte>? ImagePng;
    public ShapeLabel? Label;

    /// <summary>Swift `Element(id:tool:frame:points:text:style:)`. A new identifier is made when <paramref name="id"/> is null.</summary>
    public Element(Tool tool, Rect frame = default, ValueList<Point> points = default, string text = "",
                   Style? style = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Tool = tool;
        Frame = points.IsEmpty ? frame : Rect.Enclosing(points);
        Points = points;
        Pressures = default;
        Text = text;
        Style = style ?? new Style();
        Rotation = 0;
        GroupId = null;
        Locked = false;
        StartAnchor = null;
        EndAnchor = null;
        Seed = SeedFor(Id);
        Revision = 0;
        GeometryToken = Guid.NewGuid();
        AppendEpoch = Guid.NewGuid();
        ImagePng = null;
        Label = null;
    }

    public readonly long EstimatedBytes =>
        MinimumEstimatedBytes + Points.Count * 16L + Pressures.Count * 8L + SwiftText.Utf8Count(Text)
        + (ImagePng?.Count ?? 0) + (Label?.EstimatedBytes ?? 0) + Style.PaintEstimatedBytes;

    /// <summary>Relative points where connectors can attach to this shape.</summary>
    public readonly ValueList<Point> AnchorLocations
    {
        get
        {
            if (Tool.PolygonKind is not { } kind)
            {
                return [new Point(0.5, 0), new Point(1, 0.5), new Point(0.5, 1), new Point(0, 0.5)];
            }
            var vertices = PolygonShape.Vertices(kind, new Rect(0, 0, 1, 1));
            var result = new Point[vertices.Count * 2];
            for (var index = 0; index < vertices.Count; index++)
            {
                result[index] = vertices[index];
                result[vertices.Count + index] = (vertices[index] + vertices[(index + 1) % vertices.Count]) * 0.5;
            }
            return ValueList.Create<Point>(result);
        }
    }

    /// <summary>`id.uuidString.utf8.reduce(5381) { ($0 &amp;* 33) &amp;+ UInt64($1) }` over the uppercase UUID string.</summary>
    public static ulong SeedFor(Guid id)
    {
        Span<char> text = stackalloc char[36];
        id.TryFormat(text, out _, "D");
        ulong hash = 5381;
        foreach (var character in text)
        {
            var upper = character is >= 'a' and <= 'f' ? (char)(character - 32) : character;
            hash = unchecked(hash * 33 + upper);
        }
        return hash;
    }

    public readonly void Validate()
    {
        if (Label is not null)
        {
            if (!Tool.IsShape)
            {
                throw CanvasError.InvalidDocument("Only shapes can own a shape label.");
            }
            Label.Validate();
        }
        if (Tool == Tool.Image)
        {
            if (ImagePng is not { } image || image.Count > 32 * 1_024 * 1_024 || !image.AsSpan().StartsWith(PngSignature))
            {
                throw CanvasError.InvalidDocument("An image element must contain a bounded PNG image.");
            }
        }
        else if (ImagePng is not null)
        {
            throw CanvasError.InvalidDocument("Unexpected image data on a drawing tool.");
        }
        if (Tool.IsProtection && Rotation != 0)
        {
            throw CanvasError.InvalidDocument("Censor and pixel-effect regions must remain axis-aligned.");
        }
        if (!Frame.IsValid || !AllFinite(Points) || !double.IsFinite(Rotation) || !PressuresValid(Pressures)
            || !(Pressures.IsEmpty || Pressures.Count == Points.Count)
            || SwiftText.Utf8Count(Text) > 1_000_000 || Tool == Tool.Select || Tool == Tool.Eraser)
        {
            throw CanvasError.InvalidGeometry;
        }
        ValidateAnchor(StartAnchor, Tool);
        ValidateAnchor(EndAnchor, Tool);
        Style.Validate();
    }

    public void Translate(Point offset)
    {
        Frame = Frame.Translated(offset);
        if (!Points.IsEmpty)
        {
            Points = Points.Map(point => point + offset);
        }
        Revision = unchecked(Revision + 1);
        InvalidateGeometry();
    }

    public void Resize(Rect newFrame)
    {
        var prior = Frame;
        if (Label is { } label && prior.Width > 0 && prior.Height > 0)
        {
            var labelStyle = label.Style;
            var factor = SwiftMath.Min(newFrame.Width / prior.Width, newFrame.Height / prior.Height);
            labelStyle.FontSize = SwiftMath.Min(1024, SwiftMath.Max(4, labelStyle.FontSize * factor));
            Label = new ShapeLabel(label.Text, labelStyle, label.FollowsStroke);
        }
        if (!Points.IsEmpty)
        {
            Points = Points.Map(point => new Point(
                newFrame.X + (point.X - prior.X) * newFrame.Width / SwiftMath.Max(prior.Width, 0.001),
                newFrame.Y + (point.Y - prior.Y) * newFrame.Height / SwiftMath.Max(prior.Height, 0.001)));
        }
        Frame = newFrame;
        Revision = unchecked(Revision + 1);
        InvalidateGeometry();
    }

    public void InvalidateGeometry()
    {
        GeometryToken = Guid.NewGuid();
        AppendEpoch = Guid.NewGuid();
    }

    public readonly Point AnchorPoint(Point relative)
    {
        var symbol = SymbolFrame;
        return new Point(symbol.X + relative.X * symbol.Width, symbol.Y + relative.Y * symbol.Height)
            .Rotated(Frame.Center, Rotation);
    }

    public readonly bool Equals(in Element other) =>
        Id == other.Id && Tool == other.Tool && Frame == other.Frame && Points == other.Points
        && Pressures == other.Pressures && SwiftText.Equals(Text, other.Text)
        && Style == other.Style && Rotation == other.Rotation && GroupId == other.GroupId && Locked == other.Locked
        && StartAnchor == other.StartAnchor && EndAnchor == other.EndAnchor && Seed == other.Seed
        && Revision == other.Revision && GeometryToken == other.GeometryToken && AppendEpoch == other.AppendEpoch
        && ImagePng == other.ImagePng && Equals(Label, other.Label);

    readonly bool IEquatable<Element>.Equals(Element other) => Equals(in other);

    public override readonly bool Equals(object? obj) => obj is Element other && Equals(in other);

    public override readonly int GetHashCode() => HashCode.Combine(Id, Tool, Revision);

    public static bool operator ==(in Element left, in Element right) => left.Equals(in right);

    public static bool operator !=(in Element left, in Element right) => !left.Equals(in right);

    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    private static void ValidateAnchor(Anchor? anchor, Tool tool)
    {
        if (anchor is not { } value)
        {
            return;
        }
        if (!tool.IsConnector || !value.Relative.IsFinite
            || !(value.Relative.X >= 0 && value.Relative.X <= 1) || !(value.Relative.Y >= 0 && value.Relative.Y <= 1))
        {
            throw CanvasError.InvalidGeometry;
        }
    }

    private static bool AllFinite(ValueList<Point> points)
    {
        foreach (ref readonly var point in points)
        {
            if (!point.IsFinite)
            {
                return false;
            }
        }
        return true;
    }

    private static bool PressuresValid(ValueList<double> pressures)
    {
        foreach (var pressure in pressures.AsSpan())
        {
            if (!double.IsFinite(pressure) || !(pressure >= 0 && pressure <= 1))
            {
                return false;
            }
        }
        return true;
    }
}

public static class HitTesting
{
    public static bool Contains(Point point, in Element element, double tolerance = 6)
    {
        var p = point.Rotated(element.Frame.Center, -element.Rotation);
        var strokeWidth = element.Tool == Tool.Highlighter ? element.Style.Width * 5 : element.Style.Width;
        var hitRadius = tolerance + strokeWidth / 2;
        if (element.Tool == Tool.Arrow)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                if (ArrowHeadGeometry.Make(element, atStart: pass == 0) is { } head && head.Contains(p, hitRadius))
                {
                    return true;
                }
            }
        }
        if (element.Tool.IsConnector && element.Style.Curved && element.Points.Count >= 3)
        {
            foreach (var curve in ConnectorGeometry.Curves(element.Points))
            {
                if (curve.Contains(p, hitRadius))
                {
                    return true;
                }
            }
            return false;
        }
        if (element.Tool == Tool.Ruler && !element.Points.IsEmpty)
        {
            var first = element.Points[0];
            var last = element.Points[element.Points.Count - 1];
            var label = new Rect((first.X + last.X) / 2 - 60, (first.Y + last.Y) / 2 - 25, 120, 22);
            return label.Contains(p, tolerance) || Distance(p, first, last) <= hitRadius;
        }
        var hitFrame = element.SymbolFrame;
        if (!hitFrame.Contains(p, hitRadius))
        {
            return false;
        }
        if (element.Tool.PolygonKind is { } kind)
        {
            return PolygonShape.Contains(p, PolygonShape.Vertices(kind, element.Frame), hitRadius);
        }
        if (element.Tool.IsFreehand || element.Tool.IsConnector)
        {
            var points = element.Points.AsSpan();
            if (points.Length == 1)
            {
                return p.Distance(points[0]) <= hitRadius;
            }
            for (var index = 1; index < points.Length; index++)
            {
                if (Distance(p, points[index - 1], points[index]) <= hitRadius)
                {
                    return true;
                }
            }
            return false;
        }
        if ((element.Tool == Tool.Ellipse || element.Tool == Tool.Counter || element.Tool == Tool.Web)
            && element.Frame.Width > 0 && element.Frame.Height > 0)
        {
            var dx = (p.X - hitFrame.Center.X) / (hitFrame.Width / 2 + tolerance);
            var dy = (p.Y - hitFrame.Center.Y) / (hitFrame.Height / 2 + tolerance);
            return dx * dx + dy * dy <= 1;
        }
        if (element.Tool == Tool.Diamond && element.Frame.Width > 0 && element.Frame.Height > 0)
        {
            if (element.Style.CornerRadius > 0)
            {
                return RoundedDiamond.Contains(p, element.Frame, element.Style.CornerRadius, hitRadius);
            }
            return Math.Abs(p.X - element.Frame.Center.X) / (element.Frame.Width / 2 + tolerance)
                + Math.Abs(p.Y - element.Frame.Center.Y) / (element.Frame.Height / 2 + tolerance) <= 1;
        }
        return true;
    }

    /// <summary>Distance from a point to the segment from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static double Distance(Point p, Point a, Point b)
    {
        var d = b - a;
        var length = d.X * d.X + d.Y * d.Y;
        if (!(length > 0))
        {
            return p.Distance(a);
        }
        var t = SwiftMath.Min(1, SwiftMath.Max(0, ((p.X - a.X) * d.X + (p.Y - a.Y) * d.Y) / length));
        return p.Distance(a + d * t);
    }
}
