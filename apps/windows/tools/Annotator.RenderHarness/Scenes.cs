using System.Runtime.CompilerServices;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.RenderHarness;

/// <summary>Scenes for the harness: the visual demo, latency patches and the WIN-PERF-01 workloads.</summary>
internal static class Scenes
{
    private const ulong FixtureSeed = 0xA660_2026;

    /// <summary>A board with every kind of drawing, placed from (40, 40) points.</summary>
    public static Element[] Demo(byte[] imagePng)
    {
        var list = new List<Element>();
        Element Add(Element element)
        {
            list.Add(element);
            return element;
        }
        var rough = Add(new Element(Tool.Rectangle, new Rect(40, 40, 220, 140)));
        rough.Style.Roughness = 2;
        rough.Style.Fill = new RGBA(0.17, 0.45, 0.88, 0.35);
        rough.Style.FillPattern = FillPattern.Hatch;
        list[^1] = rough;
        var ellipse = Add(new Element(Tool.Ellipse, new Rect(300, 40, 200, 140)));
        ellipse.Style.Width = 4;
        ellipse.Style.Fill = new RGBA(1, 1, 1);
        ellipse.Style.FillGradient = new ColorGradient([new RGBA(0.96, 0.89, 0.22), new RGBA(0.94, 0.27, 0.22)], 30);
        list[^1] = ellipse;
        var diamond = Add(new Element(Tool.Diamond, new Rect(540, 40, 160, 140)));
        diamond.Style.CornerRadius = 18;
        diamond.Style.Color = Palette.Blue;
        list[^1] = diamond;
        var database = Add(new Element(Tool.DatabaseStack, new Rect(740, 40, 120, 140)));
        database.Style.Color = Palette.Ink;
        list[^1] = database;

        var arrow = new Element(Tool.Arrow, points: Points(new Point(60, 330), new Point(220, 220), new Point(420, 320)));
        arrow.Style.Curved = true;
        arrow.Style.Width = 5;
        arrow.Style.StartHead = ArrowHead.CircleOutline;
        arrow.Style.EndHead = ArrowHead.Filled;
        arrow.Style.StrokeGradient = new ColorGradient([Palette.Coral, Palette.Blue], 0);
        Add(arrow);
        var line = new Element(Tool.Line, points: Points(new Point(460, 230), new Point(700, 330)));
        line.Style.Pattern = StrokePattern.Dashed;
        line.Style.Width = 3;
        line.Style.Color = Palette.Ink;
        Add(line);

        var pen = new Element(Tool.Pen, points: Wave(60, 420, 380, 40, 160));
        pen.Style.Width = 5;
        pen.Style.Color = Palette.Coral;
        Add(pen);
        var pressure = new Element(Tool.Pen, points: Wave(60, 500, 380, 30, 200));
        pressure.Style.Width = 10;
        pressure.Style.Pressure = true;
        pressure.Style.Color = Palette.Blue;
        pressure.Pressures = ValueList.Create<double>(Enumerable.Range(0, pressure.Points.Count).Select(index => 0.3 + 0.7 * Math.Abs(Math.Sin(index * 0.05))).ToArray());
        Add(pressure);
        var highlighter = new Element(Tool.Highlighter, points: Points(new Point(470, 420), new Point(800, 420)));
        highlighter.Style.Color = Palette.Yellow;
        highlighter.Style.Width = 6;
        Add(highlighter);

        var title = new Element(Tool.Text, new Rect(470, 395, 360, 50), text: "Sharp at every scale");
        title.Style.FontSize = 32;
        title.Style.Bold = true;
        title.Style.Color = Palette.Ink;
        Add(title);
        var code = new Element(Tool.Text, new Rect(470, 460, 360, 30), text: "let latency = 4.17 ms");
        code.Style.FontSize = 18;
        code.Style.FontName = "monospace";
        code.Style.Color = Palette.Ink;
        Add(code);
        var small = new Element(Tool.Text, new Rect(470, 500, 360, 24), text: "Small caption text, 12 points");
        small.Style.FontSize = 12;
        small.Style.Color = Palette.Ink;
        Add(small);

        Add(new Element(Tool.Counter, new Rect(880, 60, 56, 56), text: "3"));
        Add(new Element(Tool.Ruler, points: Points(new Point(880, 200), new Point(1060, 200)), text: "180 px"));
        Add(new Element(Tool.Cover, new Rect(880.4, 260.3, 120.5, 60.2)));
        Add(new Element(Tool.Image, new Rect(880, 360, 120, 120)) { ImagePng = ValueList.Create<byte>(imagePng) });
        var labeled = new Element(Tool.Rectangle, new Rect(1040, 360, 160, 90));
        labeled.Label = new ShapeLabel("Label");
        labeled.Style.Color = Palette.Ink;
        Add(labeled);
        var rotated = new Element(Tool.Rectangle, new Rect(1060, 60, 120, 70)) { Rotation = Math.PI / 9 };
        rotated.Style.Color = Palette.Coral;
        rotated.Style.Fill = new RGBA(0.94, 0.27, 0.22, 0.25);
        Add(rotated);
        return [.. list];
    }

