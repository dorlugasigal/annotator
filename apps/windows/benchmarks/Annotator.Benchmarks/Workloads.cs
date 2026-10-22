using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Displays;
using Annotator.Platform.Graphics;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Text;
using static Annotator.Benchmarks.Measurement;

namespace Annotator.Benchmarks;

/// <summary>
/// The workloads of apps/macos/Sources/PerformanceBenchmarks/Workloads.swift with the same names, fixtures and checks, followed by
/// the Windows overlay presenter's frame work (<see cref="PresenterWorkloads"/>). Document workloads run Annotator.Core;
/// renderer workloads draw with Direct2D on the hardware device, as the overlay does; exports run on WARP, as the app's
/// Export PNG does.
/// </summary>
internal static class Workloads
{
    public static readonly string[] MacScenarios =
    [
        "document.add.1000", "document.add.5000", "document.select-hit-miss.1000", "document.select-hit-miss.5000",
        "document.move-100.history-0", "document.move-100.history-80", "document.undo.history-80", "document.append.200000",
        "document.roundtrip.1000-200000",
        "renderer.standard.cold", "renderer.standard.cached", "renderer.gradients-200.cold", "renderer.gradients-200.cached",
        "renderer.stress.cold", "renderer.stress.cached", "renderer.rough-256.cold", "renderer.rough-256.cached",
        "renderer.rough-1000.cold", "renderer.rough-1000.cached", "renderer.rough-wide-32.cold", "renderer.rough-wide-32.cached",
        "renderer.images-64.cold", "renderer.images-64.cached", "renderer.text-1000.cold", "renderer.text-1000.cached",
        "renderer.pressure-200000.cold", "renderer.pressure-200000.cached", "renderer.pressure-200000.clipped.cached",
        "image.decode.1920x1080", "image.png-encode.1920x1080", "export.source-only.png", "export.annotated-images.png",
        "export.synthetic-effects.png", "memory.owned-cleanup-idle",
    ];

    public static IEnumerable<string> All => MacScenarios.Concat(PresenterWorkloads.Scenarios);

    public static bool IsKnown(string scenario) => All.Contains(scenario, StringComparer.Ordinal);

    /// <summary>How many samples a worker returns for <paramref name="scenario"/> (presenter workloads measure more frames).</summary>
    public static int SampleCount(string scenario, int samples) => PresenterWorkloads.IsPresenter(scenario) ? PresenterWorkloads.Frames(scenario, samples) : samples;

    public static Result Run(string scenario, int samples, int warmup) => scenario switch
    {
        "document.append.200000" => Append(scenario, samples, warmup),
        "document.roundtrip.1000-200000" => Roundtrip(scenario, samples, warmup),
        "memory.owned-cleanup-idle" => Cleanup(scenario, samples, warmup),
        _ when scenario.StartsWith("document.", StringComparison.Ordinal) => Document(scenario, samples, warmup),
        _ when scenario.StartsWith("renderer.", StringComparison.Ordinal) => Renderer(scenario, samples, warmup),
        _ when scenario.StartsWith("image.", StringComparison.Ordinal) || scenario.StartsWith("export.", StringComparison.Ordinal) => Image(scenario, samples, warmup),
        _ when PresenterWorkloads.IsPresenter(scenario) => PresenterWorkloads.Run(scenario, samples, warmup),
        _ => throw new BenchmarkFailure($"Unknown scenario {scenario}."),
    };

    private static Result Append(string scenario, int samples, int warmup)
    {
        var points = Fixtures.Stroke(200_000).Points;
        return Measure(scenario, Workload(("appendedPoints", points.Count), ("transactions", 1)),
                       "Begin, add empty stroke, append every point, commit; input fixture excluded.", samples, warmup, () =>
        {
            var document = CanvasDocument.Empty();
            var stroke = new Element(Tool.Pen, id: Fixtures.Id(900_000));
            return new Job(() =>
            {
                document.Begin();
                document.Add(stroke);
                foreach (var point in points)
                {
                    document.AppendSample(stroke.Id, point);
                }
                document.Commit();
            }, () =>
            {
                BenchmarkFailure.Require(document.Elements.Count == 1 && document.Elements[0].Points == points, "Append lost or changed samples.");
                BenchmarkFailure.Require(document.CanUndo && document.Generation == 1, "Gesture must be one commit.");
                return Counters(("retainedPoints", (ulong)document.Elements[0].Points.Count), ("historyBytes", (ulong)document.HistoryBytes));
            });
        });
    }

