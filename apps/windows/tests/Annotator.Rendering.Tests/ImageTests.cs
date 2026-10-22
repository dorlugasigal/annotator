// Ports of apps/macos/Tests/RenderingTests/DecodedImageCacheTests.swift and apps/macos/Tests/CaptureTests/ImageCensorTests.swift
// (importedImageOrientationIsApplied), plus the WIC limits of Swift RasterAsset.
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Rendering.Drawing;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;

namespace Annotator.Rendering.Tests;

public sealed unsafe class ImageTests
{
    private static byte[] SolidPng(int size, RGBA color)
    {
        using var exporter = Warp.Exporter();
        return exporter.Data(ElementList.Empty, new Size(size, size), background: color);
    }

    [Fact]
    public void IdenticalImagesAcrossObjectsShareOneDecodeAndReleaseOnRemoval()
    {
        using var renderer = new DisplayListRenderer(Warp.Devices);
        var data = ValueList.Create<byte>(SolidPng(64, Palette.Blue));
        var otherData = ValueList.Create<byte>(data.ToArray());
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        using var cache = new DecodedImageCache(renderer.Decode);
        cache.BeginFrame(new HashSet<Guid> { first, second });
        var one = cache.Image(data, first);
        var two = cache.Image(otherData, second);
        cache.EndFrame();
        Assert.Same(one, two);
        Assert.Equal(1, cache.DecodeCount);
        Assert.Equal((long)one.BytesPerRow * one.Height, cache.Bytes);
        cache.BeginFrame(new HashSet<Guid>());
        cache.EndFrame();
        Assert.Equal(0, cache.Bytes);
        Assert.True(one.IsDisposed, "Removing the last use releases the GPU bitmap.");
    }

    [Fact]
    public void ChangedImageContentInvalidatesOnlyItsAliasAndCacheStaysBounded()
    {
        using var renderer = new DisplayListRenderer(Warp.Devices);
        var blue = ValueList.Create<byte>(SolidPng(64, Palette.Blue));
        var white = ValueList.Create<byte>(SolidPng(64, Palette.White));
        var id = Guid.NewGuid();
        using var cache = new DecodedImageCache(renderer.Decode, byteLimit: 20_000);
        cache.BeginFrame(new HashSet<Guid> { id });
        _ = cache.Image(blue, id);
        cache.EndFrame();
        cache.BeginFrame(new HashSet<Guid> { id });
        _ = cache.Image(white, id);
        cache.EndFrame();
        Assert.Equal(2, cache.DecodeCount);
        Assert.True(cache.Bytes <= 20_000);
        using var tiny = new DecodedImageCache(renderer.Decode, byteLimit: 1);
        tiny.BeginFrame(new HashSet<Guid> { id });
        using var uncached = tiny.Image(blue, id);
        Assert.Equal(64, uncached.Width);
        Assert.False(DecodedImageCache.IsCached(uncached));
        tiny.EndFrame();
        Assert.Equal(0, tiny.Bytes);
    }

    [Fact]
    public void RendererKeepsImagesOnlyWhileAListUsesThem()
    {
        using var exporter = Warp.Exporter();
        var renderer = exporter.Renderer;
        var png = ValueList.Create<byte>(SolidPng(32, Palette.Coral));
        var image = new Element(Tool.Image, new Rect(4, 4, 32, 32)) { ImagePng = png };
        var copy = new Element(Tool.Image, new Rect(40, 4, 32, 32)) { ImagePng = ValueList.Create<byte>(png.ToArray()) };
        var scene = Warp.Builder().Build(ElementList.Create([image, copy]));
        foreach (var list in scene.Elements)
        {
            renderer.Enter(list);
        }
        renderer.CollectImages();
        var picture = exporter.Canvas.Render(80, 40, context => renderer.Draw((nint)context, scene.Elements, OffscreenCanvas.Area(80, 40, 1)));
        Assert.Equal(1, renderer.Images.DecodeCount);
        Assert.Equal(32L * 4 * 32, renderer.Images.Bytes);
        Assert.Equal(255, picture[20, 20].Alpha);
        Assert.Equal(255, picture[56, 20].Alpha);
        // Drawing again reuses the decoded image.
        _ = exporter.Canvas.Render(80, 40, context => renderer.Draw((nint)context, scene.Elements, OffscreenCanvas.Area(80, 40, 1)));
        Assert.Equal(1, renderer.Images.DecodeCount);
        foreach (var list in scene.Elements)
        {
            renderer.Leave(list);
        }
        renderer.CollectImages();
        Assert.Equal(0, renderer.Images.Bytes);
    }

