using System.Text.Json.Nodes;
using Annotator.Core;

namespace Annotator.Conformance.Tests;

/// <summary>The exporter's <c>documentRecord</c> (apps/macos/Tools/Conformance/Support.swift): the observable state of a document.</summary>
internal static class DocumentRecord
{
    public static JsonObject Of(in CanvasDocument document, FreshIds ids)
    {
        var elements = new JsonArray();
        foreach (var element in document.Elements)
        {
            elements.Add(Outcome.Value(ids.Rename(element)));
        }
        var record = new JsonObject
        {
            ["elements"] = elements,
            ["selection"] = ids.Set(document.Selection),
            ["generation"] = document.Generation,
            ["canUndo"] = document.CanUndo,
            ["canRedo"] = document.CanRedo,
            ["isEditing"] = document.IsEditing,
            ["hasPendingChanges"] = document.HasPendingChanges,
            ["hasProtection"] = document.HasProtection,
            ["historyBytes"] = document.HistoryBytes,
            ["presentation"] = new JsonObject { ["mode"] = document.Presentation.Mode.RawValue, ["zoom"] = document.Presentation.Zoom },
        };
        record["undoOperationID"] = document.UndoOperationId is { } undo ? SwiftText.UuidString(ids[undo]) : null;
        record["redoOperationID"] = document.RedoOperationId is { } redo ? SwiftText.UuidString(ids[redo]) : null;
        return record;
    }
}

/// <summary>
/// Section <c>library</c>: saved-shape files and inserting them, creation and naming rules, invalid files and a scripted
/// store session. Exact; numbers within 1e-9.
/// </summary>
public class LibraryTests
{
    private static readonly NumberRule Numbers = NumberRule.Within(1e-9);

    public static TheoryData<string> ValidCases => CorpusCases.Of("library/valid/index.jsonl");

