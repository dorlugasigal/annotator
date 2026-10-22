using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Text;
using Annotator.Workspace.Canvas;

namespace Annotator.Rendering.Tests;

/// <summary>
/// The text editor's layout questions answered by DirectWrite (spec 013 S-7): the caret, selection and pointer hits sit
/// on the glyphs the renderer draws, and the text being edited renders exactly like the committed text (WIN-INP-09).
/// </summary>
public sealed class TextEditingLayoutTests
{
    private static readonly TextFont Body = new(FontDesign.System, FontWeight.Regular, 28);

    /// <summary>A text object's layout: its first baseline one Mac ascender below the frame's top, as the renderer draws it.</summary>
    private static TextLayoutRequest Request(string text, TextFont? font = null, TextAlignment alignment = TextAlignment.Left, double? width = null)
    {
        TextFont resolved = font ?? Body;
        Size content = TextLayout.ContentSize(text, resolved, Warp.Measurer);
        return new TextLayoutRequest(text, resolved, alignment, new Size(width ?? content.Width, content.Height), MacTextMetrics.Ascender(resolved.Size));
    }

    /// <summary>How far the renderer moves a layout of <paramref name="font"/> to put its first baseline where the Mac's is.</summary>
    private static double Shift(TextFont font) => MacTextMetrics.Ascender(font.Size) - Warp.Measurer.LineMetrics(font).Ascent;