    [Theory]
    [InlineData(1, 40, 20, "red", "blue")]
    [InlineData(3, 40, 20, "blue", "red")]
    [InlineData(6, 20, 40, "red", "blue")]
    [InlineData(8, 20, 40, "blue", "red")]
    public void ImportedImageOrientationIsApplied(ushort orientation, int width, int height, string first, string second)
    {
        // A 40 x 20 image, red on the left half and blue on the right half, saved as a JPEG with an EXIF orientation.
        var pixels = new byte[40 * 20 * 4];
        for (var y = 0; y < 20; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                var offset = (y * 40 + x) * 4;
                pixels[offset] = (byte)(x < 20 ? 0 : 255);
                pixels[offset + 2] = (byte)(x < 20 ? 255 : 0);
                pixels[offset + 3] = 255;
            }
        }
        var jpeg = Jpeg(new RasterImage(40, 20, 160, pixels), orientation);
        var oriented = RasterAsset.Decode(jpeg);
        Assert.Equal((width, height), (oriented.Width, oriented.Height));
        // Orientation 6 turns the image clockwise, so its left half ends on top; 8 turns it the other way.
        var (a, b) = width > height
            ? (oriented[10, 10], oriented[30, 10])
            : (oriented[10, 10], oriented[10, 30]);
        Assert.Equal(first, Name(a));
        Assert.Equal(second, Name(b));
    }

    [Fact]
    public void DecodingRefusesWhatImageIoRefuses()
    {
        foreach (var data in new[] { Array.Empty<byte>(), [1, 2, 3], new byte[64] })
        {
            var failure = Assert.Throws<RenderFailure>(() => RasterAsset.Decode(data));
            Assert.Equal(RenderFailureKind.InvalidSize, failure.Kind);
        }
        var png = SolidPng(8, Palette.White);
        Assert.Equal(8, RasterAsset.Decode(png).Width);
        // A PNG header claiming 20,000 x 10 pixels is over the 16,384-pixel side limit.
        var wide = (byte[])png.Clone();
        wide[16] = 0;
        wide[17] = 0;
        wide[18] = 0x4E;
        wide[19] = 0x20;
        Assert.Equal(RenderFailureKind.InvalidSize, Assert.Throws<RenderFailure>(() => RasterAsset.Decode(wide)).Kind);
    }

    private static string Name((byte Red, byte Green, byte Blue, byte Alpha) color) =>
        color.Red > 180 && color.Blue < 80 ? "red" : color.Blue > 180 && color.Red < 80 ? "blue" : $"({color.Red}, {color.Green}, {color.Blue})";

    /// <summary>Encodes a JPEG with an EXIF orientation through WIC (test only).</summary>
    private static byte[] Jpeg(RasterImage image, ushort orientation)
    {
        Guid clsid = PInvoke.CLSID_WICImagingFactory2;
        Guid iid = IWICImagingFactory.IID_Guid;
        void* raw;
        Native.Check(PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, &raw), "CoCreateInstance");
        var factory = (IWICImagingFactory*)raw;
        IStream* stream = null;
        IWICBitmapEncoder* encoder = null;
        IWICBitmapFrameEncode* frame = null;
        IWICMetadataQueryWriter* writer = null;
        try
        {
            Native.Check(PInvoke.CreateStreamOnHGlobal(HGLOBAL.Null, true, &stream), "CreateStreamOnHGlobal");
            Guid jpeg = PInvoke.GUID_ContainerFormatJpeg;
            Native.Check(factory->CreateEncoder(&jpeg, null, &encoder), "CreateEncoder");
            Native.Check(encoder->Initialize(stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache), "Initialize");
            Native.Check(encoder->CreateNewFrame(&frame, null), "CreateNewFrame");
            Native.Check(frame->Initialize(null), "Frame.Initialize");
            Native.Check(frame->SetSize((uint)image.Width, (uint)image.Height), "SetSize");
            Guid format = PInvoke.GUID_WICPixelFormat24bppBGR;
            Native.Check(frame->SetPixelFormat(&format), "SetPixelFormat");
            Native.Check(frame->GetMetadataQueryWriter(&writer), "GetMetadataQueryWriter");
            PROPVARIANT value = default;
            value.vt = VARENUM.VT_UI2;
            value.uiVal = orientation;
            fixed (char* name = "/app1/ifd/{ushort=274}")
            {
                Native.Check(writer->SetMetadataByName(name, &value), "SetMetadataByName");
            }
            Assert.Equal(PInvoke.GUID_WICPixelFormat24bppBGR, format);
            var bytes = new byte[image.Width * image.Height * 3];
            for (var index = 0; index < image.Width * image.Height; index++)
            {
                bytes[index * 3] = image.Pixels[index * 4];
                bytes[index * 3 + 1] = image.Pixels[index * 4 + 1];
                bytes[index * 3 + 2] = image.Pixels[index * 4 + 2];
            }
            fixed (byte* data = bytes)
            {
                Native.Check(frame->WritePixels((uint)image.Height, (uint)image.Width * 3, (uint)bytes.Length, data), "WritePixels");
            }
            Native.Check(frame->Commit(), "Frame.Commit");
            Native.Check(encoder->Commit(), "Encoder.Commit");
            ulong length;
            Native.Check(stream->Seek(0, SeekOrigin.Current, &length), "Seek");
            Native.Check(stream->Seek(0, SeekOrigin.Begin, null), "Seek");
            var output = new byte[length];
            fixed (byte* target = output)
            {
                uint read;
                Native.Check(stream->Read(target, (uint)output.Length, &read), "Read");
            }
            return output;
        }
        finally
        {
            Native.Release(ref writer);
            Native.Release(ref frame);
            Native.Release(ref encoder);
            Native.Release(ref stream);
            Native.Release(ref factory);
        }
    }
}