    private static Result Roundtrip(string scenario, int samples, int warmup)
    {
        var original = new CanvasDocument(ElementList.Create([.. Fixtures.Elements(999), Fixtures.Stroke(200_000)]));
        return Measure(scenario, Workload(("elements", 1_000), ("longStrokePoints", 200_000)),
                       "DrawingFile JSON encode, decode and document validation; no disk IO.", samples, warmup, () =>
        {
            byte[] data = [];
            var restored = CanvasDocument.Empty();
            return new Job(() =>
            {
                data = new DrawingFile(original).Data();
                restored = DrawingFile.Read(data);
            }, () =>
            {
                BenchmarkFailure.Require(restored.Elements == original.Elements, "Drawing roundtrip changed elements.");
                return Counters(("jsonBytes", (ulong)data.Length), ("elements", (ulong)restored.Elements.Count));
            });
        });
    }

    private static Result Document(string scenario, int samples, int warmup)
    {
        var count = scenario is "document.add.5000" or "document.select-hit-miss.5000" ? 5_000 : 1_000;
        var historyCount = scenario is "document.move-100.history-80" or "document.undo.history-80" ? 80 : 0;
        var adding = scenario.StartsWith("document.add.", StringComparison.Ordinal);
        var selecting = scenario.StartsWith("document.select-hit-miss.", StringComparison.Ordinal);
        var undoing = scenario == "document.undo.history-80";
        var elements = ElementList.Create(Fixtures.Elements(count));
        var initial = new CanvasDocument(elements);
        var selected = new List<Guid>();
        for (var index = 0; index < 100; index++)
        {
            selected.Add(elements[index].Id);
        }
        var hitPoint = elements[elements.Count - 1].Frame.Center;
        BenchmarkFailure.Require(ElementStacking.Frontmost(hitPoint, elements) is not null, "Selection fixture must hit.");
        long snapshotBytes = 64;
        foreach (ref readonly var element in elements)
        {
            snapshotBytes += element.EstimatedBytes;
        }
        return Measure(scenario, Workload(("initialElements", count), ("preparedHistoryEntries", historyCount),
                                          ("selectedElements", adding || selecting ? 0 : 100), ("operationsPerSample", selecting ? 100 : 1)),
                       selecting ? "50 frontmost hit + 50 off-canvas miss selections, alternating."
                                 : "One production mutation including commit/bindings/history; prepared history excluded.",
                       samples, warmup, () =>
        {
            var document = initial;
            document.Select(selected);
            for (var step = 0; step < historyCount; step++)
            {
                document.EditSelection(static (ref Element element) => element.Translate(new Point(1, 0)));
            }
            BenchmarkFailure.Require(document.HistoryBytes == historyCount * snapshotBytes, "Prepared history fixture was trimmed.");
            var previousX = document.Elements[0].Frame.X;
            var extra = new Element(Tool.Rectangle, new Rect(10, 10, 30, 30), id: Fixtures.Id(700_000));
            var hits = 0;
            return new Job(() =>
            {
                if (adding)
                {
                    document.Add(extra);
                }
                else if (selecting)
                {
                    for (var index = 0; index < 100; index++)
                    {
                        document.Select(index % 2 == 0 ? hitPoint : new Point(-1_000, -1_000));
                        if (!document.Selection.IsEmpty)
                        {
                            hits++;
                        }
                    }
                }
                else if (undoing)
                {
                    document.Undo();
                }
                else
                {
                    document.EditSelection(static (ref Element element) => element.Translate(new Point(1, 0)));
                }
            }, () =>
            {
                var counters = Counters(("elements", (ulong)document.Elements.Count), ("historyBytes", (ulong)document.HistoryBytes),
                                        ("hits", (ulong)hits), ("generation", document.Generation));
                if (adding)
                {
                    BenchmarkFailure.Require(document.Elements.Count == count + 1 && document.Elements[count].Id == extra.Id, "Add failed.");
                    document.Undo();
                    BenchmarkFailure.Require(document.Elements == elements, "Added element did not undo.");
                }
                else if (selecting)
                {
                    BenchmarkFailure.Require(hits == 50 && document.Selection.IsEmpty, "Hit/miss selection mismatch.");
                }
                else
                {
                    var expectedX = previousX + (undoing ? -1 : 1);
                    BenchmarkFailure.Require(document.Elements[0].Frame.X == expectedX, "Move/undo geometry mismatch.");
                    BenchmarkFailure.Require(document.Elements[100].Frame == elements[100].Frame, "Unselected element moved.");
                }
                return counters;
            });
        });
    }

