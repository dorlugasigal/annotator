using Annotator.Core;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Tests;

/// <summary>My shapes thumbnails (the Mac's ShapeThumbnailView): the padding rule, the fit and where the drawings land.</summary>
public sealed class ShapeThumbnailTests
{
    [Fact]
    public void PaddingFollowsTheMacRule()
    {
        var thin = new Element(Tool.Rectangle, new Rect(0, 0, 100, 50), style: Style.Defaults(Tool.Rectangle));
        thin.Style.Width = 1;
        thin.Style.Roughness = 0;
        Assert.Equal(12, ShapeThumbnailRenderer.Padding(ElementList.Create([thin])));
        var rough = thin;
        rough.Style.Width = 3;
        rough.Style.Roughness = 2;
        Assert.Equal(30, ShapeThumbnailRenderer.Padding(ElementList.Create([thin, rough])));
        var arrow = new Element(Tool.Arrow, new Rect(0, 0, 100, 0), style: Style.Defaults(Tool.Arrow));
        arrow.Style.Width = 2;
        arrow.Style.HeadScale = 1.5;
        arrow.Style.Roughness = 0;
        Assert.Equal(24, ShapeThumbnailRenderer.Padding(ElementList.Create([arrow])));
        Assert.Equal(12, ShapeThumbnailRenderer.Padding(ElementList.Empty));
    }

    [Fact]
    public void DrawingsAreFittedAndCenteredInTheThumbnail()
    {
        var box = new Element(Tool.Rectangle, new Rect(300, 200, 100, 50), style: Style.Defaults(Tool.Rectangle));
        box.Style.Width = 3;
        box.Style.Roughness = 0;
        box.Style.Color = new RGBA(1, 0, 0);
        ElementList elements = ElementList.Create([box]);
        double fit = ShapeThumbnailRenderer.Fit(box.Frame, ShapeThumbnailRenderer.Padding(elements), 154, 72);
        Assert.Equal(72.0 / (50 + 48), fit, 9);
        using var renderer = new ShapeThumbnailRenderer(Warp.Devices, Warp.Measurer);
        RasterImage image = renderer.Render(elements, box.Frame, 154, 72, 2);
        Assert.Equal(308, image.Width);
        Assert.Equal(144, image.Height);
        // The rectangle's left edge sits half its fitted width left of the middle; its inside stays empty.
        int left = (int)Math.Round((77 - 50 * fit) * 2);
        Assert.True(image[left, 72].Alpha > 200, $"edge alpha {image[left, 72].Alpha}");
        Assert.True(image[left, 72].Red > 200);
        Assert.Equal(0, image[154, 72].Alpha);
        Assert.True(Pixels.TransparentOutside(image, left - 4, (int)((36 - 25 * fit) * 2) - 4, 308 - left + 4, (int)((36 + 25 * fit) * 2) + 4));
    }
}
