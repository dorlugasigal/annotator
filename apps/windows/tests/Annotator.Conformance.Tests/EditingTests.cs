using System.Text.Json.Nodes;
using Annotator.Core;

namespace Annotator.Conformance.Tests;

/// <summary>
/// Section <c>editing</c>: scripted <c>CanvasDocument</c> sessions. Each step's result or error and the document after
/// it (elements with random IDs renamed, selection, generation, undo state, history bytes, presentation) must match.
/// Exact; numbers within 1e-9.
/// </summary>
public class EditingTests
{
    private static readonly NumberRule Numbers = NumberRule.Within(1e-9);

    private delegate JsonNode? Body(ref CanvasDocument document);

    private sealed class Script(CanvasDocument start)
    {
        private CanvasDocument document = start;

        public FreshIds Ids { get; } = new();

        public List<JsonObject> Records { get; } = [];

        public ref CanvasDocument Document => ref document;

        public void Step(string name, Body? body = null)
        {
            var record = new JsonObject { ["step"] = name };
            try
            {
                record["result"] = body is null ? null : body(ref document);
            }
            catch (Exception error) when (error is CanvasError or ShapeLibraryError or DecodingError)
            {
                record["error"] = Outcome.Describe(error);
            }
            foreach (var (key, value) in DocumentRecord.Of(document, Ids))
            {
                record[key] = value?.DeepClone();
            }
            Records.Add(record);
        }
    }

    private static readonly Element Rectangle = Fixture.Element(1, Tool.Rectangle, new Rect(40, 40, 120, 80));
    private static readonly Element Ellipse = Fixture.Element(2, Tool.Ellipse, new Rect(300, 60, 100, 60),
        style: Fixture.Styled(Tool.Ellipse, (ref Style style) => style.Fill = new RGBA(1, 0.82, 0, 0.4)));
    private static readonly Element Arrow = Fixture.Element(3, Tool.Arrow, points: [new Point(160, 80), new Point(300, 90)],
        configure: (ref Element element) =>
        {
            element.StartAnchor = new Anchor(Fixture.Id(1), new Point(1, 0.5));
            element.EndAnchor = new Anchor(Fixture.Id(2), new Point(0, 0.5));
        });

    [Fact]
    public void SessionMatchesTheMac() => Compare("editing/session.jsonl", Session());

    [Fact]
    public void HistoryLimitsMatchTheMac() => Compare("editing/history-limits.jsonl", HistoryLimits());

    [Fact]
    public void RefusalsMatchTheMac() => Compare("editing/refusals.jsonl", Refusals());

    private static void Compare(string path, List<JsonObject> actual)
    {
        var expected = Corpus.Lines(path);
        var differences = new List<string>();
        for (var index = 0; index < Math.Max(expected.Count, actual.Count); index++)
        {
            if (index >= expected.Count || index >= actual.Count)
            {
                differences.Add($"step {index}: {(index >= expected.Count ? "unexpected" : "missing")} {(index >= expected.Count ? actual[index] : expected[index])["step"]}");
                continue;
            }
            var found = JsonDiff.Differences(expected[index], actual[index], Numbers);
            differences.AddRange(found.Select(line => $"step {index} ({expected[index]["step"]}): {line}"));
        }
        Assert.True(differences.Count == 0, $"{path}: {differences.Count} difference(s)\n" + string.Join("\n", differences.Take(40)));
    }

