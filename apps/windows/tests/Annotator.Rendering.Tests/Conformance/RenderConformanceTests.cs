using System.Globalization;

namespace Annotator.Rendering.Tests.Conformance;

/// <summary>
/// The corpus <c>render</c> scenes (plan task M-1) against the Windows renderer on WARP at 1x and 2x, with the
/// WIN-REND-05 comparison at its frozen thresholds (<see cref="ComparisonThresholds.Frozen"/>). Font-dependent scenes
/// compare the layout rules and where the glyphs sit instead of glyph pixels (<see cref="TextLayoutComparison"/>).
/// Interpolation-dependent scenes must stay within the distance from the Mac measured with Direct2D's high-quality cubic
/// interpolation, which the owner approved on 2026-10-19: Core Graphics resamples a scaled-up image on a coarser grid,
/// which no Direct2D mode reproduces.
/// </summary>
public sealed class RenderConformanceTests
{
    /// <summary>
    /// The measured distance of each interpolation-dependent image from the Mac's, on WARP with high-quality cubic
    /// interpolation (S-13, gate0-results.md): CIEDE2000 over all pixels at the 95th and 99th percentiles and at most, and
    /// the largest channel difference. A change that moves the image further from the Mac fails.
    /// </summary>
    private static readonly Dictionary<string, (double P95, double P99, double Max, int Channel)> MeasuredDistances = new()
    {
        ["image@1x"] = (7.31, 10.69, 25.49, 41),
        ["image@2x"] = (7.34, 10.71, 27.07, 45),
    };

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var item in RenderCorpus.Cases)
        {
            data.Add(item.Name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SceneMatchesTheMac(string name)
    {
        var item = RenderCorpus.Find(name);
        var reference = RenderCorpus.Reference(item);
        if (item.FontDependent)
        {
            var layout = TextLayoutComparison.Compare(item, reference, ComparisonThresholds.Frozen);
            Evidence.Write("render", item.Name, reference, layout.Windows, layout.Rest, layout.Summary(), layout.Runs.Select(run => run.ToString()),
                           layout.Passed ? "pass (layout rules)" : "fail (layout rules)");
            Assert.True(layout.Passed, $"{item.Name}: {layout.Summary()}");
            return;
        }
        var candidate = RenderCorpus.Render(item);
        Assert.Equal((reference.Width, reference.Height), (candidate.Width, candidate.Height));
        var result = ImageComparison.Compare(reference, candidate, ComparisonThresholds.Frozen);
        if (item.InterpolationDependent)
        {
            var (p95, p99, max, channel) = (result.Percentile(0.95), result.Percentile(0.99), result.Percentile(1), result.MaxDifference);
            var distance = string.Create(CultureInfo.InvariantCulture,
                $"CIEDE2000 p95 {p95:F3}, p99 {p99:F3}, max {max:F3}; largest channel difference {channel}");
            Assert.True(MeasuredDistances.TryGetValue(item.Name, out var measured), $"{item.Name} has no measured distance: {distance}.");
            var within = p95 <= measured.P95 && p99 <= measured.P99 && max <= measured.Max && channel <= measured.Channel;
            Evidence.Write("render", item.Name, reference, candidate, result, details: [distance],
                           status: within ? "pass (measured interpolation distance)" : "fail (measured interpolation distance)");
            Assert.True(within, string.Create(CultureInfo.InvariantCulture,
                $"{item.Name} moved further from the Mac: {distance}; measured at most {measured.P95}, {measured.P99}, {measured.Max} and {measured.Channel}."));
            return;
        }
        Evidence.Write("render", item.Name, reference, candidate, result);
        Assert.True(result.Passed, $"{item.Name}: {result.Summary()}");
    }
}
