using System.Text;
using System.Text.Json.Nodes;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.Conformance.Tests;

/// <summary>
/// Section <c>geometry</c>: renderer paths for every shape and stroke, arrowheads, polygons and rounded diamonds,
/// connector curves, anchors, drag constraints, viewports and sharing regions, transforms, axis ticks and pointer
/// shapes. Same path element kinds and counts; numbers within 1e-6 (WIN-CORE-04). <c>geometry/text.jsonl</c> holds
/// macOS font measurements, so only the layout rules are compared there.
/// </summary>
public class GeometryTests
{
    private static readonly NumberRule Numbers = NumberRule.Within(1e-6);

    public static TheoryData<string> ShapeCases => CorpusCases.Of("geometry/shapes.jsonl", record => $"{record["element"]!["tool"]}: {record["variant"]}");

    public static TheoryData<string> StrokeCases => CorpusCases.Of("geometry/strokes.jsonl", record => record["variant"]!.GetValue<string>());

    public static TheoryData<string> ArrowheadCases => CorpusCases.Of("geometry/arrowheads.jsonl", record => $"{record["kind"]}, {record["config"]}");

    public static TheoryData<string> PolygonCases => CorpusCases.Of("geometry/polygons.jsonl", record =>
        (record["polygon"] is { } polygon ? polygon.GetValue<string>() : $"rounded diamond {record["roundedDiamond"]!.ToJsonString()}")
        + " in " + record["frame"]!.ToJsonString());

    public static TheoryData<string> ConnectorCases => CorpusCases.Of("geometry/connectors.jsonl", record => record["points"]!.ToJsonString());

    public static TheoryData<string> AnchorCases => CorpusCases.Of("geometry/anchors.jsonl", record => $"{record["tool"]}, rotation {record["rotation"]}");

    public static TheoryData<string> SnappingCases => CorpusCases.Of("geometry/snapping.jsonl", record =>
        $"{record["constraint"]}{(record["tool"] is { } tool ? " " + tool : "")} to {record["point"]!.ToJsonString()}");

    public static TheoryData<string> ViewportCases => CorpusCases.Of("geometry/viewports.jsonl", record =>
        $"{record["call"]} {record["preset"] ?? record["factor"]?.ToJsonString() ?? record["bounds"]!.ToJsonString()}");

    public static TheoryData<string> TransformCases => CorpusCases.Of("geometry/transforms.jsonl", record =>
        $"{record["call"]} {record["case"] ?? record["tool"] ?? record["to"]?.ToJsonString() ?? record["by"]?.ToJsonString()}");

    public static TheoryData<string> AxisTickCases => CorpusCases.Of("geometry/axis-ticks.jsonl", record =>
        $"{record["axisTickCount"]?.ToJsonString() ?? "null"} ticks, negative {record["negativeAxes"]?.ToJsonString() ?? "null"}");

    public static TheoryData<string> PointerShapeCases => CorpusCases.Of("geometry/pointer-shapes.jsonl", record => $"{record["shape"]} in {record["frame"]!.ToJsonString()}");

    public static TheoryData<string> TextCases => CorpusCases.Of("geometry/text.jsonl", record => record["text"] is { } text
        ? $"\"{text.GetValue<string>().Replace("\n", "\\n", StringComparison.Ordinal)}\" {record["fontSize"]} {record["fontName"]}{(record["bold"]!.GetValue<bool>() ? " bold" : "")}"
        : $"label \"{record["owner"]!["label"]!["text"]!.GetValue<string>().Replace("\n", "\\n", StringComparison.Ordinal)}\"");

    [Theory]
    [MemberData(nameof(ShapeCases))]
    public void ShapePathMatchesTheMac(string caseName) => RendererPath(caseName);

    [Theory]
    [MemberData(nameof(StrokeCases))]
    public void StrokePathMatchesTheMac(string caseName) => RendererPath(caseName);