    private static List<JsonObject> Session()
    {
        var script = new Script(new CanvasDocument());
        var pen = Fixture.Element(4, Tool.Pen, points: [new Point(60, 200)]);
        var cover = Fixture.Element(5, Tool.Cover, new Rect(400, 200, 60, 40));
        script.Step("start with an empty document");
        script.Step("add a rectangle", (ref CanvasDocument d) => { d.Add(Rectangle); return null; });
        script.Step("add an ellipse", (ref CanvasDocument d) => { d.Add(Ellipse); return null; });
        script.Step("add an arrow bound to both", (ref CanvasDocument d) => { d.Add(Arrow); return null; });
        script.Step("move the rectangle by (10, 20); the arrow start follows", (ref CanvasDocument d) =>
        {
            d.Update(Fixture.Id(1), (ref Element e) => e.Translate(new Point(10, 20)));
            return null;
        });
        script.Step("resize the ellipse; the arrow end follows", (ref CanvasDocument d) =>
        {
            d.Update(Fixture.Id(2), (ref Element e) => e.Resize(new Rect(320, 40, 140, 100)));
            return null;
        });
        script.Step("select the rectangle and the ellipse", (ref CanvasDocument d) => { d.Select([Fixture.Id(1), Fixture.Id(2)]); return null; });
        script.Step("group them", (ref CanvasDocument d) => { d.GroupSelection(); return null; });
        script.Step("select nothing by clicking empty space", (ref CanvasDocument d) => { d.Select(new Point(700, 500)); return null; });
        script.Step("click the ellipse, which selects its whole group", (ref CanvasDocument d) => { d.Select(new Point(390, 90)); return null; });
        script.Step("shift-click the arrow to add it", (ref CanvasDocument d) => { d.Select(new Point(240, 95), extending: true); return null; });
        script.Step("duplicate the selection", (ref CanvasDocument d) => { d.DuplicateSelection(); return null; });
        script.Step("send the copies to the back", (ref CanvasDocument d) => { d.ArrangeSelection(toFront: false); return null; });
        script.Step("lock the copies", (ref CanvasDocument d) => { d.SetSelectionLocked(true); return null; });
        script.Step("delete the selection; locked objects stay", (ref CanvasDocument d) => { d.DeleteSelection(); return null; });
        script.Step("unlock and delete the copies", (ref CanvasDocument d) =>
        {
            d.SetSelectionLocked(false);
            d.DeleteSelection();
            return null;
        });
        script.Step("undo", (ref CanvasDocument d) => { d.Undo(); return null; });
        script.Step("undo", (ref CanvasDocument d) => { d.Undo(); return null; });
        script.Step("redo", (ref CanvasDocument d) => { d.Redo(); return null; });
        script.Step("ungroup the selection", (ref CanvasDocument d) => { d.UngroupSelection(); return null; });
        script.Step("begin an edit and move the arrow", (ref CanvasDocument d) =>
        {
            d.Begin();
            d.Update(Fixture.Id(3), (ref Element e) => e.Translate(new Point(0, 50)));
            return null;
        });
        script.Step("cancel the edit", (ref CanvasDocument d) => { d.Cancel(); return null; });
        script.Step("begin an edit, recolor the selection and commit with an operation ID", (ref CanvasDocument d) =>
        {
            d.Begin();
            d.EditSelection((ref Element e) => e.Style.Color = Palette.Blue);
            d.Commit(Fixture.Id(800));
            return null;
        });
        script.Step("undo the operation", (ref CanvasDocument d) => { d.Undo(); return null; });
        script.Step("add a pen stroke with one point", (ref CanvasDocument d) => { d.Add(pen); return null; });
        script.Step("append three pen samples with pressure", (ref CanvasDocument d) =>
        {
            Point[] points = [new(70, 210), new(85, 205), new(100, 220)];
            for (var index = 0; index < points.Length; index++)
            {
                d.AppendSample(Fixture.Id(4), points[index], 0.25 * (index + 1));
            }
            return null;
        });
        script.Step("undo the last sample", (ref CanvasDocument d) => { d.Undo(); return null; });
        script.Step("append a sample after the undo", (ref CanvasDocument d) => { d.AppendSample(Fixture.Id(4), new Point(120, 230)); return null; });
        script.Step("nearest anchor near the rectangle's right edge", (ref CanvasDocument d) =>
            AnchorRecord(d.NearestAnchor(new Point(168, 112), Fixture.Id(3)), script.Ids));
        script.Step("nearest anchor just outside 18 points", (ref CanvasDocument d) =>
            AnchorRecord(d.NearestAnchor(new Point(170 + 18.5, 120), Fixture.Id(3)), script.Ids));
        script.Step("nearest anchor between two candidates", (ref CanvasDocument d) =>
            AnchorRecord(d.NearestAnchor(new Point(330, 85), Fixture.Id(3), threshold: 40), script.Ids));
        script.Step("add a cover", (ref CanvasDocument d) => { d.Add(cover); return null; });
        script.Step("select everything and delete without covers", (ref CanvasDocument d) =>
        {
            d.Select(d.Elements.Select(element => element.Id).ToArray());
            d.DeleteSelection(includingProtection: false);
            return null;
        });
        script.Step("undo", (ref CanvasDocument d) => { d.Undo(); return null; });
        script.Step("clear annotations; the cover stays", (ref CanvasDocument d) => { d.ClearAnnotations(); return null; });
        script.Step("undo", (ref CanvasDocument d) => { d.Undo(); return null; });
        script.Step("clear covers and effects", (ref CanvasDocument d) => { d.ClearProtection(); return null; });
        script.Step("show the lens at 3x", (ref CanvasDocument d) => { d.SetPresentation(new PresentationSettings(PresentationMode.Lens, 3)); return null; });
        script.Step("stop the lens without a history step", (ref CanvasDocument d) => { d.StopPresentationWithoutHistory(); return null; });
        script.Step("copy the rectangle and the arrow 40 points right; the copy keeps only its binding to the copied rectangle", (ref CanvasDocument d) =>
        {
            d.InsertCopies(d.Elements.Filter(element => element.Id == Fixture.Id(1) || element.Id == Fixture.Id(3)), new Point(40, 0));
            return null;
        });
        script.Step("clear all", (ref CanvasDocument d) => { d.ClearAll(); return null; });
        script.Step("undo", (ref CanvasDocument d) => { d.Undo(); return null; });

        var stroke = Fixture.Element(6, Tool.Pen, points: [new(500, 300), new(600, 300), new(600, 380), new(500, 380), new(500, 302)]);
        script.Step("add a pen stroke to recognize", (ref CanvasDocument d) => { d.Add(stroke); return null; });
        var generation = script.Document.Generation;
        var token = script.Document.Elements.First(element => element.Id == stroke.Id).GeometryToken;
        var shape = Fixture.Element(6, Tool.Rectangle, new Rect(500, 300, 100, 80));
        shape.Seed = stroke.Seed;
        script.Step("apply a recognized rectangle with a stale generation", (ref CanvasDocument d) =>
            d.ApplyRecognition(shape, unchecked(generation + 1), token));
        script.Step("apply a recognized rectangle with a stale geometry token", (ref CanvasDocument d) =>
            d.ApplyRecognition(shape, generation, Fixture.Id(7)));
        script.Step("apply the recognized rectangle", (ref CanvasDocument d) => d.ApplyRecognition(shape, generation, token));
        script.Step("undo after recognition", (ref CanvasDocument d) => { d.Undo(); return null; });
        return script.Records;
    }