    [Fact]
    public void CaretsSitBetweenTheGlyphsTheRendererDraws()
    {
        const string text = "New note";
        TextLayoutRequest request = Request(text);
        DirectWriteTextMeasurer layout = Warp.Measurer;
        // Segoe UI Variable's ascent is larger than SF Pro's, so the line moves up onto the Mac's baseline.
        double shift = Shift(Body);
        Assert.True(shift < -2, $"The layout moves {shift} points.");
        Assert.Equal(0, layout.Caret(request, 0).X, 3);
        Assert.Equal(layout.Measure(text, Body).Width, layout.Caret(request, text.Length).X, 2);
        for (int index = 0; index < text.Length; index++)
        {
            TextCaret caret = layout.Caret(request, index);
            TextCaret next = layout.Caret(request, index + 1);
            Assert.True(next.X > caret.X, $"Caret {index + 1} at {next.X} is not after caret {index} at {caret.X}.");
            Assert.Equal(shift, caret.Top, 3);
            Assert.Equal(shift + layout.LineMetrics(Body).Ascent + layout.LineMetrics(Body).Descent, caret.Bottom, 3);
            Assert.Equal(index, layout.HitTest(request, new Point(caret.X + 0.4, 18)));
            Assert.Equal(index + 1, layout.HitTest(request, new Point(next.X - 0.4, 18)));
        }

        // The renderer draws no ink between the carets around the space: the carets and glyphs agree.
        const double scale = 2;
        var element = new Element(Tool.Text, new Rect(0, 0, request.Frame.Width, request.Frame.Height), text: text,
                                  style: new Style { Color = new RGBA(0, 0, 0), FontSize = 28 });
        using var exporter = Warp.Exporter();
        RasterImage image = exporter.Image(ElementList.Create([element]), request.Frame, scale);
        int from = (int)Math.Ceiling(layout.Caret(request, 3).X * scale) + 2;
        int to = (int)Math.Floor(layout.Caret(request, 4).X * scale) - 2;
        Assert.True(to > from + 4, $"The space is only {to - from} pixels wide.");
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = from; x < to; x++)
            {
                Assert.Equal(0, image[x, y].Alpha);
            }
        }
        Assert.True(Columns(image, layout.Caret(request, 2).X * scale, layout.Caret(request, 3).X * scale) > 0, "No ink for the \"w\" before the space.");
        Assert.True(Columns(image, layout.Caret(request, 4).X * scale, layout.Caret(request, 5).X * scale) > 0, "No ink for the \"n\" after the space.");
    }

    [Fact]
    public void LinesAndTheEmptyLineAfterABreak()
    {
        const string text = "ab\ncd\n";
        TextLayoutRequest request = Request(text);
        DirectWriteTextMeasurer layout = Warp.Measurer;
        FontLineMetrics metrics = layout.LineMetrics(Body);
        double line = metrics.Ascent + metrics.Descent + metrics.Leading;
        double top = Shift(Body);
        Assert.Equal(Round(new TextLine(0, 2, top, top + line)), Round(layout.Line(request, 1)));
        Assert.Equal(Round(new TextLine(3, 5, top + line, top + line * 2)), Round(layout.Line(request, 3)));
        Assert.Equal(Round(new TextLine(6, 6, top + line * 2, top + line * 3)), Round(layout.Line(request, 6)));
        TextCaret end = layout.Caret(request, 6);
        Assert.Equal(0, end.X);
        Assert.Equal(top + line * 2, end.Top, 3);
        // A click right of a line stays before its line break; one below the text goes to the empty last line.
        Assert.Equal(2, layout.HitTest(request, new Point(300, top + line / 2)));
        Assert.Equal(5, layout.HitTest(request, new Point(300, top + line * 1.5)));
        Assert.Equal(6, layout.HitTest(request, new Point(10, top + line * 2.5)));
        Assert.Equal(6, layout.HitTest(request, new Point(10, line * 9)));
        // A click above the first line stays on it.
        Assert.Equal(2, layout.HitTest(request, new Point(300, top - 5)));
    }

    [Fact]
    public void RangesCoverTheSelectedGlyphsAndShowSelectedBreaks()
    {
        DirectWriteTextMeasurer layout = Warp.Measurer;
        TextLayoutRequest request = Request("Hello\nworld");
        var rectangles = new List<Rect>();
        layout.Range(request, 1, 4, rectangles);
        Rect only = Assert.Single(rectangles);
        Assert.Equal(layout.Caret(request, 1).X, only.X, 3);
        Assert.Equal(layout.Caret(request, 4).X, only.MaxX, 3);
        rectangles.Clear();
        layout.Range(request, 3, 8, rectangles);
        Assert.Equal(3, rectangles.Count);
        Assert.Contains(rectangles, rect => Math.Abs(rect.X - layout.Caret(request, 5).X) < 0.01 && rect.Width == 28 / 4.0);
        Assert.Contains(rectangles, rect => Math.Abs(rect.MaxX - layout.Caret(request, 8).X) < 0.01 && rect.Y > 0);
        // The selection covers the lines where the caret is.
        Assert.All(rectangles, rect => Assert.True(Math.Abs(rect.Y - layout.Caret(request, 3).Top) < 0.01
                                                   || Math.Abs(rect.Y - layout.Caret(request, 8).Top) < 0.01, $"{rect}"));
    }

    [Fact]
    public void CenteredAndRightAlignedTextPutTheCaretWhereTheTextIs()
    {
        DirectWriteTextMeasurer layout = Warp.Measurer;
        double width = layout.Measure("Hi", Body).Width;
        Assert.Equal((100 - width) / 2, layout.Caret(Request("Hi", alignment: TextAlignment.Center, width: 100), 0).X, 2);
        Assert.Equal(100 - width, layout.Caret(Request("Hi", alignment: TextAlignment.Right, width: 100), 0).X, 2);
        Assert.Equal(50, layout.Caret(Request(string.Empty, alignment: TextAlignment.Center, width: 100), 0).X);
        Assert.Equal(100, layout.Caret(Request(string.Empty, alignment: TextAlignment.Right, width: 100), 0).X);
    }

    [Fact]
    public void RightToLeftTextStartsOnTheRight()
    {
        const string text = "\u05e9\u05dc\u05d5\u05dd";
        DirectWriteTextMeasurer layout = Warp.Measurer;
        TextLayoutRequest request = Request(text);
        for (int index = 0; index < text.Length; index++)
        {
            Assert.True(layout.Caret(request, index).X > layout.Caret(request, index + 1).X);
            Assert.Equal(index, layout.HitTest(request, new Point(layout.Caret(request, index).X - 0.4, 18)));
        }
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(2)]
    public void TextBeingEditedRendersExactlyLikeCommittedText(double scale)
    {
        // WIN-INP-09: "the committed text within 0.5 points of the edited text". Both come from one display-list path on
        // one baseline, so the pixels are equal, also for rotated, gradient-painted labels that follow their shape's stroke.
        var gradient = new ColorGradient([new RGBA(1, 0, 0), new RGBA(0, 0, 1)], 25);
        var style = new Style { Color = new RGBA(0.2, 0.4, 0.9), FontSize = 40, Bold = true, StrokeGradient = gradient };
        SceneBuilder builder = Warp.Builder();
        Size text = TextLayout.ContentSize("Edited text", TextFont.Resolve(style), Warp.Measurer);
        var element = new Element(Tool.Text, new Rect(30, 40, text.Width, text.Height), text: "Edited text", style: style) { Rotation = 0.3 };
        AssertSamePixels(builder.Build(ElementList.Create([element])), SceneSnapshot.Create([Edited(builder, TextSession(element))]), scale);

        var shapeStyle = Style.Defaults(Tool.Rectangle);
        shapeStyle.StrokeGradient = gradient;
        // A new label follows the stroke in the Text tool's style; Match outline makes a label in its own color follow it again.
        Style[] labelStyles =
        [
            new Style { FontSize = 30, Alignment = TextAlignment.Center },
            new Style { FontSize = 30, Alignment = TextAlignment.Center, Color = new RGBA(0.2, 0.4, 0.9), Bold = true },
        ];
        foreach (Style labelStyle in labelStyles)
        {
            foreach (string label in (string[])["Orders", "Orders\nqueue"])
            {
                var owner = new Element(Tool.Rectangle, new Rect(60, 60, 300, 160), style: shapeStyle)
                {
                    Rotation = -0.4,
                    Label = new ShapeLabel(label, labelStyle, followsStroke: true),
                };
                var editing = Warp.Builder();
                SceneSnapshot hidden = editing.Build(ElementList.Create([owner]), hiddenLabelIds: new HashSet<Guid> { owner.Id });
                AssertSamePixels(Warp.Builder().Build(ElementList.Create([owner])),
                                 SceneSnapshot.Create([hidden.Elements[0], Edited(editing, LabelSession(owner))]), scale);
            }
        }
    }

    /// <summary>
    /// S-7's 0.5-point rule measured again after the text baseline decision, on the cases of the S-7 TextBox probe: the edited
    /// text's ink against the committed text's (both at the owner's 125%), and the caret's line against the drawn glyphs: the
    /// bottom of the first "H" (at 4x) must sit on the baseline the caret's line has (its top plus the font's ascent).
    /// </summary>
    [Fact]
    public void TheEditedTextAndItsCaretStayOnTheCommittedTextInTheProbeCases()
    {
        var gradient = new ColorGradient([new RGBA(1, 0, 0), new RGBA(0, 0, 1)], 25);
        (string Name, string Text, Style Style)[] texts =
        [
            ("12 pt", "Hello 12", new Style { FontSize = 12 }),
            ("16 pt", "Hello 16", new Style { FontSize = 16 }),
            ("28 pt", "Hello 28", new Style { FontSize = 28 }),
            ("48 pt bold", "Hello 48", new Style { FontSize = 48, Bold = true }),
            ("18 pt monospace", "Hello 18", new Style { FontSize = 18, FontName = "monospace" }),
            ("two lines, 28 pt", "Hello\nSecond line", new Style { FontSize = 28 }),
            ("Hebrew, 28 pt", "\u05e9\u05dc\u05d5\u05dd \u05e2\u05d5\u05dc\u05dd", new Style { FontSize = 28 }),
            ("gradient, 48 pt", "Hello 48", new Style { FontSize = 48, StrokeGradient = gradient }),
        ];
        var report = new List<string>();
        foreach ((string name, string content, Style style) in texts)
        {
            Size size = TextLayout.ContentSize(content, TextFont.Resolve(style), Warp.Measurer);
            var element = new Element(Tool.Text, new Rect(30, 40, size.Width, size.Height), text: content, style: style);
            SceneBuilder builder = Warp.Builder();
            report.Add(Measure(name, builder.Build(ElementList.Create([element])), SceneSnapshot.Create([Edited(builder, TextSession(element))]),
                               TextSession(element), content.StartsWith('H')));
        }
        foreach ((string name, string content) in new[] { ("label, 30 pt", "Hello"), ("two-line label, 30 pt", "Hello\nqueue") })
        {
            var owner = new Element(Tool.Rectangle, new Rect(60, 60, 300, 160))
            {
                Label = new ShapeLabel(content, new Style { FontSize = 30, Alignment = TextAlignment.Center }, followsStroke: true),
            };
            var editing = Warp.Builder();
            SceneSnapshot hidden = editing.Build(ElementList.Create([owner]), hiddenLabelIds: new HashSet<Guid> { owner.Id });
            report.Add(Measure(name, Warp.Builder().Build(ElementList.Create([owner])),
                               SceneSnapshot.Create([hidden.Elements[0], Edited(editing, LabelSession(owner))]), LabelSession(owner), true));
        }
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, report));
    }

    private static string Measure(string name, SceneSnapshot committed, SceneSnapshot edited, CanvasTextSession session, bool baseline)
    {
        using var exporter = Warp.Exporter();
        var canvas = new Size(420, 280);
        RasterImage before = exporter.Render(committed, canvas, 1.25, includeSelection: false);
        RasterImage after = exporter.Render(edited, canvas, 1.25, includeSelection: false);
        (double X, double Y) a = InkCenter(before), b = InkCenter(after);
        double dx = (b.X - a.X) / 1.25, dy = (b.Y - a.Y) / 1.25;
        Assert.True(Math.Abs(dx) <= 0.5 && Math.Abs(dy) <= 0.5, $"{name}: the edited text is {dx:F2}, {dy:F2} points from the committed text.");
        string line = FormattableString.Invariant($"{name}: edited minus committed {dx:F2}, {dy:F2} pt");
        if (!baseline)
        {
            return line;
        }
        // The caret's line puts its baseline one ascent of the font below its top, where DirectWrite puts the glyphs'.
        TextLayoutRequest request = Request(session);
        TextCaret first = Warp.Measurer.Caret(request, 0), next = Warp.Measurer.Caret(request, 1);
        double caretBaseline = session.Frame.Y + first.Top + Warp.Measurer.LineMetrics(request.Font).Ascent;
        const double scale = 4;
        RasterImage image = exporter.Render(committed, canvas, scale, includeSelection: false);
        double ink = InkBottom(image, (session.Frame.X + first.X) * scale, (session.Frame.X + next.X) * scale,
                               (int)Math.Floor((session.Frame.Y + first.Bottom) * scale)) / scale;
        double drawn = ink - caretBaseline;
        Assert.True(Math.Abs(drawn) <= 0.5, $"{name}: the drawn baseline is {drawn:F2} points from the caret's.");
        return line + FormattableString.Invariant($"; drawn baseline minus caret baseline {drawn:F2} pt");
    }

    /// <summary>The ink's alpha-weighted center, in pixels.</summary>
    private static (double X, double Y) InkCenter(RasterImage image)
    {
        double sum = 0, x = 0, y = 0;
        for (int row = 0; row < image.Height; row++)
        {
            for (int column = 0; column < image.Width; column++)
            {
                double alpha = image[column, row].Alpha;
                sum += alpha;
                x += alpha * (column + 0.5);
                y += alpha * (row + 0.5);
            }
        }
        Assert.True(sum > 0, "No ink.");
        return (x / sum, y / sum);
    }

    /// <summary>
    /// The bottom edge of a flat-bottomed glyph between columns <paramref name="from"/> and <paramref name="to"/> on a line
    /// that ends at row <paramref name="lineBottom"/>, in pixels: the last row with ink plus its coverage of the row above it.
    /// </summary>
    private static double InkBottom(RasterImage image, double from, double to, int lineBottom)
    {
        int left = (int)Math.Ceiling(from), right = (int)Math.Floor(to);
        for (int row = Math.Min(image.Height - 1, lineBottom); row > 0; row--)
        {
            double sum = 0, above = 0;
            for (int column = left; column < right; column++)
            {
                sum += image[column, row].Alpha;
                above += image[column, row - 1].Alpha;
            }
            if (sum > 0)
            {
                return above > 0 ? row + Math.Min(1, sum / above) : row + 1;
            }
        }
        throw new InvalidOperationException("No ink in the glyph's columns.");
    }

    /// <summary>The editor's session for a text object, as <c>CanvasController.RefreshTextSession</c> makes it.</summary>
    private static CanvasTextSession TextSession(in Element text) =>
        new(text.Id, false, text.Text, text.Frame, text.Rotation, text.Style, text.Frame, "Annotation text.");

    /// <summary>The editor's session for a shape's label, as <c>CanvasController.RefreshTextSession</c> makes it.</summary>
    private static CanvasTextSession LabelSession(in Element owner)
    {
        Element label = ShapeLabelLayout.TextElement(owner, Warp.Measurer)!.Value;
        return new(owner.Id, true, label.Text, label.Frame, owner.Rotation, label.Style,
                   owner.Label!.FollowsStroke == true ? owner.Frame : label.Frame, "Shape label.");
    }

    /// <summary>The live text as <c>CanvasScene</c> draws it while <paramref name="session"/> is edited.</summary>
    private static DisplayList Edited(SceneBuilder builder, CanvasTextSession session)
    {
        var text = new Element(Tool.Text, session.Frame, text: session.Text, style: session.Style, id: session.ElementId) { Rotation = session.Rotation };
        return builder.EditedText(text, session.PaintFrame, session.FirstBaseline(session.Text));
    }

    /// <summary>The editor's layout while <paramref name="session"/> is edited, as <c>CanvasTextEditor.Layout</c> asks for it.</summary>
    private static TextLayoutRequest Request(CanvasTextSession session) =>
        new(session.Text, TextFont.Resolve(session.Style), session.Style.Alignment, new Size(session.Frame.Width, session.Frame.Height),
            session.FirstBaseline(session.Text) - session.Frame.Y);

    private static void AssertSamePixels(SceneSnapshot committed, SceneSnapshot edited, double scale)
    {
        using var exporter = Warp.Exporter();
        RasterImage expected = exporter.Render(committed, new Size(420, 280), scale, includeSelection: false);
        RasterImage actual = exporter.Render(edited, new Size(420, 280), scale, includeSelection: false);
        Assert.True(Pixels.Painted(expected) > 1000, "The committed text drew nothing.");
        Assert.True(Pixels.SamePixels(expected, actual), "The edited text's pixels differ from the committed text's.");
    }

    private static int Columns(RasterImage image, double from, double to)
    {
        int ink = 0;
        for (int x = (int)Math.Floor(from); x < (int)Math.Ceiling(to); x++)
        {
            for (int y = 0; y < image.Height; y++)
            {
                ink += image[x, y].Alpha;
            }
        }
        return ink;
    }

    private static TextLine Round(TextLine line) =>
        line with { Top = Math.Round(line.Top, 3), Bottom = Math.Round(line.Bottom, 3) };
}
