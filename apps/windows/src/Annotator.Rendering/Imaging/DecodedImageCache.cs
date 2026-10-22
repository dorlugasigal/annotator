using Annotator.Core;
using Annotator.Rendering.Interop;
using Windows.Win32.Graphics.Direct2D;

namespace Annotator.Rendering.Imaging;

/// <summary>A decoded image uploaded to the GPU: a Direct2D bitmap with premultiplied BGRA pixels.</summary>
public sealed unsafe class DecodedImage : IDisposable
{
    private ID2D1Bitmap1* bitmap;

    internal DecodedImage(ID2D1Bitmap1* bitmap, int width, int height)
    {
        this.bitmap = bitmap;
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes per row of the decoded pixels (4 bytes per pixel).</summary>
    public int BytesPerRow => Width * 4;

    /// <summary>What the image costs in the cache budget: bytes per row times height.</summary>
    public long Cost => (long)BytesPerRow * Height;

    public bool IsDisposed => bitmap == null;

    internal bool CacheOwned { get; set; }

    internal ID2D1Bitmap1* Bitmap
    {
        get
        {
            ObjectDisposedException.ThrowIf(bitmap == null, this);
            return bitmap;
        }
    }

    public void Dispose() => Native.Release(ref bitmap);
}

/// <summary>
/// Port of Swift <c>DecodedImageCache</c>: decoded images keyed by their bytes, with an alias per element, so equal
/// images in several objects decode once; least recently used images are dropped to stay within the byte budget;
/// an image larger than the budget is returned without being cached; images not used in a frame are released at its
/// end. Use it on one thread.
/// </summary>
public sealed class DecodedImageCache : IDisposable
{
    /// <summary>The Mac's default budget: 128 MiB of decoded pixels.</summary>
    public const long DefaultByteLimit = 128L * 1_024 * 1_024;

    private readonly Func<ValueList<byte>, DecodedImage> decode;
    private readonly long byteLimit;
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly Dictionary<ValueList<byte>, Guid> contentKeys = new(ContentComparer.Instance);
    private readonly Dictionary<Guid, Alias> aliases = [];
    private readonly HashSet<Guid> seen = [];
    private readonly List<Guid> scratch = [];
    private ulong clock;

    public DecodedImageCache(Func<ValueList<byte>, DecodedImage> decode, long byteLimit = DefaultByteLimit)
    {
        ArgumentNullException.ThrowIfNull(decode);
        this.decode = decode;
        this.byteLimit = Math.Max(0, byteLimit);
    }

    /// <summary>Bytes of decoded pixels held by the cache.</summary>
    public long Bytes { get; private set; }

    /// <summary>How many times an image was decoded.</summary>
    public int DecodeCount { get; private set; }

    public void BeginFrame(IReadOnlySet<Guid> validIds)
    {
        ArgumentNullException.ThrowIfNull(validIds);
        scratch.Clear();
        foreach (var id in aliases.Keys)
        {
            if (!validIds.Contains(id))
            {
                scratch.Add(id);
            }
        }
        foreach (var id in scratch)
        {
            aliases.Remove(id);
        }
        seen.Clear();
    }

    /// <summary>
    /// The decoded image for an element's bytes. An image that is not cached (it exceeds the budget) belongs to the
    /// caller, who disposes it after drawing; check <see cref="IsCached"/>.
    /// </summary>
    /// <exception cref="Annotator.Core.Geometry.RenderFailure">The bytes are not an image within the raster limits.</exception>
    public DecodedImage Image(ValueList<byte> data, Guid elementId)
    {
        clock++;
        if (aliases.TryGetValue(elementId, out var alias) && alias.Data == data
            && entries.TryGetValue(alias.Key, out var aliased))
        {
            aliased.Used = clock;
            seen.Add(alias.Key);
            return aliased.Image;
        }
        aliases.Remove(elementId);
        if (contentKeys.TryGetValue(data, out var key) && entries.TryGetValue(key, out var shared))
        {
            shared.Used = clock;
            seen.Add(key);
            aliases[elementId] = new Alias(data, key);
            return shared.Image;
        }
        var image = decode(data);
        DecodeCount++;
        var cost = image.Cost;
        if (cost > byteLimit)
        {
            return image;
        }
        while (Bytes > byteLimit - cost && Oldest() is { } oldest)
        {
            Remove(oldest);
        }
        key = Guid.NewGuid();
        entries[key] = new Entry(data, image, cost, clock);
        image.CacheOwned = true;
        contentKeys[data] = key;
        aliases[elementId] = new Alias(data, key);
        Bytes += cost;
        seen.Add(key);
        return image;
    }