    private static JsonNode? AnchorRecord(Anchor? anchor, FreshIds ids) => anchor is not { } value ? null : new JsonObject
    {
        ["elementID"] = SwiftText.UuidString(ids.Known(value.ElementId)),
        ["relative"] = Shapes.Json(value.Relative),
    };

    private static List<JsonObject> HistoryLimits()
    {
        var script = new Script(new CanvasDocument(historyByteLimit: 3_000, historyCountLimit: 3));
        for (var number = 1; number <= 6; number++)
        {
            var element = Fixture.Element(number, Tool.Rectangle, new Rect(number * 20, 0, 10, 10));
            script.Step($"add rectangle {number}", (ref CanvasDocument d) => { d.Add(element); return null; });
        }
        for (var index = 0; index < 4; index++)
        {
            script.Step("undo", (ref CanvasDocument d) => { d.Undo(); return null; });
        }
        for (var index = 0; index < 4; index++)
        {
            script.Step("redo", (ref CanvasDocument d) => { d.Redo(); return null; });
        }
        script.Step("add a stroke with 200 points, larger than the history budget", (ref CanvasDocument d) =>
        {
            d.Add(Fixture.Element(7, Tool.Pen, points: Enumerable.Range(0, 200).Select(index => new Point(index, 50)).ToArray()));
            return null;
        });
        script.Step("undo", (ref CanvasDocument d) => { d.Undo(); return null; });
        return script.Records;
    }

    private static List<JsonObject> Refusals()
    {
        var script = new Script(new CanvasDocument([Rectangle, Ellipse, Arrow]));
        script.Step("start with three objects");
        script.Step("commit without an edit", (ref CanvasDocument d) => { d.Commit(); return null; });
        script.Step("update a missing object", (ref CanvasDocument d) => { d.Update(Fixture.Id(99), (ref Element e) => e.Rotation = 1); return null; });
        script.Step("update to a width of zero", (ref CanvasDocument d) => { d.Update(Fixture.Id(1), (ref Element e) => e.Style.Width = 0); return null; });
        script.Step("change an object's ID", (ref CanvasDocument d) => { d.Update(Fixture.Id(1), (ref Element e) => e.Id = Fixture.Id(98)); return null; });
        script.Step("add an object with an existing ID", (ref CanvasDocument d) => { d.Add(Rectangle); return null; });
        script.Step("append a sample to a rectangle", (ref CanvasDocument d) => { d.AppendSample(Fixture.Id(1), new Point(0, 0)); return null; });
        script.Step("append a sample beyond the coordinate limit", (ref CanvasDocument d) =>
        {
            d.Add(Fixture.Element(8, Tool.Pen, points: [new Point(0, 0)]));
            d.AppendSample(Fixture.Id(8), new Point(0, 10_000_001));
            return null;
        });
        script.Step("append a sample with pressure above one", (ref CanvasDocument d) => { d.AppendSample(Fixture.Id(8), new Point(1, 1), 1.5); return null; });
        script.Step("zoom the lens to 17x", (ref CanvasDocument d) => { d.SetPresentation(new PresentationSettings(PresentationMode.Lens, 17)); return null; });
        script.Step("lock the rectangle, then try to move it", (ref CanvasDocument d) =>
        {
            d.Select([Fixture.Id(1)]);
            d.SetSelectionLocked(true);
            d.Update(Fixture.Id(1), (ref Element e) => e.Translate(new Point(5, 5)));
            return null;
        });
        script.Step("rotate a cover", (ref CanvasDocument d) =>
        {
            d.Add(Fixture.Element(9, Tool.Cover, new Rect(0, 0, 10, 10)));
            d.Update(Fixture.Id(9), (ref Element e) => e.Rotation = 0.5);
            return null;
        });
        var records = script.Records;
        foreach (var (name, bytes, count) in new[] { ("a history byte limit of zero", 0L, 100), ("a history count limit of zero", 1_000L, 0) })
        {
            records.Add(new JsonObject
            {
                ["result"] = Outcome.Of(() => { _ = new CanvasDocument(historyByteLimit: bytes, historyCountLimit: count); return null; }),
                ["step"] = $"create a document with {name}",
            });
        }
        records.Add(new JsonObject
        {
            ["result"] = Outcome.Of(() => { _ = new CanvasDocument([Rectangle, Rectangle]); return null; }),
            ["step"] = "create a document with two objects that share an ID",
        });
        return records;
    }
}
