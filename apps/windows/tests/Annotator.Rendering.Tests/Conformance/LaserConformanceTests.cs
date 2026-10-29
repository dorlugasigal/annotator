using System.Globalization;
using System.Text.Json;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Tests.Conformance;

/// <summary>
/// The corpus <c>laser</c> pictures (the Mac's LaserTrailRenderer at 1x and 2x, transparent, top-left origin) against the
/// Windows renderer on WARP at the frozen WIN-REND-05 thresholds (<see cref="ComparisonThresholds.Frozen"/>). Each
/// picture's scenario in <c>laser/trails.jsonl</c> is played through Annotator.Core's LaserTrail and drawn with
/// LaserTrailRenderer's display list at the picture's time.
/// </summary>
public sealed class LaserConformanceTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var item in LaserPictures.Cases)
        {
            data.Add(item.Name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TrailMatchesTheMac(string name)
    {
        var item = LaserPictures.Find(name);
        var reference = ConformanceCorpus.Png($"laser/{item.ReferencePath}");
        var candidate = LaserPictures.Render(item);
        Assert.Equal((reference.Width, reference.Height), (candidate.Width, candidate.Height));
        var result = ImageComparison.Compare(reference, candidate, ComparisonThresholds.Frozen);
        Evidence.Write("laser", item.Name, reference, candidate, result);
        Assert.True(result.Passed, $"{item.Name}: {result.Summary()}");
    }

    [Fact]
    public void EveryPictureHasItsScenario()
    {
        Assert.NotEmpty(LaserPictures.Cases);
        foreach (var item in LaserPictures.Cases)
        {
            Assert.NotNull(LaserPictures.Trail(item.Scenario).Bounds(item.Time));
        }
    }
}

/// <summary>One laser picture: its scenario, the time it shows and the scale it was drawn at.</summary>
internal sealed record LaserPicture(string Scenario, double Time, int Scale, string ReferencePath, Size Size)
{
    public string Name => string.Create(CultureInfo.InvariantCulture, $"{Scenario.Replace(' ', '-')}-{Time}@{Scale}x");

    public override string ToString() => Name;
}

/// <summary>The pictures of the corpus <c>laser</c> section, drawn with Annotator.Rendering on WARP.</summary>
internal static class LaserPictures
{
    private static readonly Lazy<IReadOnlyList<LaserPicture>> AllCases = new(Read);

    public static IReadOnlyList<LaserPicture> Cases => AllCases.Value;

    public static LaserPicture Find(string name) => Cases.Single(item => item.Name == name);

    /// <summary>The trail a scenario's steps leave (Mac LaserExport.play).</summary>
    public static LaserTrail Trail(string scenario)
    {
        var record = ConformanceCorpus.Lines("laser/trails.jsonl").Single(line => line.GetProperty("name").GetString() == scenario);
        var trail = new LaserTrail(record.GetProperty("lifetime").GetDouble());
        foreach (var step in record.GetProperty("steps").EnumerateArray())
        {
            var property = step.EnumerateObject().Single();
            var value = property.Value;
            var time = value.GetProperty("time").GetDouble();
            switch (property.Name)
            {
                case "press":
                    var color = value.GetProperty("color");
                    trail.Press(PointOf(value.GetProperty("point")), time,
                                new RGBA(color.GetProperty("red").GetDouble(), color.GetProperty("green").GetDouble(),
                                         color.GetProperty("blue").GetDouble(), color.GetProperty("alpha").GetDouble()),
                                value.GetProperty("width").GetDouble());
                    break;
                case "move":
                    trail.Move(PointOf(value.GetProperty("point")), time);
                    break;
                case "release":
                    trail.Release(value.TryGetProperty("point", out var point) && point.ValueKind != JsonValueKind.Null ? PointOf(point) : null, time);
                    break;
                case "prune":
                    trail.Prune(time);
                    break;
                default:
                    throw new InvalidDataException($"Unknown laser step {property.Name}.");
            }
        }
        return trail;
    }

    /// <summary>
    /// The Windows image: the trail's display list at the picture's time, at its size and scale on a transparent background,
    /// as the Mac draws it into a bitmap with a top-left origin.
    /// </summary>
    public static RasterImage Render(LaserPicture item)
    {
        var list = LaserTrailRenderer.List(Trail(item.Scenario), item.Time)
                   ?? throw new InvalidDataException($"{item.Name} shows no trail at {item.Time} s.");
        using var exporter = new PngExporter(ConformanceDevices.Devices, Warp.Measurer, MacAppearance.Light);
        return exporter.Render(SceneSnapshot.Create([list]), item.Size, item.Scale, includeSelection: false);
    }

    private static Point PointOf(JsonElement point) => new(point[0].GetDouble(), point[1].GetDouble());

    private static IReadOnlyList<LaserPicture> Read()
    {
        var cases = new List<LaserPicture>();
        foreach (var record in ConformanceCorpus.Lines("laser/pictures.jsonl"))
        {
            var size = record.GetProperty("size");
            foreach (var image in record.GetProperty("images").EnumerateObject())
            {
                cases.Add(new LaserPicture(record.GetProperty("scenario").GetString()!, record.GetProperty("time").GetDouble(),
                                           int.Parse(image.Name.TrimEnd('x'), CultureInfo.InvariantCulture), image.Value.GetString()!,
                                           new Size(size.GetProperty("width").GetDouble(), size.GetProperty("height").GetDouble())));
            }
        }
        return cases;
    }
}