    private static Element[] RendererElements(string scenario)
    {
        var name = scenario[..scenario.LastIndexOf('.')];
        if (name == "renderer.pressure-200000.clipped")
        {
            name = "renderer.pressure-200000";
        }
        switch (name)
        {
            case "renderer.standard":
                return [.. Fixtures.Elements(200), Fixtures.Stroke(20_000)];
            case "renderer.gradients-200":
            {
                Element[] elements = [.. Fixtures.Elements(200), Fixtures.Stroke(20_000)];
                for (var index = 0; index < elements.Length; index++)
                {
                    elements[index].Style.SetGradient(GradientPreset.Spectrum.Gradient, fill: false);
                    if (elements[index].Tool.SupportsFill)
                    {
                        elements[index].Style.SetGradient(GradientPreset.Sunset.Gradient, fill: true);
                    }
                }
                return elements;
            }
            case "renderer.stress":
                return [.. Fixtures.Elements(999), Fixtures.Stroke(200_000)];
            case "renderer.rough-256":
                return Fixtures.Elements(256, ellipsesOnly: true);
            case "renderer.rough-1000":
                return Fixtures.Elements(1_000, ellipsesOnly: true);
            case "renderer.rough-wide-32":
            {
                var elements = Fixtures.Elements(32, ellipsesOnly: true);
                for (var index = 0; index < elements.Length; index++)
                {
                    elements[index].Frame = new Rect(160, 90, 1_600, 900);
                }
                return elements;
            }
            case "renderer.images-64":
                return Fixtures.Images(64, Fixtures.Png(Fixtures.Source(512, 512)));
            case "renderer.text-1000":
            {
                var elements = Fixtures.Elements(1_000);
                for (var index = 0; index < elements.Length; index++)
                {
                    elements[index].Tool = Tool.Text;
                    elements[index].Points = default;
                    elements[index].Text = $"Note {elements[index].Seed % 1_000}";
                }
                return elements;
            }
            case "renderer.pressure-200000":
            {
                var stroke = Fixtures.Stroke(200_000);
                stroke.Style.Pressure = true;
                stroke.Style.Roughness = 6;
                var pressures = new double[stroke.Points.Count];
                for (var index = 0; index < pressures.Length; index++)
                {
                    pressures[index] = 0.2 + index % 101 * 0.008;
                }
                stroke.Pressures = ValueList.Create<double>(pressures);
                return [stroke];
            }
            default:
                throw new BenchmarkFailure($"Not a renderer scenario: {scenario}.");
        }
    }

    private static void Enter(DisplayListRenderer renderer, SceneSnapshot scene)
    {
        foreach (var list in scene.Elements)
        {
            renderer.Enter(list);
        }
        renderer.CollectImages();
    }

