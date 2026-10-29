using System.Text.Json.Nodes;
using Annotator.Core;

namespace Annotator.Conformance.Tests;

/// <summary>
/// Section <c>laser</c>: the laser pointer's trail (LaserTrail). Each scenario's presses, moves, releases and prunes are
/// played through the port, then its strokes, the dots, opacity and per-layer bands of every stroke at the set times, the
/// bounds, and what a prune at each time leaves are compared with the Mac. Exact; numbers within 1e-9. The pictures in the
/// same section are compared by the Windows rendering tests.
/// </summary>
public class LaserTests
{
    private static readonly NumberRule Numbers = NumberRule.Within(1e-9);

    public static TheoryData<string> TrailCases => CorpusCases.Of("laser/trails.jsonl");

    [Theory]
    [MemberData(nameof(TrailCases))]
    public void TrailMatchesTheMac(string caseName)
    {
        JsonObject record = CorpusCases.Record(caseName);
        LaserTrail trail = Play(record["steps"]!.AsArray());
        var actual = new JsonObject
        {
            ["strokes"] = new JsonArray([.. trail.Strokes.Select(Stroke)]),
            ["isDrawing"] = trail.IsDrawing,
            ["isResting"] = trail.IsResting,
            ["frames"] = new JsonArray([.. record["frames"]!.AsArray().Select(frame => Frame(trail, frame!["time"]!.GetValue<double>()))]),
        };
        var differences = new List<string>();
        foreach (var (key, value) in actual)
        {
            differences.AddRange(JsonDiff.Differences(record[key], value, Numbers).Select(line => key + line[1..]));
        }
        Assert.True(differences.Count == 0, JsonDiff.Describe(differences, CorpusCases.Id(caseName)));
    }

    [Fact]
    public void ConstantsMatchTheMac()
    {
        JsonNode expected = Corpus.Json("laser/constants.json");
        var actual = new JsonObject
        {
            ["defaultColor"] = Color(LaserTrail.DefaultColor),
            ["defaultWidth"] = LaserTrail.DefaultWidth,
            ["lifetime"] = LaserTrail.Lifetime,
            ["minimumStep"] = LaserTrail.MinimumStep,
            ["tailTaper"] = LaserTrail.TailTaper,
            ["restInterval"] = LaserTrail.RestInterval,
            ["maximumSamples"] = LaserTrail.MaximumSamples,
            ["passes"] = new JsonArray([.. LaserTrail.Passes.Select(pass => (JsonNode)new JsonObject
            {
                ["alpha"] = pass.Alpha,
                ["extra"] = pass.Extra,
                ["scale"] = pass.Scale,
                ["white"] = pass.White,
            })]),
            ["defaultStyle"] = JsonNode.Parse(AnnotatorJson.Encode(Style.Defaults(Tool.Laser))),
        };
        JsonDiff.Equal(expected, actual, NumberRule.Exact, "laser/constants.json");
    }

    [Fact]
    public void ScenariosCoverEveryStep()
    {
        var kinds = Corpus.Lines("laser/trails.jsonl").SelectMany(record => record["steps"]!.AsArray())
            .Select(step => step!.AsObject().Single().Key).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(["move", "press", "prune", "release"], kinds.Order(StringComparer.Ordinal));
    }

    private static LaserTrail Play(JsonArray steps)
    {
        var trail = new LaserTrail();
        foreach (JsonNode? step in steps)
        {
            var (kind, value) = step!.AsObject().Single();
            double time = value!["time"]!.GetValue<double>();
            switch (kind)
            {
                case "press":
                    trail.Press(Shapes.Point(value["point"]), time, ColorOf(value["color"]), value["width"]!.GetValue<double>());
                    break;
                case "move":
                    trail.Move(Shapes.Point(value["point"]), time);
                    break;
                case "release":
                    trail.Release(value["point"] is { } point ? Shapes.Point(point) : null, time);
                    break;
                case "prune":
                    trail.Prune(time);
                    break;
                default:
                    throw new InvalidDataException($"Unknown laser step {kind}.");
            }
        }
        return trail;
    }

    private static JsonNode Frame(LaserTrail trail, double now)
    {
        LaserTrail pruned = trail;
        pruned.Prune(now);
        return new JsonObject
        {
            ["time"] = now,
            ["shapes"] = new JsonArray([.. trail.Shapes(now).Select(Shape)]),
            ["bounds"] = trail.Bounds(now) is { } bounds
                ? new JsonObject { ["height"] = bounds.Height, ["width"] = bounds.Width, ["x"] = bounds.X, ["y"] = bounds.Y }
                : null,
            ["afterPrune"] = new JsonObject
            {
                ["strokeSampleCounts"] = new JsonArray([.. pruned.Strokes.Select(stroke => (JsonNode)stroke.Samples.Count)]),
                ["isEmpty"] = pruned.IsEmpty,
            },
        };
    }

    private static JsonNode Shape(LaserTrail.Shape shape)
    {
        LaserTrail.Dot[] dots = shape.Dots.ToArray();
        return new JsonObject
        {
            ["color"] = Color(shape.Color),
            ["opacity"] = shape.Opacity,
            ["dots"] = new JsonArray([.. dots.Select(dot => (JsonNode)new JsonObject
            {
                ["center"] = Shapes.Json(dot.Center),
                ["radius"] = dot.Radius,
            })]),
            ["bands"] = new JsonArray([.. LaserTrail.Passes.Select(pass => (JsonNode)new JsonArray([.. dots.Zip(dots.Skip(1)).Select(pair =>
                LaserTrail.Band(pair.First, pair.Second, pass) is { } band
                    ? new JsonArray([.. band.Select(point => (JsonNode)Shapes.Json(point))])
                    : (JsonNode?)null)]))]),
        };
    }

    private static JsonNode Stroke(LaserTrail.Stroke stroke) => new JsonObject
    {
        ["samples"] = new JsonArray([.. stroke.Samples.Select(sample => (JsonNode)new JsonObject
        {
            ["point"] = Shapes.Json(sample.Point),
            ["time"] = sample.Time,
        })]),
        ["color"] = Color(stroke.Color),
        ["width"] = stroke.Width,
        ["live"] = stroke.Live,
    };

    private static JsonObject Color(RGBA color) => new()
    {
        ["alpha"] = color.Alpha,
        ["blue"] = color.Blue,
        ["green"] = color.Green,
        ["red"] = color.Red,
    };

    private static RGBA ColorOf(JsonNode? node) => new(node!["red"]!.GetValue<double>(), node["green"]!.GetValue<double>(),
                                                      node["blue"]!.GetValue<double>(), node["alpha"]!.GetValue<double>());
}
