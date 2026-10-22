// Port of apps/macos/Tests/RenderingTests/CursorHighlightRenderingTests.swift (geometric parts) and SpotlightTests.swift (analytic).
using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.Core.Tests;

public class PointerEffectsTests
{
    [Fact]
    public void PulseAttacksQuicklyPeaksAtAQuarterAndReleasesToRest()
    {
        var samples = new double[101];
        for (var index = 0; index <= 100; index++)
        {
            samples[index] = CursorHighlightGeometry.PulseScale(index * 0.01);
        }
        var peak = Array.IndexOf(samples, samples.Max());
        Assert.True(Math.Abs(peak * 0.01 - 0.25) <= 0.011);
        Assert.True(Math.Abs(CursorHighlightGeometry.PulseScale(0.25) - 1.18) < 0.000001);
        Assert.Equal(1, CursorHighlightGeometry.PulseScale(0));
        Assert.True(Math.Abs(CursorHighlightGeometry.PulseScale(1) - 1) < 0.000001);
        for (var index = 0; index < 25; index++)
        {
            Assert.True(samples[index] < samples[index + 1]);
        }
        for (var index = 25; index < 100; index++)
        {
            Assert.True(samples[index] >= samples[index + 1]);
        }
        Assert.True(CursorHighlightGeometry.PulseScale(0.1) - 1 > 0.18 * 0.75, "The attack reaches most of the peak early.");
        Assert.True(CursorHighlightGeometry.PulseScale(0.5) - 1 > 0.18 * 0.5, "The release is slower than the attack.");
        Assert.Equal(0, CursorHighlightGeometry.EaseInOut(-1));
        Assert.Equal(1, CursorHighlightGeometry.EaseInOut(2));
    }

    [Fact]
    public void HaloShapesAreDistinctAndIdleHasNoClickRing()
    {
        var paths = new List<PathData>();
        foreach (var shape in CursorHighlightShape.AllCases)
        {
            var settings = new CursorHighlightSettings { Shape = shape, Diameter = 74, BorderWidth = 7 };
            var frame = CursorHighlightFrame.Create(settings, new Point(128, 128), clickProgress: null);
            Assert.Null(frame.Ripple);
            Assert.Equal(shape == CursorHighlightShape.Disc, frame.Filled);
            Assert.Equal(AffineTransform.Translation(128, 128), frame.Transform);
            Assert.Equal(7, frame.Stroke.Width);
            Assert.Equal(LineCap.Round, frame.Stroke.Cap);
            var bounds = Assert.NotNull(frame.Shape.TightBounds);
            Assert.True(bounds.Width <= 74 + 1e-9 && bounds.Height <= 74 + 1e-9, shape.ToString());
            if (shape is not (CursorHighlightShape.Ring or CursorHighlightShape.Disc))
            {
                Assert.DoesNotContain(paths, other => other.ContentEquals(frame.Shape));
            }
            paths.Add(frame.Shape);
        }
        Assert.True(paths[0].ContentEquals(paths[1]), "Ring and disc share the circle and differ by fill.");
    }

    [Fact]
    public void ClickEffectsAnimateTheSelectedShapeAndRespectReducedMotion()
    {
        foreach (var effect in new[] { CursorClickEffect.Pulse, CursorClickEffect.Ripple, CursorClickEffect.Turn })
        {
            var settings = new CursorHighlightSettings { Shape = CursorHighlightShape.RoundedSquare, Diameter = 70, ClickEffect = effect };
            var idle = CursorHighlightFrame.Create(settings, new Point(128, 128), null);
            var early = CursorHighlightFrame.Create(settings, new Point(128, 128), 0.2);
            var late = CursorHighlightFrame.Create(settings, new Point(128, 128), 0.7);
            Assert.False(SameFrame(idle, early), effect.ToString());
            Assert.False(SameFrame(early, late), effect.ToString());
            var still = CursorHighlightFrame.Create(settings, new Point(128, 128), 0.2, reduceMotion: true);
            Assert.True(SameFrame(still, CursorHighlightFrame.Create(settings, new Point(128, 128), 0.7, reduceMotion: true)));
            Assert.Equal(settings.BorderWidth + 2, still.Stroke.Width);
            Assert.Null(still.Ripple);
        }
        var off = new CursorHighlightSettings { ClickEffect = CursorClickEffect.Off };
        Assert.True(SameFrame(CursorHighlightFrame.Create(off, default, null), CursorHighlightFrame.Create(off, default, 0.5)));
        var ripple = new CursorHighlightSettings { ClickEffect = CursorClickEffect.Ripple, Diameter = 100, BorderWidth = 4 };
        var frame = CursorHighlightFrame.Create(ripple, new Point(10, 10), 1.5, clickCenter: new Point(30, 10));
        Assert.Equal(0, frame.RippleOpacity);
        Assert.NotNull(frame.Ripple);
        Assert.Equal(new Rect(-50 - 28 + 20, -50 - 28, 156, 156), frame.Ripple.Bounds);
        Assert.NotNull(frame.RippleStroke);
        Assert.Equal(2, frame.RippleStroke.Width);
    }