    private static Result Renderer(string scenario, int samples, int warmup)
    {
        var cached = scenario.EndsWith(".cached", StringComparison.Ordinal);
        var source = RendererElements(scenario);
        foreach (ref readonly var element in source.AsSpan())
        {
            element.Validate();
        }
        var elements = ElementList.Create(source);
        var clip = scenario == "renderer.pressure-200000.clipped.cached"
            ? new PixelRect(912, 492, 912 + 96, 492 + 96)
            : new PixelRect(0, 0, Fixtures.Width, Fixtures.Height);
        var area = new DrawArea(1, 0, 0, clip);
        var pathElements = source.Count(element => element.Tool.IsShape || element.Tool.IsConnector || element.Tool.IsFreehand);
        using var devices = GraphicsDevices.Create();
        // Direct2D on a GPU fills merged stroke outlines with an edge pixel or two antialiased differently from draw to
        // draw (measured on the RTX 3080: up to 2 pixels of the standard scene, up to 16/255; WARP is exact). So samples
        // must match a reference draw within WIN-REND-05's edge allowance (32/255) on at most 0.01% of the pixels, and the
        // differences are recorded; the Mac's CPU bitmaps match exactly.
        byte[] reference;
        using (var measurer = new DirectWriteTextMeasurer())
        using (var renderer = new DisplayListRenderer(devices, FixedSystemColors.Light))
        using (var canvas = new GpuCanvas(devices, Fixtures.Width, Fixtures.Height))
        {
            var scene = new SceneBuilder(measurer).Build(elements);
            Enter(renderer, scene);
            canvas.Draw(context => renderer.Draw(context, scene.Elements, area));
            reference = canvas.Pixels();
        }
        var referenceChecksum = Fixtures.Checksum(reference);
        var workload = Workload(("elements", elements.Count), ("pathElements", pathElements),
                                ("storedPoints", source.Sum(element => (long)element.Points.Count)),
                                ("roughEllipses", source.Count(element => element.Tool == Tool.Ellipse && element.Style.Roughness > 0)),
                                ("imageElements", source.Count(element => element.Tool == Tool.Image)),
                                ("maximumElementWidth", (long)Math.Ceiling(source.Max(element => element.Frame.Width))),
                                ("maximumElementHeight", (long)Math.Ceiling(source.Max(element => element.Frame.Height))),
                                ("bitmapWidth", Fixtures.Width), ("bitmapHeight", Fixtures.Height),
                                ("clipWidth", clip.Width), ("clipHeight", clip.Height));
        var scope = cached
            ? "Scene build (cached, unchanged) + clear + Direct2D draw + EndDraw + GPU completion on the hardware device; renderer primed once outside each sample."
            : "Scene build + renderer entry + clear + Direct2D draw + EndDraw + GPU completion on the hardware device with empty caches; target allocated outside timing.";
        return Measure(scenario, workload, scope, samples, warmup, () =>
        {
            var measurer = new DirectWriteTextMeasurer();
            var builder = new SceneBuilder(measurer);
            var renderer = new DisplayListRenderer(devices, FixedSystemColors.Light);
            var canvas = new GpuCanvas(devices, Fixtures.Width, Fixtures.Height);
            SceneSnapshot? scene = null;
            if (cached)
            {
                var primed = builder.Build(elements);
                Enter(renderer, primed);
                canvas.Draw(context => renderer.Draw(context, primed.Elements, area));
                scene = primed;
            }
            var builds = builder.FullPathBuilds;
            var appends = builder.IncrementalAppends;
            var counters = renderer.Counters;
            long geometries = counters.GeometriesBuilt, widened = counters.WidenedStrokes, layouts = counters.TextLayoutsBuilt;
            return new Job(() =>
            {
                var built = builder.Build(elements);
                if (!ReferenceEquals(built, scene))
                {
                    Enter(renderer, built);
                    scene = built;
                }
                canvas.Draw(context => renderer.Draw(context, built.Elements, area));
            }, () =>
            {
                var pixels = canvas.Pixels();
                var checksum = Fixtures.Checksum(pixels);
                var difference = checksum == referenceChecksum ? default : canvas.Difference(reference, pixels);
                BenchmarkFailure.Require(difference.Count <= Fixtures.Width * Fixtures.Height / 10_000 && difference.Largest <= 32,
                                         $"Renderer pixels changed across cache/sample states beyond the edge allowance: {difference}.");
                var delta = builder.FullPathBuilds - builds;
                if (!cached && scenario != "renderer.pressure-200000.cold")
                {
                    BenchmarkFailure.Require(delta == pathElements, "Cold path build count mismatch.");
                }
                if (cached && pathElements <= 512)
                {
                    BenchmarkFailure.Require(delta == 0, "Fitting scene unexpectedly rebuilt paths.");
                }
                return Counters(("fullPathBuilds", (ulong)delta), ("incrementalAppends", (ulong)(builder.IncrementalAppends - appends)),
                                ("pixelChecksumFNV1a", checksum), ("referenceChecksumFNV1a", referenceChecksum),
                                ("pixelsDifferingFromReference", (ulong)difference.Count), ("largestDifferenceFromReference", (ulong)difference.Largest),
                                ("cpuSubmitMicroseconds", (ulong)Math.Round(canvas.LastSubmitMs * 1_000)),
                                ("geometriesBuilt", (ulong)(counters.GeometriesBuilt - geometries)),
                                ("widenedStrokes", (ulong)(counters.WidenedStrokes - widened)),
                                ("textLayoutsBuilt", (ulong)(counters.TextLayoutsBuilt - layouts)));
            }, () =>
            {
                renderer.Dispose();
                canvas.Dispose();
                measurer.Dispose();
            });
        });
    }

