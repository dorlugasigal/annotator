using System.Text;
using System.Text.Json.Nodes;
using Annotator.Core;

namespace Annotator.Conformance.Tests;

/// <summary>
/// Section <c>recognition</c>: Smart Draw on recorded pen strokes. Same tool or null; numbers within 1e-6. The
/// recognized element keeps the stroke's ID; its new geometry token and append epoch get stand-in IDs, numbered across
/// the whole file in record order.
/// </summary>
public class RecognitionTests
{
    private static readonly Lazy<List<JsonNode?>> Results = new(() =>
    {
        var ids = new FreshIds();
        return Corpus.Lines("recognition/strokes.jsonl")
            .Select(record => ShapeRecognition.Recognize(Decode(record["stroke"])) is { } shape ? Outcome.Value(ids.Rename(shape)) : null)
            .ToList();
    });

    public static TheoryData<string> StrokeCases => CorpusCases.Of("recognition/strokes.jsonl");

    [Theory]
    [MemberData(nameof(StrokeCases))]
    public void RecognitionMatchesTheMac(string caseName)
    {
        var record = CorpusCases.Record(caseName);
        var stroke = Decode(record["stroke"]);
        JsonDiff.Equal(record["stroke"], Outcome.Value(stroke), NumberRule.Exact, "stroke round trip");
        JsonDiff.Equal(record["result"], Results.Value[CorpusCases.Index(caseName)], NumberRule.Within(1e-6), CorpusCases.Id(caseName));
    }

    [Fact]
    public void PreviewInputMatchesTheMac()
    {
        var expected = Corpus.Json("recognition/preview-input.json");
        var stroke = Fixture.Element(7_100, Tool.Pen,
            points: Enumerable.Range(0, 1_000).Select(index => new Point(index * 0.5, 100 + 40 * Math.Sin(index / 40.0))).ToArray());
        var preview = ShapeRecognition.PreviewInput(stroke);
        JsonDiff.Equal(expected, new JsonObject
        {
            ["note"] = expected["note"]!.GetValue<string>(),
            ["previewPoints"] = new JsonArray(preview.Points.Select(point => (JsonNode)Shapes.Json(point)).ToArray()),
            ["previewSeed"] = preview.Seed,
            ["strokeFirst"] = Shapes.Json(stroke.Points[0]),
            ["strokeLast"] = Shapes.Json(stroke.Points[^1]),
            ["strokePointCount"] = stroke.Points.Count,
        }, NumberRule.Within(1e-6));
    }

    internal static Element Decode(JsonNode? node) => AnnotatorJson.Decode<Element>(Encoding.UTF8.GetBytes(node!.ToJsonString()));
}

/// <summary>
/// Section <c>hittest</c>: grids of <c>HitTesting.contains</c> per element and of <c>ElementStacking.frontmost</c> per
/// scene. Exact, except points within 1e-9 of a threshold: a cell that differs passes only when moving its point by at
/// most 1e-9 gives the Mac's answer.
/// </summary>
public class HitTestTests
{
    private const double Nudge = 1e-9;

    public static TheoryData<string> ContainsCases => CorpusCases.Of("hittest/contains.jsonl");

    public static TheoryData<string> FrontmostCases => CorpusCases.Of("hittest/frontmost.jsonl", record => record["scene"]!.GetValue<string>());

    [Theory]
    [MemberData(nameof(ContainsCases))]
    public void ContainsGridMatchesTheMac(string caseName)
    {
        var record = CorpusCases.Record(caseName);
        var element = RecognitionTests.Decode(record["element"]);
        JsonDiff.Equal(record["element"], Outcome.Value(element), NumberRule.Exact, "element round trip");
        var tolerance = record["tolerance"]!.GetValue<double>();
        CompareGrid(caseName, record, point => HitTesting.Contains(point, element, tolerance) ? '#' : '.');
    }

    [Theory]
    [MemberData(nameof(FrontmostCases))]
    public void FrontmostGridMatchesTheMac(string caseName)
    {
        var record = CorpusCases.Record(caseName);
        var elements = ElementList.CreateRange(record["elements"]!.AsArray().Select(RecognitionTests.Decode));
        var letters = record["legend"]!.AsObject().ToDictionary(entry => Guid.Parse(entry.Value!.GetValue<string>()), entry => entry.Key[0]);
        Assert.Equal(elements.Count, letters.Count);
        CompareGrid(caseName, record, point => ElementStacking.Frontmost(point, elements) is { } hit ? letters[hit.Id] : '.');
    }

    private static void CompareGrid(string caseName, JsonObject record, Func<Point, char> symbol)
    {
        var origin = Shapes.Point(record["origin"]);
        var step = record["step"]!.GetValue<double>();
        var columns = record["columns"]!.GetValue<int>();
        var rows = record["rows"]!.GetValue<int>();
        var grid = record["grid"]!.AsArray().Select(row => row!.GetValue<string>()).ToList();
        Assert.Equal(rows, grid.Count);
        var differences = new List<string>();
        for (var row = 0; row < rows; row++)
        {
            Assert.Equal(columns, grid[row].Length);
            for (var column = 0; column < columns; column++)
            {
                var point = origin + new Point(column * step, row * step);
                var expected = grid[row][column];
                if (symbol(point) == expected || NearThreshold(point, expected, symbol))
                {
                    continue;
                }
                differences.Add($"row {row}, column {column} at ({Shapes.Text(point.X)}, {Shapes.Text(point.Y)}): expected '{expected}', found '{symbol(point)}'");
            }
        }
        Assert.True(differences.Count == 0, $"{CorpusCases.Id(caseName)}: {differences.Count} cell(s) differ\n" + string.Join("\n", differences.Take(25)));
    }

    private static bool NearThreshold(Point point, char expected, Func<Point, char> symbol)
    {
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (symbol(point + new Point(dx * Nudge, dy * Nudge)) == expected)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