    /// <summary>The exporter makes one record per shape tool, variant and roughness, in <c>Tool.allCases</c> order.</summary>
    [Fact]
    public void ShapeCasesCoverEveryShape()
    {
        var expected = Corpus.Lines("geometry/shapes.jsonl").Select(record => $"{record["element"]!["tool"]}: {record["variant"]}").ToList();
        var actual = new List<string>();
        foreach (var tool in Tool.AllCases)
        {
            if (!tool.IsShape)
            {
                continue;
            }
            List<string> variants = ["plain"];
            if (tool is Tool.Rectangle or Tool.Diamond)
            {
                variants.AddRange(["corner radius 12", "corner radius 500"]);
            }
            if (tool == Tool.Grid)
            {
                variants.Add("3 rows and 5 columns");
            }
            if (tool == Tool.Axes)
            {
                variants.AddRange(["4 ticks", "no ticks", "negative axes with 3 ticks"]);
            }
            string[] roughness = Tool.SystemDesignShapes.Contains(tool) ? ["0.0", "2.5"] : ["0.0", "1.0", "2.5", "6.0"];
            actual.AddRange(variants.SelectMany(variant => roughness.Select(value => $"{tool.RawValue}: {variant}, roughness {value}")));
        }
        actual.Add("ellipse: thin ellipse, roughness 12");
        actual.Add("rectangle: empty rectangle, roughness 3");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(ArrowheadCases))]
    public void ArrowheadMatchesTheMac(string caseName)
    {
        var record = CorpusCases.Record(caseName);
        var element = ElementOf(record, "element");
        Same(caseName, record, new JsonObject
        {
            ["kind"] = element.Style.EndHead.RawValue,
            ["label"] = element.Style.EndHead.Label,
            ["start"] = Head(ArrowHeadGeometry.Make(element, atStart: true)),
            ["end"] = Head(ArrowHeadGeometry.Make(element, atStart: false)),
        });
    }

    [Fact]
    public void ArrowheadChoicesMatchTheMac()
    {
        JsonDiff.Equal(Corpus.Json("geometry/arrowhead-choices.json"), new JsonObject
        {
            ["relationshipChoices"] = new JsonArray(ArrowHead.RelationshipChoices.Select(head => (JsonNode)head.RawValue).ToArray()),
            ["shapeChoices"] = new JsonArray(ArrowHead.ShapeChoices.Select(head => (JsonNode)head.RawValue).ToArray()),
        }, NumberRule.Exact);
    }

    [Theory]
    [MemberData(nameof(PolygonCases))]
    public void PolygonMatchesTheMac(string caseName)
    {
        var record = CorpusCases.Record(caseName);
        var frame = Shapes.Rect(record["frame"]);
        if (record["polygon"] is { } polygon)
        {
            var kind = polygon.GetValue<string>() switch
            {
                "triangle" => PolygonShapeKind.Triangle,
                "hexagon" => PolygonShapeKind.Hexagon,
                "star" => PolygonShapeKind.Star,
                var other => throw new InvalidDataException($"Unknown polygon {other}."),
            };
            Same(caseName, record, new JsonObject { ["vertices"] = Points(PolygonShape.Vertices(kind, frame)) });
            return;
        }
        var radius = record["roundedDiamond"]!.GetValue<double>();
        Same(caseName, record, new JsonObject
        {
            ["corners"] = new JsonArray(RoundedDiamond.Corners(frame, radius).Select(corner => (JsonNode)new JsonObject
            {
                ["control"] = Shapes.Json(corner.Control),
                ["entry"] = Shapes.Json(corner.Entry),
                ["exit"] = Shapes.Json(corner.Exit),
            }).ToArray()),
            ["path"] = Shapes.Elements(DiamondGeometry.Path(frame, radius)),
        });
    }

    [Theory]
    [MemberData(nameof(ConnectorCases))]
    public void ConnectorMatchesTheMac(string caseName)
    {
        var record = CorpusCases.Record(caseName);
        var points = ValueList.Create<Point>(record["points"]!.AsArray().Select(Shapes.Point).ToArray());
        Same(caseName, record, new JsonObject
        {
            ["curves"] = new JsonArray(ConnectorGeometry.Curves(points).Select(curve => (JsonNode)Curve(curve)).ToArray()),
            ["startCurve"] = ConnectorGeometry.EndpointCurve(points, atStart: true) is { } start ? Curve(start) : null,
            ["endCurve"] = ConnectorGeometry.EndpointCurve(points, atStart: false) is { } end ? Curve(end) : null,
        });
    }

    [Theory]
    [MemberData(nameof(AnchorCases))]
    public void AnchorsMatchTheMac(string caseName)
    {
        var record = CorpusCases.Record(caseName);
        var element = Fixture.Element(4_000, ToolOf(record["tool"]), Shapes.Rect(record["frame"]));
        element.Rotation = record["rotation"]!.GetValue<double>();
        Same(caseName, record, new JsonObject
        {
            ["symbolFrame"] = Shapes.Json(element.SymbolFrame),
            ["anchorLocations"] = Points(element.AnchorLocations),
            ["anchorPoints"] = Points(element.AnchorLocations.Select(element.AnchorPoint)),
        });
    }

    [Theory]
    [MemberData(nameof(SnappingCases))]
    public void DragConstraintMatchesTheMac(string caseName)
    {
        var record = CorpusCases.Record(caseName);
        var start = Shapes.Point(record["start"]);
        var point = Shapes.Point(record["point"]);
        var fields = new JsonObject();
        switch (record["constraint"]!.GetValue<string>())
        {
            case "snappedLineEnd":
                fields["result"] = Shapes.Json(DragConstraint.SnappedLineEnd(start, point));
                break;
            case "squareEnd":
                fields["result"] = Shapes.Json(DragConstraint.SquareEnd(start, point));
                break;
            case "aspectEnd":
                var ratio = ToolOf(record["tool"]).SystemShapeAspectRatio ?? throw new InvalidDataException("The tool has no aspect ratio.");
                fields["ratio"] = ratio;
                fields["result"] = Shapes.Json(DragConstraint.AspectEnd(start, point, ratio));
                break;
            default:
                throw new InvalidDataException($"Unknown constraint {record["constraint"]}.");
        }
        Same(caseName, record, fields);
    }

    [Theory]
    [MemberData(nameof(ViewportCases))]
    public void ViewportMatchesTheMac(string caseName)
    {
        var record = CorpusCases.Record(caseName);
        var display = Shapes.Rect(record["display"]);
        JsonObject result;
        switch (record["call"]!.GetValue<string>())
        {
            case "zoom":
                result = Outcome.Of(() => Viewport(Core.Viewport.Zoom(display, NumberOf(record["factor"]), Shapes.Point(record["focus"]))));
                break;
            case "cursorAnchored":
                result = Outcome.Of(() => Viewport(Core.Viewport.CursorAnchored(display, NumberOf(record["factor"]), Shapes.Point(record["cursor"]))));
                break;
            case "sharingRegion" when record["preset"] is { } presetName:
                var preset = ShareRegionPreset.FromRawValue(presetName.GetValue<string>()) ?? throw new InvalidDataException("Unknown preset.");
                Assert.Equal(record["title"]!.GetValue<string>(), preset.Title);
                var zoomed = Core.Viewport.Zoom(new Rect(0, 0, 1512, 982), 2, new Point(400, 300));
                result = Outcome.Of(() =>
                {
                    var region = new SharingRegion(preset, display);
                    return new JsonObject
                    {
                        ["aspectFitIn1280x720"] = Shapes.Json(region.AspectFit(new Rect(0, 0, 1280, 720))),
                        ["bounds"] = Shapes.Json(region.Bounds),
                        ["sourceBoundsAtZoom2"] = Shapes.Json(region.SourceBounds(zoomed)),
                    };
                });
                break;
            case "sharingRegion":
                result = Outcome.Of(() => Shapes.Json(new SharingRegion(Shapes.Rect(record["bounds"]), display).Bounds));
                break;
            default:
                throw new InvalidDataException($"Unknown call {record["call"]}.");
        }
        Same(caseName, record, new JsonObject { ["result"] = result });
    }

    [Theory]
    [MemberData(nameof(TransformCases))]
    public void TransformMatchesTheMac(string caseName)
    {
        var record = CorpusCases.Record(caseName);
        var fields = new JsonObject();
        switch (record["call"]!.GetValue<string>())
        {
            case "resize":
            {
                var element = ElementOf(record, "element");
                element.Resize(Shapes.Rect(record["to"]));
                fields["frame"] = Shapes.Json(element.Frame);
                fields["points"] = Points(element.Points);
                fields["labelFontSize"] = element.Label is { } label ? label.Style.FontSize : null;
                fields["revision"] = element.Revision;
                break;
            }
            case "translate":
            {
                var element = ElementOf(record, "element");
                element.Translate(Shapes.Point(record["by"]));
                fields["frame"] = Shapes.Json(element.Frame);
                fields["points"] = Points(element.Points);
                break;
            }
            case "symbolFrame":
                fields["result"] = Shapes.Json(Fixture.Element(5_100, ToolOf(record["tool"]), Shapes.Rect(record["frame"])).SymbolFrame);
                break;
            case "selectionGeometry":
            {
                var elements = ElementList.CreateRange(record["elements"]!.AsArray().Select(node => AnnotatorJson.Decode<Element>(Bytes(node))));
                var selection = SelectionGeometry.Create(elements, elements.Select(element => element.Id).ToHashSet());
                fields["result"] = selection is { } value
                    ? new JsonObject { ["bounds"] = Shapes.Json(value.Bounds), ["canRotate"] = value.CanRotate, ["corners"] = Points(value.Corners) }
                    : null;
                break;
            }
            default:
                throw new InvalidDataException($"Unknown call {record["call"]}.");
        }
        Same(caseName, record, fields);
    }

    [Theory]
    [MemberData(nameof(AxisTickCases))]
    public void AxisTicksMatchTheMac(string caseName)
    {
        var record = CorpusCases.Record(caseName);
        var style = Style.Defaults(Tool.Axes);
        style.AxisTickCount = record["axisTickCount"]?.GetValue<long>();
        style.NegativeAxes = record["negativeAxes"]?.GetValue<bool>();
        var positions = AxisTickGeometry.Positions(Shapes.Rect(record["frame"]), style);
        Same(caseName, record, new JsonObject
        {
            ["horizontal"] = new JsonArray(positions.Horizontal.Select(value => (JsonNode)value).ToArray()),
            ["vertical"] = new JsonArray(positions.Vertical.Select(value => (JsonNode)value).ToArray()),
        });
    }

    [Theory]
    [MemberData(nameof(PointerShapeCases))]
    public void PointerShapeMatchesTheMac(string caseName)
    {
        var record = CorpusCases.Record(caseName);
        var shape = CursorHighlightShape.FromRawValue(record["shape"]!.GetValue<string>()) ?? throw new InvalidDataException("Unknown shape.");
        Same(caseName, record, new JsonObject
        {
            ["label"] = shape.Label,
            ["path"] = Shapes.PathSummary(CursorHighlightGeometry.Path(shape, Shapes.Rect(record["frame"]))),
        });
    }

    [Fact]
    public void PointerRulesMatchTheMac()
    {
        var visibility = new JsonArray();
        foreach (var mode in CursorHighlightVisibility.AllCases)
        {
            var settings = new CursorHighlightSettings { Visibility = mode };
            foreach (var (now, movement, click) in new (double, double?, double?)[]
                     {
                         (10, null, null), (10, 9.3, null), (10, 9.25, null), (10, null, 9.6), (10, null, 9.55), (10, 10.5, null), (10, 9.9, 9.0),
                     })
            {
                visibility.Add(new JsonObject
                {
                    ["lastClick"] = click,
                    ["lastMovement"] = movement,
                    ["now"] = now,
                    ["visibility"] = mode.RawValue,
                    ["visible"] = settings.IsVisible(now, movement, click),
                });
            }
        }
        var effects = new JsonArray();
        foreach (var show in new[] { true, false })
        {
            foreach (var effect in new CursorClickEffect?[] { null }.Concat(CursorClickEffect.AllCases.Select(value => (CursorClickEffect?)value)))
            {
                var settings = new CursorHighlightSettings { ShowClicks = show, ClickEffect = effect };
                effects.Add(new JsonObject
                {
                    ["clickEffect"] = effect?.RawValue,
                    ["resolved"] = settings.ResolvedClickEffect.RawValue,
                    ["showClicks"] = show,
                });
            }
        }
        var feather = new JsonArray();
        foreach (var radius in new[] { 50.0, 100, 130, 200, 256, 350 })
        {
            var paint = SpotlightPaint.Create(new Rect(0, 0, 1000, 1000), new Point(500, 500), radius)!.Value;
            feather.Add(new JsonObject { ["feather"] = paint.OuterRadius - radius, ["radius"] = radius });
        }
        JsonDiff.Equal(Corpus.Json("geometry/pointer-rules.json"), new JsonObject
        {
            ["clickDuration"] = CursorHighlightSettings.ClickDuration,
            ["clickEffects"] = effects,
            ["defaults"] = Outcome.Value(new CursorHighlightSettings()),
            ["spotlightFeather"] = feather,
            ["visibility"] = visibility,
        }, NumberRule.Exact);
    }

    /// <summary>
    /// <c>geometry/text.jsonl</c> records TextKit's measurements with macOS fonts, so the Windows measurer is not
    /// compared. These checks feed the Mac's own line metrics and widths through the Windows rules: the font choice
    /// (semibold for bold, the monospaced design for "monospace"), the content size (measured size rounded up plus 2,
    /// at least 32 wide and one line high, empty text measured as a space, a trailing line feed adding a line), the
    /// label frame centered on its owner and the owner grown to fit its label. TextKit's height for n lines is
    /// n × (round(ascender) + round(−descender) + round(leading)), counting the empty line after a trailing line feed;
    /// the model below uses that, which reproduces every recorded height.
    /// </summary>
    [Theory]
    [MemberData(nameof(TextCases))]
    public void TextLayoutRulesMatchTheMac(string caseName)
    {
        var record = CorpusCases.Record(caseName);
        if (record["text"] is { } textNode)
        {
            var style = new Style
            {
                FontSize = record["fontSize"]!.GetValue<double>(),
                Bold = record["bold"]!.GetValue<bool>(),
                FontName = record["fontName"]!.GetValue<string>(),
            };
            var font = TextFont.Resolve(style);
            var postScriptName = record["fontPostScriptName"]!.GetValue<string>();
            Assert.Equal(postScriptName.Contains("Monospaced", StringComparison.Ordinal), font.Design == FontDesign.Monospace);
            Assert.Equal(postScriptName.Contains("Demi", StringComparison.Ordinal), font.Weight == FontWeight.Semibold);
            Assert.Equal(style.FontSize, font.Size);
            var size = TextLayout.ContentSize(textNode.GetValue<string>(), font, MacTextModel.For(record, Shapes.Size(record["contentSize"])));
            Same(caseName, record, new JsonObject { ["contentSize"] = Shapes.Json(size) });
            return;
        }
        var owner = ElementOf(record, "owner");
        var labelFrame = Shapes.Rect(record["labelFrame"]);
        var measurer = MacTextModel.For(MacTextModel.SystemFont28, new Size(labelFrame.Width, labelFrame.Height));
        Same(caseName, record, new JsonObject
        {
            ["labelFrame"] = ShapeLabelLayout.TextElement(owner, measurer) is { } text ? Shapes.Json(text.Frame) : null,
            ["fittedOwnerFrame"] = Shapes.Json(ShapeLabelLayout.FittedOwnerFrame(owner, owner.Label!, measurer)),
        });
    }

    /// <summary>
    /// Where Windows puts the first baseline (<see cref="MacTextMetrics"/>): the recorded fonts' ascender, descender and
    /// leading in ems, the Mac's content height for every recorded text, and for a label the baseline of the recorded
    /// label frame, whatever the height of the frame Windows centers on the shape.
    /// </summary>
    [Theory]
    [MemberData(nameof(TextCases))]
    public void MacTextMetricsMatchTheRecordedFonts(string caseName)
    {
        var record = CorpusCases.Record(caseName);
        if (record["text"] is { } textNode)
        {
            var size = record["fontSize"]!.GetValue<double>();
            Assert.Equal(record["ascender"]!.GetValue<double>(), MacTextMetrics.Ascender(size), 12);
            Assert.Equal(-record["descender"]!.GetValue<double>(), MacTextMetrics.Descender(size), 12);
            Assert.Equal(0, record["leading"]!.GetValue<double>());
            Assert.Equal(Shapes.Size(record["contentSize"]).Height, MacTextMetrics.ContentHeight(textNode.GetValue<string>(), size));
            return;
        }
        var label = ElementOf(record, "owner").Label!;
        var labelFrame = Shapes.Rect(record["labelFrame"]);
        var expected = labelFrame.Y + MacTextMetrics.Ascender(label.Style.FontSize);
        foreach (var height in new[] { labelFrame.Height, labelFrame.Height + 5, labelFrame.Height + 9.5 })
        {
            var windowsFrame = new Rect(labelFrame.X, labelFrame.Center.Y - height / 2, labelFrame.Width, height);
            Assert.Equal(expected, MacTextMetrics.CenteredFirstBaseline(windowsFrame, label.Text, label.Style.FontSize), 9);
        }
    }

    /// <summary>
    /// The Mac's measurement as the Windows rules expect it: line heights from the recorded font metrics with TextKit's
    /// rounding, and a width whose rounded-up value plus 2 is the recorded width (or below the 32-point minimum).
    /// </summary>
    private sealed class MacTextModel(FontLineMetrics metrics, double width) : ITextMeasurer
    {
        public static JsonObject SystemFont28 => Corpus.Lines("geometry/text.jsonl").First(record =>
            record["fontSize"]?.GetValue<double>() == 28 && record["fontName"]!.GetValue<string>() == "system" && !record["bold"]!.GetValue<bool>());

        public static MacTextModel For(JsonObject fontRecord, Size result) => new(
            new FontLineMetrics(fontRecord["ascender"]!.GetValue<double>(), -fontRecord["descender"]!.GetValue<double>(), fontRecord["leading"]!.GetValue<double>()),
            result.Width > 32 ? result.Width - 2.75 : 20.75);

        public Size Measure(string text, TextFont font)
        {
            var lines = text.Count(character => character == '\n') + 1;
            var line = Math.Round(metrics.Ascent, MidpointRounding.AwayFromZero) + Math.Round(metrics.Descent, MidpointRounding.AwayFromZero)
                       + Math.Round(metrics.Leading, MidpointRounding.AwayFromZero);
            return new Size(width, lines * line);
        }

        public FontLineMetrics LineMetrics(TextFont font) => metrics;
    }

    private static void RendererPath(string caseName)
    {
        var record = CorpusCases.Record(caseName);
        var element = ElementOf(record, "element");
        var path = new SceneBuilder(MacTextModel.For(MacTextModel.SystemFont28, new Size(32, 35))).Path(element);
        Same(caseName, record, new JsonObject { ["path"] = Shapes.PathSummary(path) });
    }

    /// <summary>Decodes an element of the record and checks that the Windows codec writes it back unchanged.</summary>
    private static Element ElementOf(JsonObject record, string key)
    {
        var element = AnnotatorJson.Decode<Element>(Bytes(record[key]));
        JsonDiff.Equal(record[key], Outcome.Value(element), NumberRule.Exact, $"{key} round trip");
        return element;
    }

    private static byte[] Bytes(JsonNode? node) => Encoding.UTF8.GetBytes(node!.ToJsonString());

    /// <summary>Compares each field the Windows code produced with the same field of the record.</summary>
    private static void Same(string caseName, JsonObject record, JsonObject actual)
    {
        var differences = new List<string>();
        foreach (var (key, value) in actual)
        {
            differences.AddRange(JsonDiff.Differences(record[key], value, Numbers).Select(line => key + line[1..]));
        }
        Assert.True(differences.Count == 0, JsonDiff.Describe(differences, CorpusCases.Id(caseName)));
    }

    private static Tool ToolOf(JsonNode? node) => Tool.FromRawValue(node!.GetValue<string>()) ?? throw new InvalidDataException($"Unknown tool {node}.");

    private static double NumberOf(JsonNode? node) => node!.GetValueKind() == System.Text.Json.JsonValueKind.String
        ? node.GetValue<string>() switch
        {
            "NaN" => double.NaN,
            "+Infinity" => double.PositiveInfinity,
            "-Infinity" => double.NegativeInfinity,
            var other => throw new InvalidDataException($"Unknown number {other}."),
        }
        : node.GetValue<double>();

    private static JsonArray Points(IEnumerable<Point> points) => new(points.Select(point => (JsonNode)Shapes.Json(point)).ToArray());

    private static JsonObject Curve(ConnectorCurve curve) => new()
    {
        ["bounds"] = Shapes.Json(curve.Bounds),
        ["end"] = Shapes.Json(curve.End),
        ["firstControl"] = Shapes.Json(curve.FirstControl),
        ["samples"] = Points(Enumerable.Range(0, 9).Select(step => curve.PointAt(step / 8.0))),
        ["secondControl"] = Shapes.Json(curve.SecondControl),
        ["start"] = Shapes.Json(curve.Start),
    };

    private static JsonObject Viewport(Viewport value) => new()
    {
        ["output"] = Shapes.Json(value.Output),
        ["outputPoints"] = Points([value.OutputPoint(value.Source.Origin), value.OutputPoint(value.Source.Center)]),
        ["source"] = Shapes.Json(value.Source),
        ["sourcePoints"] = Points([value.SourcePoint(value.Output.Origin), value.SourcePoint(value.Output.Center),
                                   value.SourcePoint(new Point(value.Output.MaxX, value.Output.MaxY))]),
    };

    private static JsonNode? Head(ArrowHeadGeometry? candidate)
    {
        if (candidate is not { } head)
        {
            return null;
        }
        return new JsonObject
        {
            ["base"] = Shapes.Json(head.Base),
            ["direction"] = Shapes.Json(head.Direction),
            ["hollow"] = head.Hollow,
            ["kind"] = head.Kind.RawValue,
            ["left"] = Shapes.Json(head.Left),
            ["length"] = head.Length,
            ["polygon"] = Points(head.Polygon),
            ["relationshipCircle"] = head.RelationshipCircle is { } circle
                ? new JsonObject { ["center"] = Shapes.Json(circle.Center), ["radius"] = circle.Radius }
                : null,
            ["relationshipSegments"] = new JsonArray(head.RelationshipSegments
                .Select(segment => (JsonNode)new JsonArray(Shapes.Json(segment.Start), Shapes.Json(segment.End))).ToArray()),
            ["right"] = Shapes.Json(head.Right),
            ["side"] = Shapes.Json(head.Side),
            ["square"] = Points(head.Square),
            ["tip"] = Shapes.Json(head.Tip),
        };
    }
}
