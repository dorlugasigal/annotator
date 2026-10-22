namespace Annotator.Rendering.Tests.Conformance;

/// <summary>
/// The corpus <c>effects</c> cases (Core Image through the Mac's CaptureCompositor) against the Windows effect
/// pipeline on WARP, at the frozen WIN-REND-05 thresholds for pixel effects (<see cref="ComparisonThresholds.Effects"/>):
/// the first thresholds with no allowance or outliers, and no channel more than 2/255 apart, because the pipeline
/// reproduces Core Image's filters rather than approximating them.
/// </summary>
public sealed class EffectsConformanceTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var item in EffectsCorpus.Cases)
        {
            data.Add(item.Name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EffectMatchesCoreImage(string name)
    {
        var item = EffectsCorpus.Find(name);
        var reference = EffectsCorpus.Reference(item);
        var candidate = EffectsCorpus.Render(item);
        Assert.Equal((reference.Width, reference.Height), (candidate.Width, candidate.Height));
        var result = ImageComparison.Compare(reference, candidate, ComparisonThresholds.Effects);
        Evidence.Write("effects", item.Name, reference, candidate, result);
        Assert.True(result.Passed, $"{item.Name}: {result.Summary()}");
    }
}
