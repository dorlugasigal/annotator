using Annotator.Core.Geometry;
using Annotator.Rendering.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;

namespace Annotator.Rendering.Imaging;

/// <summary>
/// Windows Imaging Component decoding and PNG encoding with the Mac's raster limits (<c>RasterAsset</c>,
/// <c>PNGExporter.encode</c>). One instance per owner; WIC's factory is free-threaded, but this wrapper is not
/// meant to be shared between threads.
/// </summary>
internal sealed unsafe class WicImaging : IDisposable
{
    /// <summary>Swift <c>RasterAsset</c>: encoded data up to 128 MiB.</summary>
    public const int MaximumEncodedBytes = 128 * 1_024 * 1_024;

    /// <summary>Swift <c>RasterAsset</c>: each side at most 16,384 pixels.</summary>
    public const int MaximumSide = 16_384;

    /// <summary>Swift <c>RasterAsset</c>: at most 40 million pixels.</summary>
    public const double MaximumPixels = 40_000_000;

    private const int CoENotInitialized = unchecked((int)0x800401F0);

    private IWICImagingFactory* factory;

    private WicImaging(IWICImagingFactory* factory)
    {
        this.factory = factory;
    }

    public static WicImaging Create()
    {
        Guid clsid = PInvoke.CLSID_WICImagingFactory2;
        Guid iid = IWICImagingFactory.IID_Guid;
        void* raw;
        var result = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, &raw);
        if (result.Value == CoENotInitialized)
        {
            // A thread the runtime never initialized for COM (rare in .NET); join the multithreaded apartment.
            Native.Check(PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED), "CoInitializeEx(multithreaded)");
            result = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, &raw);
        }
        Native.Check(result, "CoCreateInstance(CLSID_WICImagingFactory2)");
        return new WicImaging((IWICImagingFactory*)raw);
    }

    /// <summary>
    /// Swift <c>RasterAsset.decode</c>: the first frame with its orientation applied, as premultiplied BGRA in WIC
    /// memory. Anything Image I/O would refuse (unreadable data, an empty or oversized frame) throws
    /// <see cref="RenderFailure"/> with <see cref="RenderFailureKind.InvalidSize"/>, as on the Mac. The caller owns
    /// the returned bitmap.
    /// </summary>
    public IWICBitmap* Decode(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(factory == null, this);
        if (data.Length > MaximumEncodedBytes || data.IsEmpty)
        {
            throw new RenderFailure(RenderFailureKind.InvalidSize);
        }
        IWICStream* stream = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;
        IWICFormatConverter* converter = null;
        IWICBitmapFlipRotator* rotator = null;
        try
        {
            fixed (byte* bytes = data)
            {
                Native.Check(factory->CreateStream(&stream), "IWICImagingFactory::CreateStream");
                Native.Check(stream->InitializeFromMemory(bytes, (uint)data.Length), "IWICStream::InitializeFromMemory");
                if (factory->CreateDecoderFromStream((IStream*)stream, null, WICDecodeOptions.WICDecodeMetadataCacheOnDemand, &decoder).Failed
                    || decoder->GetFrame(0, &frame).Failed)
                {
                    throw new RenderFailure(RenderFailureKind.InvalidSize);
                }
                uint width, height;
                if (frame->GetSize(&width, &height).Failed || width == 0 || height == 0 || width > MaximumSide || height > MaximumSide
                    || (double)width * height > MaximumPixels)
                {
                    throw new RenderFailure(RenderFailureKind.InvalidSize);
                }
                Native.Check(factory->CreateFormatConverter(&converter), "IWICImagingFactory::CreateFormatConverter");
                Guid premultiplied = PInvoke.GUID_WICPixelFormat32bppPBGRA;
                if (converter->Initialize((IWICBitmapSource*)frame, &premultiplied, WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0,
                                          WICBitmapPaletteType.WICBitmapPaletteTypeCustom).Failed)
                {
                    throw new RenderFailure(RenderFailureKind.InvalidSize);
                }
                var source = (IWICBitmapSource*)converter;
                var transform = OrientationTransform(Orientation(frame));
                if (transform != WICBitmapTransformOptions.WICBitmapTransformRotate0)
                {
                    Native.Check(factory->CreateBitmapFlipRotator(&rotator), "IWICImagingFactory::CreateBitmapFlipRotator");
                    Native.Check(rotator->Initialize(source, transform), "IWICBitmapFlipRotator::Initialize");
                    source = (IWICBitmapSource*)rotator;
                }
                IWICBitmap* bitmap;
                // Load now, while the encoded bytes are pinned.
                if (factory->CreateBitmapFromSource(source, WICBitmapCreateCacheOption.WICBitmapCacheOnLoad, &bitmap).Failed)
                {
                    throw new RenderFailure(RenderFailureKind.InvalidSize);
                }
                return bitmap;
            }
        }
        finally
        {
            Native.Release(ref rotator);
            Native.Release(ref converter);
            Native.Release(ref frame);
            Native.Release(ref decoder);
            Native.Release(ref stream);
        }
    }

    /// <summary>Decodes into a CPU image (see <see cref="Decode"/>).</summary>
    public RasterImage DecodeImage(ReadOnlySpan<byte> data)
    {
        var bitmap = Decode(data);
        try
        {
            return Copy((IWICBitmapSource*)bitmap);
        }
        finally
        {
            Native.Release(ref bitmap);
        }
    }

    public RasterImage Copy(IWICBitmapSource* source)
    {
        uint width, height;
        Native.Check(source->GetSize(&width, &height), "IWICBitmapSource::GetSize");
        var stride = checked((int)width * 4);
        var pixels = GC.AllocateUninitializedArray<byte>(checked(stride * (int)height));
        fixed (byte* buffer = pixels)
        {
            Native.Check(source->CopyPixels(null, (uint)stride, (uint)pixels.Length, buffer), "IWICBitmapSource::CopyPixels");
        }
        return new RasterImage((int)width, (int)height, stride, pixels);
    }

    /// <summary>
    /// Swift <c>PNGExporter.encode</c>: an 8-bit RGBA PNG with straight alpha. Throws <see cref="RenderFailure"/> with
    /// <see cref="RenderFailureKind.EncodingFailed"/> when WIC cannot write it.
    /// </summary>
    public byte[] EncodePng(RasterImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(factory == null, this);
        var straight = Unpremultiply(image);
        IStream* stream = null;
        IWICBitmapEncoder* encoder = null;
        IWICBitmapFrameEncode* frame = null;
        try
        {
            if (PInvoke.CreateStreamOnHGlobal(HGLOBAL.Null, true, &stream).Failed)
            {
                throw new RenderFailure(RenderFailureKind.EncodingFailed);
            }
            Guid png = PInvoke.GUID_ContainerFormatPng;
            Guid format = PInvoke.GUID_WICPixelFormat32bppBGRA;
            var requested = format;
            if (factory->CreateEncoder(&png, null, &encoder).Failed
                || encoder->Initialize(stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache).Failed
                || encoder->CreateNewFrame(&frame, null).Failed
                || frame->Initialize(null).Failed
                || frame->SetSize((uint)image.Width, (uint)image.Height).Failed
                || frame->SetPixelFormat(&format).Failed
                || format != requested)
            {
                throw new RenderFailure(RenderFailureKind.EncodingFailed);
            }
            fixed (byte* pixels = straight)
            {
                if (frame->WritePixels((uint)image.Height, (uint)image.Width * 4, (uint)straight.Length, pixels).Failed
                    || frame->Commit().Failed || encoder->Commit().Failed)
                {
                    throw new RenderFailure(RenderFailureKind.EncodingFailed);
                }
            }
            ulong length;
            Native.Check(stream->Seek(0, SeekOrigin.Current, &length), "IStream::Seek");
            Native.Check(stream->Seek(0, SeekOrigin.Begin, null), "IStream::Seek");
            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
            uint read;
            fixed (byte* output = bytes)
            {
                Native.Check(stream->Read(output, (uint)bytes.Length, &read), "IStream::Read");
            }
            if (read != bytes.Length)
            {
                throw new RenderFailure(RenderFailureKind.EncodingFailed);
            }
            return bytes;
        }
        finally
        {
            Native.Release(ref frame);
            Native.Release(ref encoder);
            Native.Release(ref stream);
        }
    }

    public void Dispose() => Native.Release(ref factory);

    /// <summary>Straight-alpha BGRA rows without padding, rounding to nearest.</summary>
    private static byte[] Unpremultiply(RasterImage image)
    {
        var output = GC.AllocateUninitializedArray<byte>(image.Width * image.Height * 4);
        var source = image.Pixels;
        for (var y = 0; y < image.Height; y++)
        {
            var row = source.Slice(y * image.Stride, image.Width * 4);
            var target = output.AsSpan(y * image.Width * 4, image.Width * 4);
            for (var x = 0; x < row.Length; x += 4)
            {
                int alpha = row[x + 3];
                if (alpha is 0 or 255)
                {
                    target[x] = alpha == 0 ? (byte)0 : row[x];
                    target[x + 1] = alpha == 0 ? (byte)0 : row[x + 1];
                    target[x + 2] = alpha == 0 ? (byte)0 : row[x + 2];
                }
                else
                {
                    target[x] = (byte)Math.Min(255, (row[x] * 255 + alpha / 2) / alpha);
                    target[x + 1] = (byte)Math.Min(255, (row[x + 1] * 255 + alpha / 2) / alpha);
                    target[x + 2] = (byte)Math.Min(255, (row[x + 2] * 255 + alpha / 2) / alpha);
                }
                target[x + 3] = (byte)alpha;
            }
        }
        return output;
    }

    /// <summary>The EXIF orientation (1 to 8) through WIC's photo metadata policy; 1 when the image has none.</summary>
    private static ushort Orientation(IWICBitmapFrameDecode* frame)
    {
        IWICMetadataQueryReader* reader = null;
        if (frame->GetMetadataQueryReader(&reader).Failed || reader == null)
        {
            return 1;
        }
        try
        {
            PROPVARIANT value = default;
            fixed (char* name = "System.Photo.Orientation")
            {
                if (reader->GetMetadataByName(name, &value).Failed)
                {
                    return 1;
                }
            }
            var orientation = value.vt == VARENUM.VT_UI2 ? value.uiVal : (ushort)1;
            PInvoke.PropVariantClear(&value);
            return orientation is >= 1 and <= 8 ? orientation : (ushort)1;
        }
        finally
        {
            Native.Release(ref reader);
        }
    }

    /// <summary>EXIF orientation to WIC's transform, which flips before it rotates (clockwise).</summary>
    private static WICBitmapTransformOptions OrientationTransform(ushort orientation) => orientation switch
    {
        2 => WICBitmapTransformOptions.WICBitmapTransformFlipHorizontal,
        3 => WICBitmapTransformOptions.WICBitmapTransformRotate180,
        4 => WICBitmapTransformOptions.WICBitmapTransformFlipVertical,
        5 => WICBitmapTransformOptions.WICBitmapTransformRotate270 | WICBitmapTransformOptions.WICBitmapTransformFlipHorizontal,
        6 => WICBitmapTransformOptions.WICBitmapTransformRotate90,
        7 => WICBitmapTransformOptions.WICBitmapTransformRotate90 | WICBitmapTransformOptions.WICBitmapTransformFlipHorizontal,
        8 => WICBitmapTransformOptions.WICBitmapTransformRotate270,
        _ => WICBitmapTransformOptions.WICBitmapTransformRotate0,
    };
}
