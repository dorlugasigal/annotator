// Port of apps/macos/Tests/AnnotationCoreTests/RoundedDiamondHitTests.swift.
namespace Annotator.Core.Tests;

public class RoundedDiamondHitTests
{
    [Fact]
    public void RoundedDiamondDoesNotSelectCutAwayCorner()
    {
        var diamond = new Element(Tool.Diamond, new Rect(30, 30, 120, 120));
        Assert.True(HitTesting.Contains(new Point(90, 32), diamond, tolerance: 0));
        diamond.Style.CornerRadius = 24;
        Assert.False(HitTesting.Contains(new Point(90, 32), diamond, tolerance: 0));
        Assert.True(HitTesting.Contains(new Point(90, 90), diamond, tolerance: 0));
        foreach (var corner in RoundedDiamond.Corners(diamond.Frame, radius: 24))
        {
            Assert.True(HitTesting.Contains(corner.Entry, diamond, tolerance: 0));
            Assert.True(HitTesting.Contains(corner.Exit, diamond, tolerance: 0));
        }
    }
}
