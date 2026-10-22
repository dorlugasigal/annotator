using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Platform.Displays;
using Annotator.Rendering.Effects;
using Annotator.Rendering.Imaging;

namespace Annotator.Rendering.Tests.Conformance;

/// <summary>One <c>effects</c> case: an input image through Core Image's CaptureCompositor on the Mac.</summary>
internal sealed record EffectsCase(string Name, string Input, string Output, double PixelsPerPoint, Size LogicalSize, ElementList Elements,
                                   bool RegionsOnly, bool IncludeImageLayers, Rect? PrivacyRegion)
{
    public override string ToString() => Name;
}

/// <summary>The <c>effects</c> section of the corpus and the Windows pipeline that reproduces it.</summary>
internal static class EffectsCorpus
{
    private static readonly Lazy<IReadOnlyList<EffectsCase>> AllCases = new(Read);

    public static IReadOnlyList<EffectsCase> Cases => AllCases.Value;

    public static EffectsCase Find(string name) => Cases.Single(item => item.Name == name);

    public static RasterImage Input(EffectsCase item) => ConformanceCorpus.Png($"effects/{item.Input}");

    public static RasterImage Reference(EffectsCase item) => ConformanceCorpus.Png($"effects/{item.Output}");

    /// <summary>
    /// The Windows result: the input through <see cref="EffectPipeline"/>. With <c>regionsOnly</c> it is the effect
    /// layer alone (<see cref="EffectPipeline.Render"/>, the live overlay); otherwise the whole frame
    /// (<see cref="EffectPipeline.RenderScene"/>, the snapshot and lens paths).
    /// </summary>
    public static RasterImage Render(EffectsCase item)
    {
        var source = Input(item);
        var list = EffectList.From(item.Elements, item.PixelsPerPoint, source.Width, source.Height, item.IncludeImageLayers, item.PrivacyRegion);
        var devices = ConformanceDevices.Devices;
        using var pipeline = new EffectPipeline(devices);
        var input = EffectTextures.Upload(devices, source);
        var target = EffectTextures.Target(devices, source.Width, source.Height);
        try
        {
            if (item.RegionsOnly)
            {
                pipeline.Render(input, target, list, [new PixelRect(0, 0, source.Width, source.Height)]);
            }
            else
            {
                pipeline.RenderScene(input, target, list);
            }
            return EffectTextures.Read(devices, target, source.Width, source.Height);
        }
        finally
        {
            Marshal.Release(target);
            Marshal.Release(input);
        }
    }

    private static IReadOnlyList<EffectsCase> Read()
    {
        var cases = new List<EffectsCase>();
        foreach (var record in ConformanceCorpus.Lines("effects/index.jsonl"))
        {
            var board = $"{{\"elements\":{record.GetProperty("elements").GetRawText()},\"schemaVersion\":{DrawingFile.CurrentSchema}}}";
            var elements = DrawingFile.Read(Encoding.UTF8.GetBytes(board)).Elements;
            var logical = record.GetProperty("logicalSize");
            var privacy = record.GetProperty("privacyRegion");
            cases.Add(new EffectsCase(record.GetProperty("name").GetString()!, record.GetProperty("input").GetString()!,
                                      record.GetProperty("output").GetString()!, record.GetProperty("pixelsPerPoint").GetDouble(),
                                      new Size(logical.GetProperty("width").GetDouble(), logical.GetProperty("height").GetDouble()), elements,
                                      record.GetProperty("regionsOnly").GetBoolean(), record.GetProperty("includeImageLayers").GetBoolean(),
                                      privacy.ValueKind == JsonValueKind.Null ? null
                                          : new Rect(privacy.GetProperty("x").GetDouble(), privacy.GetProperty("y").GetDouble(),
                                                     privacy.GetProperty("width").GetDouble(), privacy.GetProperty("height").GetDouble())));
        }
        return cases;
    }
}