    private static Result Image(string scenario, int samples, int warmup)
    {
        var source = Fixtures.Source();
        var png = Fixtures.Png(source);
        var annotated = scenario is "export.annotated-images.png" or "export.synthetic-effects.png";
        var exporting = scenario.StartsWith("export.", StringComparison.Ordinal);
        var list = new List<Element>();
        if (annotated)
        {
            list.AddRange(Fixtures.Elements(200));
            list.Add(Fixtures.Stroke(20_000));
            list.AddRange(Fixtures.Images(8, Fixtures.Png(Fixtures.Source(512, 512))));
        }
        if (scenario == "export.synthetic-effects.png")
        {
            Tool[] tools = [Tool.Blur, Tool.Pixelate, Tool.Distort, Tool.Cover];
            for (var index = 0; index < tools.Length; index++)
            {
                list.Add(new Element(tools[index], new Rect(index * 400, 200, 300, 300), id: Fixtures.Id(600_000 + index)));
            }
        }
        var elements = ElementList.Create(list.ToArray());
        using var devices = exporting ? GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true }) : null;
        using var measurer = exporting ? new DirectWriteTextMeasurer() : null;
        var scope = scenario switch
        {
            "image.decode.1920x1080" => "RasterAsset.Decode (WIC) plus full decoded-byte read (forces pixel materialization).",
            "image.png-encode.1920x1080" => "RasterAsset.EncodePng (WIC) to memory.",
            _ => "SnapshotExporter.Image on WARP (as the app exports) plus PNG encoding to memory; synthetic source, no disk IO; exporter created outside timing.",
        };
        return Measure(scenario, Workload(("width", source.Width), ("height", source.Height), ("sourcePNGBytes", png.Length),
                                          ("annotationElements", elements.Count), ("imageElements", annotated ? 8 : 0)),
                       scope, samples, warmup, () =>
        {
            var exporter = exporting ? new SnapshotExporter(devices!, measurer!) : null;
            byte[] result = [];
            RasterImage? decoded = null;
            long pixelBytes = 0;
            ulong pixelSum = 0;
            return new Job(() =>
            {
                if (scenario == "image.decode.1920x1080")
                {
                    decoded = RasterAsset.Decode(png);
                    var pixels = decoded.Pixels;
                    pixelBytes = pixels.Length;
                    foreach (var value in pixels)
                    {
                        pixelSum += value;
                    }
                }
                else if (scenario == "image.png-encode.1920x1080")
                {
                    result = RasterAsset.EncodePng(source);
                }
                else
                {
                    result = RasterAsset.EncodePng(exporter!.Image(source, elements));
                }
            }, () =>
            {
                var image = decoded ?? RasterAsset.Decode(result);
                BenchmarkFailure.Require(image.Width == Fixtures.Width && image.Height == Fixtures.Height, "Image size mismatch.");
                if (decoded is not null)
                {
                    BenchmarkFailure.Require(pixelBytes >= Fixtures.Width * Fixtures.Height * 4 && pixelSum > 0, "Decode did not materialize pixels.");
                }
                else
                {
                    BenchmarkFailure.Require(result.Length > 0, "PNG output is empty.");
                }
                return Counters(("outputPNGBytes", (ulong)result.Length), ("materializedPixelBytes", (ulong)pixelBytes), ("pixelByteSum", pixelSum));
            }, () => exporter?.Dispose());
        });
    }

    private static Result Cleanup(string scenario, int samples, int warmup)
    {
        using var devices = GraphicsDevices.Create();
        var stages = new List<MemoryStage>();
        var measurements = new List<Sample>();
        var area = new DrawArea(1, 0, 0, new PixelRect(0, 0, Fixtures.Width, Fixtures.Height));
        for (var cycle = 0; cycle < samples + warmup; cycle++)
        {
            var before = MemoryReading.Read();
            stages.Add(new MemoryStage(cycle, "before-owned-allocation", before));
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            Owned(devices, area, cycle, stages);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var end = System.Diagnostics.Stopwatch.GetTimestamp();
            var drained = MemoryReading.Read();
            stages.Add(new MemoryStage(cycle, "after-dispose-and-collection", drained));
            var idleStart = System.Diagnostics.Stopwatch.GetTimestamp();
            Thread.Sleep(250);
            var idleEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            var idle = MemoryReading.Read();
            stages.Add(new MemoryStage(cycle, "after-harness-idle", idle, Milliseconds(idleStart, idleEnd),
                                       (idle.ProcessCpuSeconds - drained.ProcessCpuSeconds) * 1_000));
            if (cycle >= warmup)
            {
                measurements.Add(new Sample(Milliseconds(start, end), before, drained, Counters(("imageElements", 64), ("historyEdits", 20))));
            }
        }
        return Finish(scenario, Workload(("imageElements", 64), ("sourceImageWidth", 512), ("sourceImageHeight", 512), ("historyEdits", 20), ("idleSleepMS", 250)),
                      "Owned fixture allocation, history, draw on the hardware device, dispose and garbage collection; includes memory probes, excludes idle sleep.",
                      warmup, measurements, stages);
    }

    private static void Owned(GraphicsDevices devices, DrawArea area, int cycle, List<MemoryStage> stages)
    {
        var png = Fixtures.Png(Fixtures.Source(512, 512));
        var document = new CanvasDocument(ElementList.Create(Fixtures.Images(64, png)));
        var ids = new List<Guid>();
        foreach (ref readonly var element in document.Elements)
        {
            ids.Add(element.Id);
        }
        document.Select(ids);
        for (var step = 0; step < 20; step++)
        {
            document.EditSelection(static (ref Element element) => element.Translate(new Point(1, 0)));
        }
        using (var measurer = new DirectWriteTextMeasurer())
        using (var renderer = new DisplayListRenderer(devices, FixedSystemColors.Light))
        using (var canvas = new GpuCanvas(devices, Fixtures.Width, Fixtures.Height))
        {
            var builder = new SceneBuilder(measurer);
            var scene = builder.Build(document.Elements);
            Enter(renderer, scene);
            canvas.Draw(context => renderer.Draw(context, scene.Elements, area));
            stages.Add(new MemoryStage(cycle, "owned-live", MemoryReading.Read()));
            builder.Reset();
        }
        document = CanvasDocument.Empty();
        BenchmarkFailure.Require(document.Elements.IsEmpty && !document.CanUndo, "Owned document cleanup failed.");
    }
}
