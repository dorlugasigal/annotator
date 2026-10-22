using System.Text.Json.Nodes;
using Annotator.Core;
using Annotator.Workspace.Library;

namespace Annotator.Workspace.Tests.Library;

/// <summary>
/// The Tools window's sections, tiles and sizes against ToolLibrary.swift and the UX reference in
/// reference/ux-reference/macos/tools-window (WIN-UI-05, WIN-PAR-06): frames within 1 point, words and order exact.
/// </summary>
public sealed class ToolLibraryTests
{
    private const double Tolerance = 1;

    [Fact]
    public void SectionsKeepTheMacOrderAndTitles()
    {
        Assert.Equal(["Draw", "Shapes", "Focus", "Extras", "My shapes"], ToolLibrarySection.AllCases.Select(section => section.RawValue));
        Assert.Equal(25, ToolLibrarySection.Shapes.Tools.Length);
        Assert.Equal(ToolLibrarySection.Focus, ToolLibrarySection.Containing(Tool.Pixelate));
        Assert.Equal(ToolLibrarySection.Shapes, ToolLibrarySection.Containing(Tool.Bell));
        Assert.Equal(ToolLibrarySection.Extras, ToolLibrarySection.Containing(Tool.Counter));
        Assert.Equal(ToolLibrarySection.Draw, ToolLibrarySection.Containing(Tool.Eraser));
    }

