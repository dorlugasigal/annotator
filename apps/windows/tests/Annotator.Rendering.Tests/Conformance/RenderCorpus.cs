using System.Text.Json;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Tests.Conformance;

/// <summary>One image of a <c>render</c> scene: the board, the appearance and the scale it was drawn at.</summary>
internal sealed record RenderCase(string Scene, string Board, string Appearance, int Scale, string ReferencePath, Size Size,
                                  bool FontDependent, bool InterpolationDependent, string Note)
{
    public string Name => $"{Scene}{(Appearance == "dark" ? "-dark" : "")}@{Scale}x";

    public override string ToString() => Name;
}

/// <summary>The <c>render</c> section of the corpus, drawn with Annotator.Rendering on WARP.</summary>
internal static class RenderCorpus
{
    private static readonly Lazy<IReadOnlyList<RenderCase>> AllCases = new(Read);

    public static IReadOnlyList<RenderCase> Cases => AllCases.Value;

    public static RenderCase Find(string name) => Cases.Single(item => item.Name == name);

    /// <summary>The scene's elements, read from its board file with Annotator.Core.</summary>
    public static ElementList Elements(RenderCase item) => DrawingFile.Read(ConformanceCorpus.Read($"render/{item.Board}")).Elements;

    public static RasterImage Reference(RenderCase item) => ConformanceCorpus.Png($"render/{item.ReferencePath}");

    /// <summary>
    /// The Windows image: <see cref="PngExporter.Image"/> at the scene's size and scale, transparent, with the Mac
    /// appearance's system colors so only the renderer differs.
    /// </summary>
    public static RasterImage Render(RenderCase item)
    {
        using var exporter = new PngExporter(ConformanceDevices.Devices, Warp.Measurer, MacAppearance.For(item.Appearance));
        return exporter.Image(Elements(item), item.Size, item.Scale);
    }

    private static IReadOnlyList<RenderCase> Read()
    {
        var cases = new List<RenderCase>();
        foreach (var record in ConformanceCorpus.Lines("render/index.jsonl"))
        {
            var scene = record.GetProperty("scene").GetString()!;
            var size = record.GetProperty("size");
            foreach (var image in record.GetProperty("images").EnumerateObject())
            {
                var parts = image.Name.Split(' ');
                cases.Add(new RenderCase(scene, record.GetProperty("board").GetString()!, parts[0], int.Parse(parts[1].TrimEnd('x'), System.Globalization.CultureInfo.InvariantCulture),
                                         image.Value.GetString()!, new Size(size.GetProperty("width").GetDouble(), size.GetProperty("height").GetDouble()),
                                         record.GetProperty("fontDependent").GetBoolean(), record.GetProperty("interpolationDependent").GetBoolean(),
                                         record.GetProperty("note").GetString()!));
            }
        }
        return cases;
    }
}

/// <summary>
/// The system colors the corpus was drawn with, read from its images: the Mac's window background is white in the
/// light appearance and (30, 30, 30) in the dark one. Accent and red are AppKit's documented defaults; no corpus scene
/// uses them.
/// </summary>
internal static class MacAppearance
{
    public static FixedSystemColors Light { get; } = new(FixedSystemColors.Rgb(0x00, 0x7A, 0xFF), FixedSystemColors.Rgb(0xFF, 0xFF, 0xFF),
                                                         FixedSystemColors.Rgb(0xFF, 0x3B, 0x30));

    public static FixedSystemColors Dark { get; } = new(FixedSystemColors.Rgb(0x0A, 0x84, 0xFF), FixedSystemColors.Rgb(30, 30, 30),
                                                        FixedSystemColors.Rgb(0xFF, 0x45, 0x3A));

    public static FixedSystemColors For(string appearance) => appearance == "dark" ? Dark : Light;
}
