namespace Annotator.Conformance.Tests;

/// <summary>
/// The corpus cases that still differ on Windows. Each entry names the case ("file#name") and says why. Keep this list
/// short and true: fix a difference or explain it here, and ask for a Mac re-export when the corpus itself is wrong.
/// </summary>
internal static partial class OpenDifferences
{
    private const string CoreGraphicsNoise =
        "Core Graphics builds arcs and rounded corners through rotations, so some of its points differ from the direct " +
        "formula in the last bit or two (at most 3e-14 here). Element kinds, counts and every other value match; the test " +
        "checks the difference stays within 1e-12.";

    private const string WindowsTangent =
        "Windows' tan differs from Apple's libm by 2 units in the last place for this input; the README allows 1. The test " +
        "checks it stays within 2.";

    private static IEnumerable<(string Id, string Reason)> Entries()
    {
        foreach (var name in new[]
        {
            "rounded rect", "rounded rect with unequal radii", "rounded rect with full radius", "rounded rect with tiny radius",
            "rounded rect after an open subpath", "rounded rect after a point at its start", "arc quarter clockwise",
            "arc 100 degrees", "arc half", "arc full circle", "arc more than a full circle", "arc negative angles",
            "relative arc 100 degrees", "relative arc minus 45 degrees", "CGPath(roundedRect:)",
            "CGPath(roundedRect:) rotated, as the diamond pointer halo",
        })
        {
            yield return ("platform/cgpath.jsonl#" + name, CoreGraphicsNoise);
        }
        yield return ("platform/math.jsonl#-5.251909036748856", WindowsTangent);
        yield return ("platform/math.jsonl#-4.423522378381235", WindowsTangent);
    }
}