    [Theory]
    [InlineData("section-draw", ToolLibrarySection.Draw)]
    [InlineData("section-shapes", ToolLibrarySection.Shapes)]
    [InlineData("section-focus", ToolLibrarySection.Focus)]
    [InlineData("section-extras", ToolLibrarySection.Extras)]
    public void TilesHaveTheReferenceLabelsAndHelpInOrder(string state, ToolLibrarySection section)
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        string[] titles = [.. ToolLibraryTiles.For(section, model.Snapshot).Select(tile => tile.Title)];
        JsonNode[] buttons = Tiles(Reference(state));
        // The Mac's lazy grid leaves its last, unscrolled tile out of the tree (Axes in Shapes).
        Assert.Equal(buttons.Select(node => (string)node["label"]!), titles.Take(buttons.Length));
        Assert.All(buttons, node => Assert.Equal((string)node["label"]!, (string)node["help"]!));
        if (section == ToolLibrarySection.Shapes)
        {
            Assert.Equal("Axes", titles[^1]);
        }
    }

    [Fact]
    public void TilesShowTheMacKeysWithWindowsLabels()
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        Dictionary<string, string?> draw = ToolLibraryTiles.For(ToolLibrarySection.Draw, model.Snapshot).ToDictionary(tile => tile.Title, tile => tile.Shortcut);
        Assert.Equal("1", draw["Select"]);
        Assert.Equal("7", draw["Pen"]);
        Assert.Equal("0", draw["Eraser"]);
        Dictionary<string, string?> focus = ToolLibraryTiles.For(ToolLibrarySection.Focus, model.Snapshot).ToDictionary(tile => tile.Title, tile => tile.Shortcut);
        Assert.Equal("S", focus["Effects"]);
        Assert.Equal("Ctrl+1", focus["Screen zoom"]);
        Assert.Null(focus["Spotlight"]);
        Dictionary<string, string?> shapes = ToolLibraryTiles.For(ToolLibrarySection.Shapes, model.Snapshot).ToDictionary(tile => tile.Title, tile => tile.Shortcut);
        Assert.Equal("H", shapes["Person"]);
        Assert.Equal("D", shapes["Cylinder"]);
        Assert.Null(shapes["Triangle"]);
    }

    [Fact]
    public void FeaturesThatAreNotBuiltStayUnavailable()
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        WorkspaceSnapshot snapshot = model.Snapshot;
        ToolLibraryTile[] focus = [.. ToolLibraryTiles.For(ToolLibrarySection.Focus, snapshot)];
        Assert.Equal("Effects", focus[0].Title);
        Assert.Equal(WorkspaceModel.IsAvailable(ToolLibraryTiles.PreferredEffect(snapshot.Preferences)), focus[0].Available);
        // The app shows live effects, so Effects can be chosen, as on the Mac.
        Assert.True(focus[0].Available);
        Assert.Equal(["Pointer lens", "Screen zoom", "Spotlight", "Cursor halo", "Share region"], focus.Skip(1).Select(tile => tile.Title));
        // The pointer lens, screen zoom, Spotlight, Cursor halo and region sharing are built.
        Assert.All(focus.Skip(1), tile => Assert.True(tile.Available));
        Assert.All(focus, tile => Assert.False(tile.Selected));
        ToolLibraryTile[] extras = [.. ToolLibraryTiles.For(ToolLibrarySection.Extras, snapshot)];
        Assert.Equal(["Paste image", Tool.Ruler.Label, Tool.Counter.Label, "Insert image"], extras.Select(tile => tile.Title));
        // Paste image and Insert image are built: they paste the clipboard's image and open an image file.
        Assert.All(extras, tile => Assert.True(tile.Available));
        Assert.Equal(ToolLibraryAction.InsertImage, extras[3].Action);
        Assert.All(ToolLibraryTiles.For(ToolLibrarySection.Shapes, snapshot), tile => Assert.True(tile.Available));
    }

    [Fact]
    public void TextSaysWhyItIsUnavailableWithoutAnEditor()
    {
        var model = new WorkspaceModel(new WorkspaceOptions
        {
            Displays = () => [TestWorkspace.PrimaryDisplay],
            TextEditingAvailable = false,
        });
        ToolLibraryTile text = ToolLibraryTiles.For(ToolLibrarySection.Draw, model.Snapshot).Single(tile => tile.Tool == Tool.Text);
        Assert.False(text.Available);
        Assert.Equal(WorkspaceModel.TextUnavailableMessage, text.Explanation);
    }

    [Fact]
    public void TheCurrentToolIsSelected()
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        model.Choose(Tool.Triangle);
        ToolLibraryTile[] shapes = [.. ToolLibraryTiles.For(ToolLibrarySection.Shapes, model.Snapshot)];
        Assert.Equal(["Triangle"], shapes.Where(tile => tile.Selected).Select(tile => tile.Title));
        Assert.DoesNotContain(ToolLibraryTiles.For(ToolLibrarySection.Draw, model.Snapshot), tile => tile.Selected);

        // Effects is current while an effect is the tool, as in the Mac's section-focus capture.
        model.Choose(Tool.Pixelate);
        ToolLibraryTile effects = ToolLibraryTiles.For(ToolLibrarySection.Focus, model.Snapshot)[0];
        Assert.Equal("Effects", effects.Title);
        Assert.True(effects.Selected);
        Assert.Equal(Tool.Pixelate, ToolLibraryTiles.PreferredEffect(model.Snapshot.Preferences));
    }

    [Theory]
    [InlineData("section-draw", ToolLibrarySection.Draw, 368, 420)]
    [InlineData("section-shapes", ToolLibrarySection.Shapes, 368, 420)]
    [InlineData("section-focus", ToolLibrarySection.Focus, 368, 420)]
    [InlineData("section-extras", ToolLibrarySection.Extras, 368, 420)]
    [InlineData("section-my-shapes", ToolLibrarySection.MyShapes, 368, 420)]
    [InlineData("shapes-resized", ToolLibrarySection.Shapes, 780, 570)]
    public void PanelSizesMatchTheReference(string state, ToolLibrarySection section, double width, double maximumHeight)
    {
        JsonObject reference = Reference(state);
        double viewportWidth = ToolLibraryLayout.ViewportWidth(width);
        double viewportHeight = ToolLibraryLayout.ViewportHeight(maximumHeight, section);
        double shown = section == ToolLibrarySection.MyShapes ? viewportHeight : ToolLibraryLayout.GridHeight(section, viewportWidth, viewportHeight);
        Assert.Equal((double)reference["size"]![1]!, ToolLibraryLayout.IdealHeight(section, shown), Tolerance);
        Assert.Equal((double)reference["size"]![0]!, width, Tolerance);
        JsonNode scroll = Role(Accessibility(reference), "AXScrollArea");
        AssertFrame(scroll, ToolLibraryLayout.Padding, 84, viewportWidth, shown);
    }

    [Theory]
    [InlineData("section-draw", 368)]
    [InlineData("shapes-resized", 780)]
    public void SegmentsMatchTheReference(string state, double width)
    {
        JsonNode picker = Role(Accessibility(Reference(state)), "AXRadioGroup");
        Assert.Equal("Tool category", (string)picker["label"]!);
        JsonNode[] segments = Children(picker);
        var expected = ToolLibraryLayout.Segments(ToolLibraryLayout.ViewportWidth(width));
        Assert.Equal(segments.Length, expected.Length);
        for (int index = 0; index < segments.Length; index++)
        {
            Assert.Equal(ToolLibrarySection.AllCases[index].RawValue, (string)segments[index]["label"]!);
            AssertFrame(segments[index], expected[index].X, 48, expected[index].Width, ToolLibraryLayout.PickerHeight);
        }
        AssertFrame(picker, expected[0].X, 48, expected[^1].X + expected[^1].Width - expected[0].X, ToolLibraryLayout.PickerHeight);
    }

    [Fact]
    public void SegmentsFillTheWidthBetweenNaturalAndEqualSizes()
    {
        var segments = ToolLibraryLayout.Segments(408);
        Assert.Equal(410, segments.Sum(segment => segment.Width), 6);
        Assert.Equal(15, segments[0].X, 6);
        Assert.True(segments.Zip(ToolLibraryLayout.NaturalSegmentWidths).All(pair => pair.First.Width >= pair.Second - 1e-9));
        Assert.Equal(89.5, segments[^1].Width, 6);
    }

    [Theory]
    [InlineData("section-draw", ToolLibrarySection.Draw, 368, 420)]
    [InlineData("section-focus", ToolLibrarySection.Focus, 368, 420)]
    [InlineData("section-extras", ToolLibrarySection.Extras, 368, 420)]
    [InlineData("shapes-resized", ToolLibrarySection.Shapes, 780, 570)]
    [InlineData("section-shapes", ToolLibrarySection.Shapes, 368, 420)]
    public void TilesSitWhereTheReferenceShowsThem(string state, ToolLibrarySection section, double width, double maximumHeight)
    {
        WorkspaceModel model = TestWorkspace.Drawing();
        int count = ToolLibraryTiles.For(section, model.Snapshot).Length;
        double viewportWidth = ToolLibraryLayout.ViewportWidth(width);
        double gridHeight = ToolLibraryLayout.GridHeight(section, viewportWidth, ToolLibraryLayout.ViewportHeight(maximumHeight, section));
        GridPlan plan = ToolLibraryLayout.Grid(count, viewportWidth, gridHeight);
        JsonNode grid = Children(Role(Accessibility(Reference(state)), "AXScrollArea"))[0];
        JsonNode[] tiles = Tiles(Reference(state));
        // Off-screen tiles of the Mac's lazy grid report a placeholder frame, so only the rows on screen are compared.
        int shown = Math.Min(tiles.Length, (int)Math.Ceiling(gridHeight / (ToolLibraryLayout.TileHeight + ToolLibraryLayout.TileSpacing)) * plan.Columns.Count);
        for (int index = 0; index < shown; index++)
        {
            int row = index / plan.Columns.Count, column = index % plan.Columns.Count;
            AssertFrame(tiles[index], ToolLibraryLayout.Padding + plan.Columns.X(column), 84 + row * 84, plan.Columns.Width, ToolLibraryLayout.TileHeight);
        }
        double[] frame = Frame(grid);
        Assert.Equal(plan.GridWidth, frame[2], Tolerance);
        if (section != ToolLibrarySection.Shapes || width > 368)
        {
            Assert.Equal(plan.ContentHeight, frame[3], Tolerance);
        }
        Assert.Equal(section == ToolLibrarySection.Shapes && width == 368, plan.Scrolls);
    }

    private static JsonObject Reference(string state) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(UXReference.Folder("tools-window"), $"{state}-light.json")))!.AsObject();

    private static JsonNode[] Accessibility(JsonObject reference) => [.. reference["accessibility"]!.AsArray().Select(node => node!)];

    private static JsonNode[] Children(JsonNode node) => [.. node["children"]!.AsArray().Select(child => child!)];

    private static JsonNode Role(JsonNode[] nodes, string role) => nodes.Single(node => (string?)node["role"] == role);

    private static JsonNode[] Tiles(JsonObject reference)
    {
        JsonNode scroll = Role(Accessibility(reference), "AXScrollArea");
        return Children(Role(Children(scroll), "AXOpaqueProviderGroup"));
    }

    private static double[] Frame(JsonNode node) => [.. node["frame"]!.AsArray().Select(value => (double)value!)];

    private static void AssertFrame(JsonNode node, double x, double y, double width, double height)
    {
        double[] frame = Frame(node);
        string message = $"{node["label"] ?? node["role"]}: [{string.Join(", ", frame)}] against [{x}, {y}, {width}, {height}]";
        Assert.True(Math.Abs(frame[0] - x) <= Tolerance && Math.Abs(frame[1] - y) <= Tolerance &&
                    Math.Abs(frame[2] - width) <= Tolerance && Math.Abs(frame[3] - height) <= Tolerance, message);
    }
}