    [Theory]
    [MemberData(nameof(ValidCases))]
    public void SavedShapesReadAndInsertAsOnTheMac(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name), () =>
        {
            var data = Corpus.Bytes("library/valid/" + record["file"]!.GetValue<string>());
            var shape = SavedShape.Read(data);
            var basis = new CanvasDocument([Fixture.Element(500, Tool.Ellipse, new Rect(0, 0, 40, 40))]);
            var fresh = new FreshIds();
            var actual = record.DeepClone().AsObject();
            actual["bounds"] = Shapes.Json(shape.Bounds);
            actual["drawingSchema"] = shape.Drawing.SchemaVersion;
            actual["elementCount"] = shape.Elements.Count;
            actual["estimatedBytes"] = shape.EstimatedBytes;
            actual["id"] = SwiftText.UuidString(shape.Id);
            actual["insertedAt300x200"] = Outcome.Of(() => DocumentRecord.Of(shape.Adding(basis, new Point(300, 200)), fresh));
            actual["shapeName"] = shape.Name;
            actual["writtenEqualsInput"] = shape.Data().AsSpan().SequenceEqual(data);
            JsonDiff.Equal(record, actual, Numbers);
        });
    }

    public static TheoryData<string> InvalidCases => CorpusCases.Of("library/invalid/index.jsonl");

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void InvalidSavedShapesFailWithTheMacMessage(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name), () =>
        {
            var data = record["file"] is { } file
                ? Corpus.Bytes("library/invalid/" + file.GetValue<string>())
                : Enumerable.Repeat((byte)0x20, 8 * 1_024 * 1_024 + 1).ToArray();
            JsonDiff.Equal(record["read"], Outcome.Of(() => SavedShape.Read(data).Name), NumberRule.Exact, "read");
        });
    }

    public static TheoryData<string> CreationCases => CorpusCases.Of("library/creation.jsonl", record =>
        record["case"]!.GetValue<string>() + (record["input"] is { } input ? " " + input.ToJsonString() : ""));

    [Theory]
    [MemberData(nameof(CreationCases))]
    public void SavingAndNamingFollowTheMacRules(string name)
    {
        var record = CorpusCases.Record(name);
        OpenDifferences.Check(CorpusCases.Id(name), () =>
        {
            var input = record["input"]?.GetValue<string>();
            var result = record["case"]!.GetValue<string>() switch
            {
                "name" => Make(input!, [1]),
                "nothing selected" => Make("Shape", []),
                "unfinished edit" => Make("Shape", [1], editing: true),
                "cover selected" => Make("Shape", [1, 60], [.. Samples.Basic(), Fixture.Element(60, Tool.Cover, new Rect(0, 0, 10, 10))]),
                "rename" => Outcome.Of(() => Original().Renamed(input!).Name),
                "insert into a document with an unfinished edit" => Insert(Editing(), new Point(0, 0)),
                "insert at a center that is not finite" => Insert(new CanvasDocument(), new Point(double.PositiveInfinity, 0)),
                "insert at a center beyond the coordinate limit" => Insert(new CanvasDocument(), new Point(10_000_001, 0)),
                "insert when the undo history budget is too small" => Insert(new CanvasDocument(Samples.Basic(), historyByteLimit: 6_000), new Point(0, 0)),
                var other => throw new InvalidDataException($"Unknown case {other}."),
            };
            JsonDiff.Equal(record["result"], result, NumberRule.Exact, "result");
        });

        static JsonObject Make(string name, int[] select, ElementList? elements = null, bool editing = false) => Outcome.Of(() =>
        {
            var document = new CanvasDocument(elements ?? Samples.Basic());
            document.Select(select.Select(number => Fixture.Id(number)));
            if (editing)
            {
                document.Begin();
            }
            var shape = new SavedShape(name, document);
            return new JsonObject { ["elementCount"] = shape.Elements.Count, ["name"] = shape.Name };
        });

        static JsonObject Insert(CanvasDocument document, Point center) => Outcome.Of(() => Original().Adding(document, center).Elements.Count);

        static CanvasDocument Editing()
        {
            var document = new CanvasDocument(Samples.Basic());
            document.Begin();
            return document;
        }
    }

    /// <summary>The exporter's "Original" shape: the basic rectangle saved under fixture ID 310.</summary>
    private static SavedShape Original() => SavedShape.Read(File(310, "Original", Samples.Basic(), [1]));

    /// <summary>The exporter's <c>file</c>: a saved shape written by the app, with its random ID replaced by a fixture ID.</summary>
    internal static byte[] File(long number, string name, ElementList elements, int[] selecting)
    {
        var document = new CanvasDocument(elements);
        document.Select(selecting.Select(index => Fixture.Id(index)));
        var shape = new SavedShape(name, document);
        var text = System.Text.Encoding.UTF8.GetString(shape.Data());
        return System.Text.Encoding.UTF8.GetBytes(text.Replace(SwiftText.UuidString(shape.Id), SwiftText.UuidString(Fixture.Id(number)), StringComparison.Ordinal));
    }

    [Fact]
    public void LimitsMatchTheMac()
    {
        var limits = Corpus.Json("library/limits.json");
        Assert.Equal(ShapeLibraryStore.MaximumBytes, limits["libraryMaximumBytes"]!.GetValue<int>());
        Assert.Equal(ShapeLibraryStore.MaximumEntries, limits["libraryMaximumEntries"]!.GetValue<int>());
        Assert.Equal(SavedShape.MaximumBytes, limits["savedShapeMaximumBytes"]!.GetValue<int>());
        Assert.Equal(SavedShape.CurrentSchema, limits["savedShapeSchema"]!.GetValue<int>());
    }

    /// <summary>
    /// The exporter's scripted <c>ShapeLibraryStore</c> session in a temporary folder. The two steps with symbolic links
    /// run only where this account may create links (Developer Mode or administrator rights on Windows).
    /// </summary>
    [Fact]
    public async Task StoreScriptMatchesTheMac()
    {
        var expected = Corpus.Lines("library/store-script.jsonl");
        string[] names = ["arrow-between-boxes", "detached-connector", "grouped-label", "gradient-pen", "trimmed-name", "single-rectangle"];
        var files = names.Select(file => Corpus.Bytes($"library/valid/{file}.json")).ToArray();
        var shapes = files.Select(data => SavedShape.Read(data)).ToArray();
        var root = Path.Combine(Path.GetTempPath(), $"annotator-conformance-{Environment.ProcessId}-{Guid.NewGuid():N}");
        var folder = Path.Combine(root, "shapes");
        var store = new ShapeLibraryStore(folder);
        var actual = new List<JsonObject>();
        var skippedLinks = false;
        static JsonArray List(IReadOnlyList<SavedShape> result) =>
            new(result.Select(shape => (JsonNode)new JsonArray(shape.Name, SwiftText.UuidString(shape.Id))).ToArray());
        async Task Step(string name, Func<Task<IReadOnlyList<SavedShape>>> body)
        {
            JsonObject result;
            try
            {
                result = new JsonObject { ["ok"] = List(await body()) };
            }
            catch (Exception error) when (error is CanvasError or ShapeLibraryError or DecodingError)
            {
                result = new JsonObject { ["error"] = Outcome.Describe(error) };
            }
            actual.Add(new JsonObject { ["result"] = result, ["step"] = name });
        }
        try
        {
            await Step("load a folder that does not exist", store.LoadAsync);
            await Step("save Arrow between boxes", () => store.SaveAsync(shapes[0]));
            await Step("save Grouped label", () => store.SaveAsync(shapes[2]));
            await Step("save Single rectangle", () => store.SaveAsync(shapes[5]));
            await Step("save Arrow between boxes again", () => store.SaveAsync(shapes[0]));
            await Step("rename Grouped label to Alpha", () => store.RenameAsync(shapes[2].Id, "Alpha"));
            await Step("rename Single rectangle to an empty name", () => store.RenameAsync(shapes[5].Id, ""));
            await Step("rename a shape that is not saved", () => store.RenameAsync(Fixture.Id(399), "Missing"));
            await Step("delete Arrow between boxes", () => store.DeleteAsync(shapes[0].Id));
            await Step("delete it again", () => store.DeleteAsync(shapes[0].Id));
            const string Lettered = "ABCDEF01-0000-4000-8000-000000000302";
            var detached = System.Text.Encoding.UTF8.GetString(files[1]);
            (string Name, string FileName, byte[] Data)[] extra =
            [
                ("a file whose name is not an ID", "not-an-id.json", files[1]),
                ("a file whose name is a lowercase ID", Lettered.ToLowerInvariant() + ".json",
                    System.Text.Encoding.UTF8.GetBytes(detached.Replace(SwiftText.UuidString(Fixture.Id(302)), Lettered, StringComparison.Ordinal))),
                ("a file whose name is another shape's ID", SwiftText.UuidString(Fixture.Id(377)) + ".json", files[1]),
                ("a file that is not a saved shape", SwiftText.UuidString(Fixture.Id(378)) + ".json", "{}"u8.ToArray()),
            ];
            foreach (var (name, fileName, data) in extra)
            {
                var path = Path.Combine(folder, fileName);
                await System.IO.File.WriteAllBytesAsync(path, data, TestContext.Current.CancellationToken);
                await Step($"load with {name}", store.LoadAsync);
                System.IO.File.Delete(path);
            }
            await System.IO.File.WriteAllBytesAsync(Path.Combine(folder, ".hidden.json"), files[3], TestContext.Current.CancellationToken);
            await System.IO.File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "notes", TestContext.Current.CancellationToken);
            await Step("load with a hidden file and a text file, which are ignored", store.LoadAsync);
            var outside = Path.Combine(root, "outside.json");
            await System.IO.File.WriteAllBytesAsync(outside, files[3], TestContext.Current.CancellationToken);
            var link = Path.Combine(folder, SwiftText.UuidString(Fixture.Id(304)) + ".json");
            if (TryLink(() => System.IO.File.CreateSymbolicLink(link, outside)))
            {
                await Step("load with a saved shape that is a link", store.LoadAsync);
                System.IO.File.Delete(link);
                var linkedFolder = Path.Combine(root, "linked");
                if (TryLink(() => Directory.CreateSymbolicLink(linkedFolder, folder)))
                {
                    await Step("load a library folder that is a link", () => new ShapeLibraryStore(linkedFolder).LoadAsync());
                }
                else
                {
                    skippedLinks = true;
                }
            }
            else
            {
                skippedLinks = true;
            }
            var memory = new ShapeLibraryStore();
            var template = System.Text.Encoding.UTF8.GetString(files[5]);
            IReadOnlyList<SavedShape> full = [];
            for (var index = 0; index < ShapeLibraryStore.MaximumEntries; index++)
            {
                var data = System.Text.Encoding.UTF8.GetBytes(template.Replace(SwiftText.UuidString(Fixture.Id(306)),
                    SwiftText.UuidString(Fixture.Id(10_000 + index)), StringComparison.Ordinal));
                full = await memory.SaveAsync(SavedShape.Read(data));
            }
            actual.Add(new JsonObject
            {
                ["result"] = new JsonObject { ["ok"] = new JsonObject { ["count"] = full.Count } },
                ["step"] = "save 256 shapes in a library kept in memory",
            });
            await Step("save a 257th shape", () => memory.SaveAsync(shapes[3]));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        var compared = skippedLinks
            ? expected.Where(record => !record["step"]!.GetValue<string>().Contains("link", StringComparison.Ordinal)).ToList()
            : [.. expected];
        Assert.Equal(compared.Count, actual.Count);
        for (var index = 0; index < compared.Count; index++)
        {
            JsonDiff.Equal(compared[index], actual[index], NumberRule.Exact, $"step {index} ({compared[index]["step"]})");
        }
        if (skippedLinks)
        {
            Assert.Skip("Every other step matches. The two steps with symbolic links need Developer Mode or administrator rights to create links on this machine.");
        }
    }

    private static bool TryLink(Action create)
    {
        try
        {
            create();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Elements shared by several corpus sections (apps/macos/Tools/Conformance/Support.swift, <c>Samples</c>).</summary>
internal static class Samples
{
    public static Point[] Wave(Point start, int count, double step, double amplitude) =>
        Enumerable.Range(0, count).Select(index => new Point(start.X + index * step, start.Y + amplitude * Math.Sin(index * 0.45))).ToArray();

    /// <summary>Common tools that need board format 1, with an arrow bound to two shapes.</summary>
    public static ElementList Basic()
    {
        var blue = new RGBA(0.17, 0.45, 0.88);
        return
        [
            Fixture.Element(1, Tool.Rectangle, new Rect(40, 40, 160, 100), style: Fixture.Styled(Tool.Rectangle, (ref Style style) =>
            {
                style.Color = blue;
                style.CornerRadius = 8;
                style.Fill = new RGBA(0.17, 0.45, 0.88, 0.4);
                style.FillPattern = FillPattern.Hatch;
            })),
            Fixture.Element(2, Tool.Ellipse, new Rect(320, 60, 140, 90), style: Fixture.Styled(Tool.Ellipse, (ref Style style) =>
            {
                style.Opacity = 0.8;
                style.Fill = new RGBA(0.96, 0.89, 0.22, 0.4);
            })),
            Fixture.Element(3, Tool.Arrow, points: [new Point(200, 90), new Point(320, 105)], configure: (ref Element element) =>
            {
                element.StartAnchor = new Anchor(Fixture.Id(1), new Point(1, 0.5));
                element.EndAnchor = new Anchor(Fixture.Id(2), new Point(0, 0.5));
            }),
            Fixture.Element(4, Tool.Pen, points: Wave(new Point(40, 200), 24, 8, 14), style: Fixture.Styled(Tool.Pen, (ref Style style) =>
            {
                style.Pressure = true;
                style.Width = 5;
            }), configure: (ref Element element) =>
                element.Pressures = ValueList.Create<double>(Enumerable.Range(0, 24).Select(index => 0.25 + 0.75 * Math.Abs(Math.Sin(index * 0.3))).ToArray())),
            Fixture.Element(5, Tool.Highlighter, points: [new Point(260, 200), new Point(330, 204), new Point(400, 198)]),
            Fixture.Element(6, Tool.Line, points: [new Point(40, 260), new Point(240, 300)], style: Fixture.Styled(Tool.Line, (ref Style style) =>
            {
                style.Pattern = StrokePattern.Dashed;
                style.Color = Palette.Ink;
            })),
            Fixture.Element(7, Tool.Text, new Rect(280, 240, 180, 40), text: "Hello, board", style: Fixture.Styled(Tool.Text, (ref Style style) =>
            {
                style.Color = Palette.Ink;
                style.FontSize = 24;
            })),
            Fixture.Element(8, Tool.Counter, new Rect(480, 40, 36, 36), text: "1"),
            Fixture.Element(9, Tool.Ruler, points: [new Point(40, 340), new Point(160, 340)], text: "120 px"),
            Fixture.Element(10, Tool.Diamond, new Rect(480, 120, 100, 80), style: Fixture.Styled(Tool.Diamond, (ref Style style) => style.CornerRadius = 10)),
        ];
    }
}