    public static ValueList<Point> Points(params Point[] points) => ValueList.Create<Point>(points);

    /// <summary>An opaque white rectangle, drawn first so screenshots show only the harness's own content.</summary>
    public static Element Backdrop(Rect area)
    {
        var backdrop = new Element(Tool.Rectangle, area);
        backdrop.Style.Color = Palette.White;
        backdrop.Style.Fill = Palette.White;
        backdrop.Style.Roughness = 0;
        return backdrop;
    }

    public static ValueList<Point> Wave(double x, double y, double width, double height, int count)
    {
        var points = new Point[count];
        for (var index = 0; index < count; index++)
        {
            var t = (double)index / (count - 1);
            points[index] = new Point(x + t * width, y + Math.Sin(t * Math.PI * 4) * height / 2);
        }
        return ValueList.Create<Point>(points);
    }

    /// <summary>A display list of one opaque rectangle, for latency patches.</summary>
    public static SceneSnapshot Patch(Rect rect, RGBA color) =>
        Scene([NewList(Guid.NewGuid(), [DisplayItem.FillRect(rect, Paint.Solid(color))], SceneIssues.None)], DisplayList.Empty, SceneIssues.None);

    public static Guid Id(int index)
    {
        var n = (ulong)index;
        ReadOnlySpan<byte> bytes =
        [
            0xA6, 0x60, 0x20, 0x26, 0, 0, 0x40, 0, 0x80, 0,
            (byte)(n >> 40), (byte)(n >> 32), (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n,
        ];
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>The mixed elements of the 003 workloads (Swift <c>PerformanceBenchmarks</c> fixtures), as Core's tests build them.</summary>
    public static Element[] Mixed(int count, double scaleX = 1, double scaleY = 1)
    {
        Tool[] tools = [Tool.Rectangle, Tool.Ellipse, Tool.Diamond, Tool.Arrow, Tool.Pen, Tool.Highlighter, Tool.Text, Tool.Counter, Tool.Grid, Tool.Axes];
        var result = new Element[count];
        for (var index = 0; index < count; index++)
        {
            var tool = tools[index % tools.Length];
            double x = (index * 73 % 1_800 + 20) * scaleX, y = (index * 97 % 950 + 20) * scaleY;
            var style = new Style { Roughness = index % 3 * 2, Width = 2, FontSize = 14 };
            if (tool.SupportsFill && index % 4 == 0)
            {
                style.Fill = new RGBA(0.2, 0.4, 0.8, 0.2);
            }
            ValueList<Point> points = default;
            if (tool.IsFreehand || tool.IsConnector)
            {
                var array = new Point[24];
                for (var step = 0; step < array.Length; step++)
                {
                    array[step] = new Point(x + step * 3, y + Math.Sin(step * 0.2) * 25);
                }
                points = ValueList.Create<Point>(array);
            }
            result[index] = new Element(tool, new Rect(x, y, 75, 55), points, tool == Tool.Counter ? $"{index % 99}" : $"Owned {index}", style, Id(index))
            {
                Seed = unchecked(FixtureSeed + (ulong)index),
                GeometryToken = Id(index + 1_000_000),
                AppendEpoch = Id(index + 2_000_000),
            };
        }
        return result;
    }

    /// <summary>The long stroke of the 003 workloads: <paramref name="count"/> samples across the display.</summary>
    public static Element Stroke(int count, double scaleX = 1, double scaleY = 1)
    {
        var points = new Point[count];
        for (var index = 0; index < count; index++)
        {
            var t = (double)index / Math.Max(1, count - 1);
            points[index] = new Point((10 + t * 1_900) * scaleX, (540 + Math.Sin(t * Math.PI * 24) * 400) * scaleY);
        }
        return new Element(Tool.Pen, points: ValueList.Create<Point>(points), id: Id(900_000))
        {
            Seed = FixtureSeed,
            GeometryToken = Id(3_000_000),
            AppendEpoch = Id(4_000_000),
        };
    }

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern DisplayList NewList(Guid elementId, DisplayItem[] items, SceneIssues issues);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern SceneSnapshot Scene(DisplayList[] elements, DisplayList selection, SceneIssues issues);
}
