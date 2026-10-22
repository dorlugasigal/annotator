// Tests of the scene composition helpers that canvas layers use: single-element lists, combined snapshots and
// hand-built adornment lists.
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using static Annotator.Core.Tests.SceneTestSupport;

namespace Annotator.Core.Tests;

public class SceneCompositionTests
{
    [Fact]
    public void ListMatchesTheBuiltSceneAndIsCached()
    {
        var builder = Builder();
        var shape = new Element(Tool.Rectangle, new Rect(10, 10, 80, 40));
        var list = builder.List(shape);
        AssertSameLists(ListOf(Builder().Build(ElementList.Create([shape])), shape.Id), list);
        Assert.Same(list, builder.List(shape));

        var faded = builder.List(shape, faded: true);
        Assert.NotSame(list, faded);
        Assert.Equal(DisplayOp.PushLayer, faded.Items[0].Op);
        Assert.Equal(0.22, faded.Items[0].Opacity);
    }

    [Fact]
    public void ListExtendsAGrowingStrokeInsteadOfRebuildingIt()
    {
        var builder = Builder();
        var stroke = new Element(Tool.Pen, points: [new Point(20, 20), new Point(40, 24)]);
        _ = builder.List(stroke);
        var builds = builder.FullPathBuilds;
        for (var sample = 0; sample < 5; sample++)
        {
            stroke.Points = stroke.Points.Add(new Point(60 + sample * 10, 30));
            stroke.Frame = Rect.Enclosing(stroke.Points.AsSpan());
            stroke.GeometryToken = Guid.NewGuid();
            AssertSameLists(ListOf(Builder().Build(ElementList.Create([stroke])), stroke.Id), builder.List(stroke));
        }
        Assert.Equal(builds, builder.FullPathBuilds);
        Assert.Equal(5, builder.IncrementalAppends);
    }

    [Fact]
    public void CreateKeepsOrderAndLeavesOutEmptyLists()
    {
        var builder = Builder();
        var first = builder.List(new Element(Tool.Ellipse, new Rect(0, 0, 20, 20)));
        var second = builder.List(new Element(Tool.Rectangle, new Rect(30, 0, 20, 20)));
        var scene = SceneSnapshot.Create([first, DisplayList.Empty, second]);
        Assert.Equal(new[] { first, second }, scene.Elements.ToArray());
        Assert.Same(DisplayList.Empty, scene.Selection);
        Assert.Same(SceneSnapshot.Empty, SceneSnapshot.Create([DisplayList.Empty]));
        Assert.Throws<ArgumentNullException>(() => SceneSnapshot.Create([first, null!]));
    }

    [Fact]
    public void BuilderMakesBalancedListsOnly()
    {
        var builder = new DisplayListBuilder();
        var outline = new PathBuilder(5, 4).AddRect(new Rect(1, 2, 3, 4)).ToPathData();
        var list = builder.Add(DisplayItem.PushLayer(0.5))
            .Add(DisplayItem.StrokePath(outline, new StrokeStyle(1), Paint.Accent))
            .Add(DisplayItem.PopLayer())
            .ToList();
        Assert.Equal(Guid.Empty, list.ElementId);
        Assert.Equal(new[] { DisplayOp.PushLayer, DisplayOp.StrokePath, DisplayOp.PopLayer }, list.Items.ToArray().Select(item => item.Op));
        Assert.Equal(0, builder.Count);

        Assert.Throws<InvalidOperationException>(() => builder.Add(DisplayItem.PopLayer()));
        builder.Add(DisplayItem.PushClip(new Rect(0, 0, 1, 1)));
        Assert.Throws<InvalidOperationException>(() => builder.Add(DisplayItem.PopTransform()));
        Assert.Throws<InvalidOperationException>(() => builder.ToList());
        builder.Clear();
        Assert.Same(DisplayList.Empty, builder.ToList());
    }

    [Fact]
    public void BuilderWrapsAnElementListWithItsIssues()
    {
        var image = new Element(Tool.Image, new Rect(0, 0, 40, 30));
        var source = Builder().List(image);
        Assert.Equal(SceneIssues.MissingImageData, source.Issues);
        var wrapped = new DisplayListBuilder().Add(DisplayItem.PushLayer(0.4)).AddRange(source).Add(DisplayItem.PopLayer()).ToList(image.Id);
        Assert.Equal(image.Id, wrapped.ElementId);
        Assert.Equal(source.Count + 2, wrapped.Count);
        Assert.Equal(SceneIssues.MissingImageData, wrapped.Issues);
    }
}
