// Port of apps/macos/Tests/AnnotationCoreTests/AxisStyleTests.swift.
using static Annotator.Core.Tests.TestSupport;

namespace Annotator.Core.Tests;

public class AxisStyleTests
{
    [Fact]
    public void AxisStyleValidatesCountAndReadsLegacyFiles()
    {
        var legacy = AnnotatorJson.Decode<Style>(AnnotatorJson.Encode(new Style()));
        Assert.Null(legacy.AxisTickCount);
        foreach (var count in new long[] { 0, 1, 9, 24 })
        {
            var style = legacy;
            style.AxisTickCount = count;
            style.NegativeAxes = true;
            style.Validate();
            Assert.Equal(style, AnnotatorJson.Decode<Style>(AnnotatorJson.Encode(style)));
        }
        foreach (var count in new[] { -1, 25, long.MaxValue })
        {
            var style = legacy;
            style.AxisTickCount = count;
            AssertCanvasError(CanvasError.InvalidStyle, () => style.Validate());
        }
    }

    [Fact]
    public void AxisSettingsStayPerObjectAndSurviveSaveAndUndo()
    {
        var first = new Element(Tool.Axes, new Rect(20, 30, 200, 100));
        var second = new Element(Tool.Axes, new Rect(250, 30, 200, 100));
        var document = new CanvasDocument([first, second]);
        document.Select([first.Id]);
        document.Begin();
        document.EditSelection((ref Element element) =>
        {
            element.Style.AxisTickCount = 6;
            element.Style.NegativeAxes = true;
        });
        document.Commit();
        Assert.Equal(6, document.Elements[0].Style.AxisTickCount);
        Assert.True(document.Elements[0].Style.NegativeAxes == true);
        Assert.Equal(second, document.Elements[1]);
        var saved = DrawingFile.Read(new DrawingFile(document).Data());
        Assert.Equal(document.Elements, saved.Elements);
        document.Undo();
        Assert.Equal([first, second], document.Elements);
        document.Redo();
        Assert.Equal(saved.Elements, document.Elements);
    }
}