    [Fact]
    public void GlowAndOpacityApplyOnceAndBordersDash()
    {
        var settings = new CursorHighlightSettings
        {
            Color = new RGBA(1, 0.82, 0, 0.34), GlowRadius = 12, BorderWidth = 4, BorderStyle = StrokePattern.Dashed,
        };
        var frame = CursorHighlightFrame.Create(settings, default, null);
        Assert.Equal(0.34, frame.Opacity);
        Assert.Equal(new RGBA(1, 0.82, 0), frame.Color);
        Assert.Equal(12, frame.GlowRadius);
        Assert.Equal([12.0, 8], frame.Stroke.Dashes.ToArray());
        settings.BorderStyle = StrokePattern.Dotted;
        Assert.Equal([0.01, 10], CursorHighlightFrame.Create(settings, default, null).Stroke.Dashes.ToArray());
        var turn = new CursorHighlightSettings { ClickEffect = CursorClickEffect.Turn, Shape = CursorHighlightShape.Hexagon };
        var turned = CursorHighlightFrame.Create(turn, default, 1);
        Assert.True(Math.Abs(Math.Atan2(turned.Transform.B, turned.Transform.A) - Math.PI / 3) < 1e-12);
    }

    [Fact]
    public void SpotlightEdgesFadeInsteadOfCuttingOff()
    {
        var spotlight = Assert.NotNull(SpotlightPaint.Create(new Rect(0, 0, 400, 400), new Point(200, 200), 100));
        Assert.Equal(75, spotlight.InnerRadius);
        Assert.Equal(125, spotlight.OuterRadius);
        static byte Alpha(SpotlightPaint paint, int x) => (byte)Math.Round(paint.AlphaAt(x + 0.5 - 200) * 255);
        Assert.Equal(0, Alpha(spotlight, 200));
        Assert.True(Alpha(spotlight, 290) > 0);
        Assert.True(Alpha(spotlight, 290) < Alpha(spotlight, 300));
        Assert.True(Alpha(spotlight, 300) < Alpha(spotlight, 310));
        Assert.InRange(Alpha(spotlight, 330), (byte)155, (byte)160);
        Assert.Null(SpotlightPaint.Create(default, default, 100, opacity: 0));
        Assert.Equal(1, Assert.NotNull(SpotlightPaint.Create(default, default, 100, opacity: 3)).Opacity);
        Assert.Equal(0, Assert.NotNull(SpotlightPaint.Create(default, default, 8)).InnerRadius);
        Assert.Equal(164, Assert.NotNull(SpotlightPaint.Create(default, default, 100_000)).OuterRadius - 100_000 + 100);
        Assert.Equal(RenderFailureKind.ContextUnavailable,
                     Assert.Throws<RenderFailure>(() => SpotlightPaint.Create(default, default, 100, double.NaN)).Kind);
        Assert.Throws<RenderFailure>(() => SpotlightPaint.Create(default, default, 0));
    }

    private static bool SameFrame(CursorHighlightFrame first, CursorHighlightFrame second) =>
        first.Transform == second.Transform && first.Opacity == second.Opacity && first.Stroke.Equals(second.Stroke)
        && first.Shape.ContentEquals(second.Shape) && first.RippleOpacity == second.RippleOpacity
        && (first.Ripple is null ? second.Ripple is null : first.Ripple.ContentEquals(second.Ripple));
}
