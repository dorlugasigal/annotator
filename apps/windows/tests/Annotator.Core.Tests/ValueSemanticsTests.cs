// New for the Windows port: the value-semantics design (see the note at the top of Element.cs) and stable identifiers.
using System.Reflection;

namespace Annotator.Core.Tests;

public class ValueSemanticsTests
{
    [Fact]
    public void AppendingToSharedListsNeverChangesOtherValues()
    {
        ValueList<Point> start = [new(0, 0)];
        var first = start.Add(new Point(1, 1));
        var second = start.Add(new Point(2, 2));
        var longer = first.Add(new Point(3, 3));
        Assert.Equal(new[] { new Point(0, 0) }, start);
        Assert.Equal(new[] { new Point(0, 0), new Point(1, 1) }, first);
        Assert.Equal(new[] { new Point(0, 0), new Point(2, 2) }, second);
        Assert.Equal(new[] { new Point(0, 0), new Point(1, 1), new Point(3, 3) }, longer);
        Assert.Equal(new[] { new Point(9, 9), new Point(1, 1) }, first.SetItem(0, new Point(9, 9)));
        Assert.Equal(new Point(0, 0), first[0]);
        Assert.True(first == ValueList.Create<Point>([new(0, 0), new(1, 1)]));
    }

    [Fact]
    public void CopiedElementsAndDocumentsAreIndependentValues()
    {
        var original = new Element(Tool.Pen, points: [new(0, 0), new(10, 10)]);
        var copy = original;
        copy.Style.Width = 9;
        copy.Points = copy.Points.Add(new Point(20, 20));
        copy.Translate(new Point(5, 5));
        Assert.Equal(3.0, original.Style.Width);
        Assert.Equal(2, original.Points.Count);
        Assert.Equal(new Point(0, 0), original.Points[0]);
        Assert.Equal(0UL, original.Revision);

        var document = CanvasDocument.Empty();
        document.Add(original);
        var branch = document;
        branch.Add(new Element(Tool.Rectangle, new Rect(0, 0, 10, 10)));
        branch.Undo();
        branch.Undo();
        Assert.Single(document.Elements);
        Assert.True(document.CanUndo);
        Assert.False(document.CanRedo);
        Assert.Empty(branch.Elements);
        Assert.Equal(document.Identity, branch.Identity);
        Assert.NotEqual(document.Identity, CanvasDocument.Empty().Identity);
    }

    [Fact]
    public void ElementListsShareUnchangedChunksAndCompareByValue()
    {
        var elements = Enumerable.Range(0, 40).Select(index => new Element(Tool.Rectangle, new Rect(index, 0, 10, 10))).ToArray();
        var list = ElementList.Create(elements);
        var changedElement = list[33];
        changedElement.Locked = true;
        var changed = list.SetItem(33, changedElement);
        Assert.False(list[33].Locked);
        Assert.True(changed[33].Locked);
        Assert.NotEqual(list, changed);
        Assert.Equal(list, ElementList.CreateRange(elements));
        Assert.Equal(41, list.Add(new Element(Tool.Ellipse)).Count);
        Assert.Equal(40, list.Count);
        Assert.Equal(20, list.RemoveAll(element => element.Frame.X >= 20).Count);
        Assert.Equal(elements.Select(element => element.Id), list.Select(element => element.Id));
    }

    [Fact]
    public void LongStrokesAppendInAmortizedConstantTime()
    {
        // The Mac benchmark appends 200,000 samples in one transaction; copying the stroke per sample would be quadratic.
        var document = CanvasDocument.Empty();
        var stroke = new Element(Tool.Pen);
        document.Begin();
        document.Add(stroke);
        for (var index = 0; index < 200_000; index++)
        {
            document.AppendSample(stroke.Id, new Point(index % 1_000, index / 1_000));
        }
        document.Commit();
        Assert.Equal(200_000, document.Elements[0].Points.Count);
        Assert.Equal(1UL, document.Generation);
        document.Undo();
        Assert.Empty(document.Elements);
    }

    [Fact]
    public void RawValuesMatchTheSwiftCaseNames()
    {
        AssertRawValues(Tool.AllCases, tool => tool.RawValue);
        AssertRawValues(ArrowHead.AllCases, head => head.RawValue);
        AssertRawValues(StrokePattern.AllCases, pattern => pattern.RawValue);
        AssertRawValues(FillPattern.AllCases, pattern => pattern.RawValue);
        AssertRawValues(TextAlignment.AllCases, alignment => alignment.RawValue);
        AssertRawValues(GradientPreset.AllCases, preset => preset.RawValue);
        AssertRawValues(CursorHighlightShape.AllCases, shape => shape.RawValue);
        AssertRawValues(CursorHighlightVisibility.AllCases, visibility => visibility.RawValue);
        AssertRawValues(CursorClickEffect.AllCases, effect => effect.RawValue);
        AssertRawValues(ToolbarOrientation.AllCases, orientation => orientation.RawValue);
        AssertRawValues(ToolbarDockEdge.AllCases, edge => edge.RawValue);
        AssertRawValues(VisibilityAnimationStyle.AllCases, style => style.RawValue);
        AssertRawValues(AppTheme.AllCases, theme => theme.RawValue);
        AssertRawValues(ToolCursorStyle.AllCases, style => style.RawValue);
        AssertRawValues(ToolbarSize.AllCases, size => size.RawValue);
        AssertRawValues(ShortcutAction.AllCases, action => action.RawValue);
        AssertRawValues(ShareRegionPreset.AllCases, preset => preset.RawValue);
        Assert.Equal(40, Tool.AllCases.Count);
        Assert.Equal(Tool.Laser, Tool.FromRawValue("laser"));
        Assert.Equal("apiGateway", Tool.ApiGateway.RawValue);
        Assert.Equal(Tool.DatabaseStack, Tool.FromRawValue("databaseStack"));
        Assert.Null(Tool.FromRawValue("DatabaseStack"));
    }

    private static void AssertRawValues<TEnum>(ValueList<TEnum> cases, Func<TEnum, string> rawValue) where TEnum : struct, Enum
    {
        Assert.Equal(Enum.GetValues<TEnum>(), cases.ToArray());
        foreach (var value in cases)
        {
            var name = typeof(TEnum).GetField(value.ToString(), BindingFlags.Public | BindingFlags.Static)!.Name;
            Assert.Equal(char.ToLowerInvariant(name[0]) + name[1..], rawValue(value));
        }
    }
}