    /// <summary>Keeps an element's cached image for this frame without decoding; false when it is not cached.</summary>
    public bool Touch(ValueList<byte> data, Guid elementId)
    {
        if (aliases.TryGetValue(elementId, out var alias) && alias.Data == data && entries.TryGetValue(alias.Key, out var entry))
        {
            entry.Used = ++clock;
            seen.Add(alias.Key);
            return true;
        }
        return false;
    }

    /// <summary>Whether <paramref name="image"/> is owned by the cache (otherwise the caller disposes it).</summary>
    public static bool IsCached(DecodedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.CacheOwned;
    }

    public void EndFrame()
    {
        scratch.Clear();
        foreach (var key in entries.Keys)
        {
            if (!seen.Contains(key))
            {
                scratch.Add(key);
            }
        }
        foreach (var key in scratch)
        {
            Remove(key);
        }
        scratch.Clear();
        foreach (var (id, alias) in aliases)
        {
            if (!entries.ContainsKey(alias.Key))
            {
                scratch.Add(id);
            }
        }
        foreach (var id in scratch)
        {
            aliases.Remove(id);
        }
    }

    public void Reset()
    {
        foreach (var entry in entries.Values)
        {
            entry.Image.CacheOwned = false;
            entry.Image.Dispose();
        }
        entries.Clear();
        contentKeys.Clear();
        aliases.Clear();
        seen.Clear();
        Bytes = 0;
    }

    public void Dispose() => Reset();

    private Guid? Oldest()
    {
        Guid? oldest = null;
        var used = ulong.MaxValue;
        foreach (var (key, entry) in entries)
        {
            if (entry.Used < used)
            {
                used = entry.Used;
                oldest = key;
            }
        }
        return oldest;
    }

    private void Remove(Guid key)
    {
        if (!entries.Remove(key, out var entry))
        {
            return;
        }
        contentKeys.Remove(entry.Data);
        Bytes -= entry.Cost;
        entry.Image.CacheOwned = false;
        entry.Image.Dispose();
    }

    private sealed class Entry(ValueList<byte> data, DecodedImage image, long cost, ulong used)
    {
        public ValueList<byte> Data { get; } = data;

        public DecodedImage Image { get; } = image;

        public long Cost { get; } = cost;

        public ulong Used { get; set; } = used;
    }

    private readonly record struct Alias(ValueList<byte> Data, Guid Key);

    /// <summary>Byte equality with a vectorized comparison and a hash of every byte.</summary>
    private sealed class ContentComparer : IEqualityComparer<ValueList<byte>>
    {
        public static ContentComparer Instance { get; } = new();

        public bool Equals(ValueList<byte> x, ValueList<byte> y) => x.AsSpan().SequenceEqual(y.AsSpan());

        public int GetHashCode(ValueList<byte> obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj.AsSpan());
            return hash.ToHashCode();
        }
    }
}

/// <summary>Swift <c>RasterAsset</c>: decoding with Image I/O's limits, here through WIC.</summary>
public static class RasterAsset
{
    /// <summary>
    /// Decodes the first frame with its EXIF orientation applied. Throws
    /// <see cref="Annotator.Core.Geometry.RenderFailure"/> (<c>InvalidSize</c>) for data over 128 MiB, unreadable data,
    /// or a frame that is empty, wider or taller than 16,384 pixels or larger than 40 million pixels.
    /// </summary>
    public static RasterImage Decode(ReadOnlySpan<byte> data)
    {
        using var wic = WicImaging.Create();
        return wic.DecodeImage(data);
    }

    /// <summary>Swift <c>PNGExporter.encode</c>.</summary>
    public static byte[] EncodePng(RasterImage image)
    {
        using var wic = WicImaging.Create();
        return wic.EncodePng(image);
    }

    /// <summary>
    /// Swift <c>RasterAsset.decode</c> where only its outcome matters, as when Open board checks every pasted image: decodes
    /// with the same limits and releases the pixels at once. Throws what <see cref="Decode"/> throws.
    /// </summary>
    public static unsafe void Check(ReadOnlySpan<byte> data)
    {
        using var wic = WicImaging.Create();
        var bitmap = wic.Decode(data);
        Interop.Native.Release(ref bitmap);
    }
}
