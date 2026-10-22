// Port of apps/macos/Tests/AnnotationCoreTests/CursorHighlightTests.swift.
namespace Annotator.Core.Tests;

public class CursorHighlightTests
{
    [Fact]
    public void PointerVisibilityExpiresWithoutChangingDrawingState()
    {
        var settings = new CursorHighlightSettings();
        Assert.True(settings.IsVisible(now: 10, lastMovement: null, lastClick: null));
        settings.Visibility = CursorHighlightVisibility.Moving;
        Assert.True(settings.IsVisible(now: 10.5, lastMovement: 10, lastClick: null));
        Assert.False(settings.IsVisible(now: 10.8, lastMovement: 10, lastClick: null));
        settings.Visibility = CursorHighlightVisibility.Clicks;
        Assert.True(settings.IsVisible(now: 10.2, lastMovement: null, lastClick: 10));
        Assert.False(settings.IsVisible(now: 10.5, lastMovement: null, lastClick: 10));
        foreach (var size in new[] { double.NaN, double.PositiveInfinity, 0, 161 })
        {
            settings.Diameter = size;
            var candidate = settings;
            Assert.Throws<CanvasError>(() => candidate.Validate());
        }
    }

    [Fact]
    public void HaloSettingsPreserveLegacyDefaultsAndValidateGlow()
    {
        var settings = AnnotatorJson.Decode<CursorHighlightSettings>("""
            {"shape":"roundedSquare","visibility":"always","color":{"red":1,"green":0.82,"blue":0,"alpha":0.34},
            "diameter":74,"borderWidth":7,"showClicks":true}
            """u8);
        settings.Validate();
        Assert.Equal(CursorHighlightShape.RoundedSquare, settings.Shape);
        Assert.Equal(0.0, settings.ResolvedGlow);
        Assert.Equal(CursorClickEffect.Pulse, settings.ResolvedClickEffect);
        Assert.Equal(StrokePattern.Solid, settings.ResolvedBorder);
        settings.ShowClicks = false;
        Assert.Equal(CursorClickEffect.Off, settings.ResolvedClickEffect);
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, -1, 25 })
        {
            settings.GlowRadius = value;
            var candidate = settings;
            Assert.Throws<CanvasError>(() => candidate.Validate());
        }
        settings.GlowRadius = 12;
        settings.ClickEffect = CursorClickEffect.Ripple;
        settings.Shape = CursorHighlightShape.Diamond;
        settings.BorderStyle = StrokePattern.Dotted;
        settings.Validate();
        Assert.Equal(settings, AnnotatorJson.Decode<CursorHighlightSettings>(AnnotatorJson.Encode(settings)));
    }

    [Fact]
    public void ClickProgressIsBoundedAndClicksRevealMovingHighlights()
    {
        Assert.Null(CursorHighlightSettings.ClickProgress(now: 10, startedAt: null));
        Assert.Null(CursorHighlightSettings.ClickProgress(now: 9, startedAt: 10));
        Assert.Null(CursorHighlightSettings.ClickProgress(now: double.PositiveInfinity, startedAt: 10));
        Assert.Equal(0.0, CursorHighlightSettings.ClickProgress(now: 10, startedAt: 10));
        Assert.True(Math.Abs((CursorHighlightSettings.ClickProgress(now: 10.225, startedAt: 10) ?? 0) - 0.5) < 0.001);
        Assert.Null(CursorHighlightSettings.ClickProgress(now: 10.5, startedAt: 10));
        var settings = new CursorHighlightSettings();
        settings.Visibility = CursorHighlightVisibility.Moving;
        Assert.True(settings.IsVisible(now: 10.2, lastMovement: null, lastClick: 10));
    }
}
