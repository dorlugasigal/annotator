using System.Runtime.InteropServices;

namespace Annotator.RenderHarness;

/// <summary>
/// H.264 MP4 files read and written with Windows' own Media Foundation, so the live replays need no external tool: the
/// Source Reader decodes with the built-in H.264 decoder and converts to 32-bit BGR, and the Sink Writer converts BGR to
/// NV12 and encodes with the built-in H.264 encoder. Only software transforms are used (no hardware encoder or decoder),
/// so results do not depend on the graphics driver. COM interfaces are called through their vtables, as Annotator.Platform
/// does for composition interop; the slot numbers are those of the Windows SDK's mfreadwrite.h and mfobjects.h.
/// </summary>
internal static unsafe partial class MediaFoundation
{
    private const uint Version = 0x0002_0070; // MF_SDK_VERSION << 16 | MF_API_VERSION
    private const uint StartupLite = 1;       // MFSTARTUP_LITE: no sockets
    private static readonly Lock s_gate = new();
    private static int s_users;

    internal static readonly Guid MajorType = new(0x48eba18e, 0xf8c9, 0x4687, 0xbf, 0x11, 0x0a, 0x74, 0xc9, 0xf9, 0x6a, 0x8f);
    internal static readonly Guid Subtype = new(0xf7e34c9a, 0x42e8, 0x4714, 0xb7, 0x4b, 0xcb, 0x29, 0xd7, 0x2c, 0x35, 0xe5);
    internal static readonly Guid FrameSize = new(0x1652c33d, 0xd6b2, 0x4012, 0xb8, 0x34, 0x72, 0x03, 0x08, 0x49, 0xa3, 0x7d);
    internal static readonly Guid FrameRate = new(0xc459a2e8, 0x3d2c, 0x4e44, 0xb1, 0x32, 0xfe, 0xe5, 0x15, 0x6c, 0x7b, 0xb0);
    internal static readonly Guid PixelAspectRatio = new(0xc6376a1e, 0x8d0a, 0x4027, 0xbe, 0x45, 0x6d, 0x9a, 0x0a, 0xd3, 0x9b, 0xb6);
    internal static readonly Guid InterlaceMode = new(0xe2724bb8, 0xe676, 0x4806, 0xb4, 0xb2, 0xa8, 0xd6, 0xef, 0xb4, 0x4c, 0xcd);
    internal static readonly Guid AverageBitRate = new(0x20332624, 0xfb0d, 0x4d9e, 0xbd, 0x0d, 0xcb, 0xf6, 0x78, 0x6c, 0x10, 0x2e);
    internal static readonly Guid DefaultStride = new(0x644b4e48, 0x1e02, 0x4516, 0xb0, 0xeb, 0xc0, 0x1c, 0xa9, 0xd4, 0x9a, 0xc6);
    internal static readonly Guid Mpeg2Profile = new(0xad76a80b, 0x2d5c, 0x4e0b, 0xb3, 0x75, 0x64, 0xe5, 0x20, 0x13, 0x70, 0x36);
    internal static readonly Guid MinimumDisplayAperture = new(0xd7388766, 0x18fe, 0x48c6, 0xa1, 0x77, 0xee, 0x89, 0x48, 0x67, 0xc8, 0xc4);
    internal static readonly Guid YuvMatrix = new(0x3e23d450, 0x2c75, 0x4d25, 0xa0, 0x0e, 0xb9, 0x16, 0x70, 0xd1, 0x23, 0x27);
    internal static readonly Guid NominalRange = new(0xc21b8ee5, 0xb956, 0x4071, 0x8d, 0xaf, 0x32, 0x5e, 0xdf, 0x5c, 0xab, 0x11);
    internal static readonly Guid PresentationDuration = new(0x6c990d33, 0xbb8e, 0x477a, 0x85, 0x98, 0x0d, 0x5d, 0x96, 0xfc, 0xd8, 0x8a);
    internal static readonly Guid ContainerType = new(0x150ff23f, 0x4abc, 0x478b, 0xac, 0x4f, 0xe1, 0x91, 0x6f, 0xba, 0x1c, 0xca);
    internal static readonly Guid ContainerMpeg4 = new(0xdc6cd05d, 0xb9d0, 0x40ef, 0xbd, 0x35, 0xfa, 0x62, 0x2c, 0x1a, 0xb2, 0x8a);
    internal static readonly Guid DisableThrottling = new(0x08b845d8, 0x2b74, 0x4afe, 0x9d, 0x53, 0xbe, 0x16, 0xd2, 0xd5, 0xae, 0x4f);
    internal static readonly Guid EnableVideoProcessing = new(0xfb394f3d, 0xccf1, 0x42ee, 0xbb, 0xb3, 0xf9, 0xb8, 0x45, 0xd5, 0x68, 0x1d);
    internal static readonly Guid Video = new(0x73646976, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    internal static readonly Guid Rgb32 = new(0x00000016, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    internal static readonly Guid H264 = new(0x34363248, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    internal static readonly Guid RateControlMode = new(0x1c0608e9, 0x370c, 0x4710, 0x8a, 0x58, 0xcb, 0x61, 0x81, 0xc4, 0x24, 0x23);
    internal static readonly Guid Quality = new(0xfcbf57a3, 0x7ea5, 0x4b0c, 0x96, 0x44, 0x69, 0xb4, 0x0c, 0x39, 0xc3, 0x91);
    internal static readonly Guid TwoDimensionalBuffer = new(0x7dc9d5f9, 0x9ed9, 0x44ec, 0x9b, 0xbf, 0x06, 0x00, 0xbb, 0x58, 0x9f, 0xbb);

    /// <summary>Starts Media Foundation for the caller; disposing the result stops it once nobody uses it.</summary>
    public static IDisposable Start()
    {
        lock (s_gate)
        {
            if (s_users == 0)
            {
                // Either apartment works for the Source Reader and Sink Writer; S_FALSE and RPC_E_CHANGED_MODE are fine here.
                _ = CoInitializeEx(null, 0);
                Check(MFStartup(Version, StartupLite), "MFStartup");
            }
            s_users++;
        }
        return new Session();
    }

    internal static void** Table(void* unknown) => *(void***)unknown;

    internal static void Release(ref void* pointer)
    {
        if (pointer is not null)
        {
            _ = ((delegate* unmanaged[Stdcall]<void*, uint>)Table(pointer)[2])(pointer);
            pointer = null;
        }
    }

    internal static void Check(int result, string operation)
    {
        if (result < 0)
        {
            var name = unchecked((uint)result) switch
            {
                0xC00D36B4 => " (MF_E_INVALIDMEDIATYPE)",
                0xC00D36C4 => " (MF_E_UNSUPPORTED_BYTESTREAM_TYPE)",
                0xC00D5212 => " (MF_E_TOPO_CODEC_NOT_FOUND: Windows has no H.264 codec, as in N editions without the Media Feature Pack)",
                0xC00D36E6 => " (MF_E_ATTRIBUTENOTFOUND)",
                0xC00D3E85 => " (MF_E_NOT_FOUND)",
                0x80070002 => " (the file was not found)",
                _ => "",
            };
            throw new InvalidOperationException($"Media Foundation: {operation} failed with 0x{unchecked((uint)result):X8}{name}.");
        }
    }

    /// <summary>A new attribute store or media type with <paramref name="capacity"/> items.</summary>
    internal static void* CreateAttributes(uint capacity)
    {
        void* attributes;
        Check(MFCreateAttributes(&attributes, capacity), "MFCreateAttributes");
        return attributes;
    }

    internal static void* CreateMediaType()
    {
        void* type;
        Check(MFCreateMediaType(&type), "MFCreateMediaType");
        return type;
    }

    internal static void SetUInt32(void* attributes, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<void*, Guid*, uint, int>)Table(attributes)[21])(attributes, &key, value), "IMFAttributes::SetUINT32");

    internal static void SetUInt64(void* attributes, Guid key, ulong value) =>
        Check(((delegate* unmanaged[Stdcall]<void*, Guid*, ulong, int>)Table(attributes)[22])(attributes, &key, value), "IMFAttributes::SetUINT64");

    internal static void SetGuid(void* attributes, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, int>)Table(attributes)[24])(attributes, &key, &value), "IMFAttributes::SetGUID");

    internal static bool TryGetUInt32(void* attributes, Guid key, out uint value)
    {
        uint read;
        var result = ((delegate* unmanaged[Stdcall]<void*, Guid*, uint*, int>)Table(attributes)[7])(attributes, &key, &read);
        value = read;
        return result >= 0;
    }

    internal static bool TryGetUInt64(void* attributes, Guid key, out ulong value)
    {
        ulong read;
        var result = ((delegate* unmanaged[Stdcall]<void*, Guid*, ulong*, int>)Table(attributes)[8])(attributes, &key, &read);
        value = read;
        return result >= 0;
    }

    internal static bool TryGetBlob(void* attributes, Guid key, Span<byte> destination)
    {
        uint size;
        fixed (byte* buffer = destination)
        {
            return ((delegate* unmanaged[Stdcall]<void*, Guid*, byte*, uint, uint*, int>)Table(attributes)[15])(
                attributes, &key, buffer, (uint)destination.Length, &size) >= 0 && size == destination.Length;
        }
    }

    /// <summary>A sample of one memory buffer holding <paramref name="length"/> bytes, which <paramref name="fill"/> writes.</summary>
    internal static void* CreateSample(uint length, Action<IntPtr> fill, long time, long duration)
    {
        void* buffer = null, sample = null;
        try
        {
            Check(MFCreateMemoryBuffer(length, &buffer), "MFCreateMemoryBuffer");
            byte* data;
            uint maximum, current;
            Check(((delegate* unmanaged[Stdcall]<void*, byte**, uint*, uint*, int>)Table(buffer)[3])(buffer, &data, &maximum, &current), "IMFMediaBuffer::Lock");
            try
            {
                fill((IntPtr)data);
            }
            finally
            {
                _ = ((delegate* unmanaged[Stdcall]<void*, int>)Table(buffer)[4])(buffer);
            }
            Check(((delegate* unmanaged[Stdcall]<void*, uint, int>)Table(buffer)[6])(buffer, length), "IMFMediaBuffer::SetCurrentLength");
            Check(MFCreateSample(&sample), "MFCreateSample");
            Check(((delegate* unmanaged[Stdcall]<void*, void*, int>)Table(sample)[42])(sample, buffer), "IMFSample::AddBuffer");
            Check(((delegate* unmanaged[Stdcall]<void*, long, int>)Table(sample)[36])(sample, time), "IMFSample::SetSampleTime");
            Check(((delegate* unmanaged[Stdcall]<void*, long, int>)Table(sample)[38])(sample, duration), "IMFSample::SetSampleDuration");
            var made = sample;
            sample = null;
            return made;
        }
        finally
        {
            Release(ref buffer);
            Release(ref sample);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(void* reserved, uint model);

    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    private static partial int MFShutdown();

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateAttributes(void** attributes, uint initialSize);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateMediaType(void** type);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateSample(void** sample);

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateMemoryBuffer(uint maximumLength, void** buffer);

    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MFCreateSourceReaderFromURL(string url, void* attributes, void** reader);

    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MFCreateSinkWriterFromURL(string url, void* byteStream, void* attributes, void** writer);

    private sealed class Session : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (s_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                if (--s_users == 0)
                {
                    _ = MFShutdown();
                }
            }
        }
    }
}

/// <summary>PROPVARIANT with an 8-byte integer (VT_I8 or VT_UI8); 24 bytes on 64-bit Windows.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)]
    public ushort Type;

    [FieldOffset(8)]
    public long Value;
}

/// <summary>
/// Decodes a video file's first video stream to 32-bit BGRA frames, top row first, cropped to the stream's display area
/// (an H.264 stream of 1,440 by 900 pixels decodes to 1,440 by 912 with a 900-row display area). Alpha is 255.
/// </summary>
internal sealed unsafe class VideoReader : IDisposable
{
    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint MediaSource = 0xFFFFFFFF;
    private const uint EndOfStream = 0x2;
    private const uint MediaTypeChanged = 0x20;
    private readonly IDisposable _session;
    private void* _reader;
    private int _codedWidth;
    private int _codedHeight;
    private int _stride;
    private int _left;
    private int _top;

    private VideoReader(IDisposable session, void* reader)
    {
        _session = session;
        _reader = reader;
    }

    /// <summary>Frame width of the display area in pixels.</summary>
    public int Width { get; private set; }

    /// <summary>Frame height of the display area in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>The file's duration in seconds.</summary>
    public double Duration { get; private set; }

    /// <summary>The stream's nominal frame rate (frames per second), or 0 when it has none.</summary>
    public double FrameRate { get; private set; }

    /// <summary>Frames read so far.</summary>
    public int Frames { get; private set; }

    public static VideoReader Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The video was not found at {path}.", path);
        }
        IDisposable session = MediaFoundation.Start();
        void* attributes = null, reader = null, type = null;
        try
        {
            attributes = MediaFoundation.CreateAttributes(1);
            MediaFoundation.SetUInt32(attributes, MediaFoundation.EnableVideoProcessing, 1);
            MediaFoundation.Check(MediaFoundation.MFCreateSourceReaderFromURL(Path.GetFullPath(path), attributes, &reader), "MFCreateSourceReaderFromURL");
            var table = MediaFoundation.Table(reader);
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<void*, uint, int, int>)table[4])(reader, AllStreams, 0), "IMFSourceReader::SetStreamSelection");
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<void*, uint, int, int>)table[4])(reader, FirstVideoStream, 1), "IMFSourceReader::SetStreamSelection");
            type = MediaFoundation.CreateMediaType();
            MediaFoundation.SetGuid(type, MediaFoundation.MajorType, MediaFoundation.Video);
            MediaFoundation.SetGuid(type, MediaFoundation.Subtype, MediaFoundation.Rgb32);
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<void*, uint, uint*, void*, int>)table[7])(reader, FirstVideoStream, null, type),
                "IMFSourceReader::SetCurrentMediaType (32-bit BGR)");
            var video = new VideoReader(session, reader);
            reader = null;
            try
            {
                video.ReadMediaType();
                var duration = new PropVariant();
                var key = MediaFoundation.PresentationDuration;
                if (((delegate* unmanaged[Stdcall]<void*, uint, Guid*, PropVariant*, int>)table[12])(video._reader, MediaSource, &key, &duration) >= 0)
                {
                    video.Duration = duration.Value / 1e7;
                }
            }
            catch
            {
                video.Dispose();
                throw;
            }
            return video;
        }
        catch
        {
            session.Dispose();
            throw;
        }
        finally
        {
            MediaFoundation.Release(ref attributes);
            MediaFoundation.Release(ref type);
            MediaFoundation.Release(ref reader);
        }
    }

    /// <summary>
    /// Reads the next frame into <paramref name="destination"/> (Width x Height x 4 bytes, top row first) and its time in
    /// seconds; false at the end of the stream.
    /// </summary>
    public bool Read(Span<byte> destination, out double time)
    {
        ObjectDisposedException.ThrowIf(_reader is null, this);
        while (true)
        {
            uint stream, flags;
            long timestamp;
            void* sample = null;
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<void*, uint, uint, uint*, uint*, long*, void**, int>)MediaFoundation.Table(_reader)[9])(
                _reader, FirstVideoStream, 0, &stream, &flags, &timestamp, &sample), "IMFSourceReader::ReadSample");
            try
            {
                if ((flags & MediaTypeChanged) != 0)
                {
                    ReadMediaType();
                }
                if ((flags & EndOfStream) != 0 && sample is null)
                {
                    time = 0;
                    return false;
                }
                if (sample is null)
                {
                    continue;
                }
                if (destination.Length < Width * Height * 4)
                {
                    throw new ArgumentException($"The frame needs {Width * Height * 4} bytes.", nameof(destination));
                }
                Copy(sample, destination);
                time = timestamp / 1e7;
                Frames++;
                return true;
            }
            finally
            {
                MediaFoundation.Release(ref sample);
            }
        }
    }

    /// <summary>
    /// The first frame at or after <paramref name="seconds"/> from the first frame (read from the start), as an image. Times
    /// count from the first frame, as ffmpeg's do: Media Foundation leaves out the MP4 edit list, so its first frame can carry
    /// a time of one or two frames.
    /// </summary>
    public static Annotator.Rendering.Imaging.RasterImage FrameAt(string path, double seconds)
    {
        using var reader = Open(path);
        var pixels = new byte[reader.Width * reader.Height * 4];
        var found = false;
        double? first = null;
        // Half a frame early counts: timestamps are rounded to 100 ns.
        while (reader.Read(pixels, out var time))
        {
            first ??= time;
            if (time - first.Value >= seconds - 0.004)
            {
                found = true;
                break;
            }
        }
        if (!found)
        {
            throw new InvalidOperationException($"{path} has no frame at {seconds:F3} s.");
        }
        return new Annotator.Rendering.Imaging.RasterImage(reader.Width, reader.Height, reader.Width * 4, pixels);
    }

    public void Dispose()
    {
        if (_reader is null)
        {
            return;
        }
        MediaFoundation.Release(ref _reader);
        _session.Dispose();
    }

    private void ReadMediaType()
    {
        void* type = null;
        try
        {
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)MediaFoundation.Table(_reader)[6])(_reader, FirstVideoStream, &type),
                "IMFSourceReader::GetCurrentMediaType");
            if (!MediaFoundation.TryGetUInt64(type, MediaFoundation.FrameSize, out var size))
            {
                throw new InvalidOperationException("The decoded video has no frame size.");
            }
            _codedWidth = (int)(size >> 32);
            _codedHeight = (int)(size & 0xFFFFFFFF);
            _stride = MediaFoundation.TryGetUInt32(type, MediaFoundation.DefaultStride, out var stride) ? unchecked((int)stride) : _codedWidth * 4;
            Span<byte> area = stackalloc byte[16];
            if (MediaFoundation.TryGetBlob(type, MediaFoundation.MinimumDisplayAperture, area))
            {
                // MFVideoArea: MFOffset x and y (WORD fraction, SHORT value), then SIZE (LONG cx, cy).
                _left = BitConverter.ToInt16(area[2..4]);
                _top = BitConverter.ToInt16(area[6..8]);
                Width = BitConverter.ToInt32(area[8..12]);
                Height = BitConverter.ToInt32(area[12..16]);
            }
            else
            {
                _left = _top = 0;
                Width = _codedWidth;
                Height = _codedHeight;
            }
            if (Width <= 0 || Height <= 0 || _left < 0 || _top < 0 || _left + Width > _codedWidth || _top + Height > _codedHeight)
            {
                throw new InvalidOperationException($"The decoded video has an unusable display area ({_left}, {_top}, {Width} x {Height} in {_codedWidth} x {_codedHeight}).");
            }
            FrameRate = MediaFoundation.TryGetUInt64(type, MediaFoundation.FrameRate, out var rate) && (rate & 0xFFFFFFFF) != 0
                ? (rate >> 32) / (double)(rate & 0xFFFFFFFF)
                : 0;
        }
        finally
        {
            MediaFoundation.Release(ref type);
        }
    }

    private void Copy(void* sample, Span<byte> destination)
    {
        void* buffer = null, planar = null;
        try
        {
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<void*, void**, int>)MediaFoundation.Table(sample)[41])(sample, &buffer), "IMFSample::ConvertToContiguousBuffer");
            var iid = MediaFoundation.TwoDimensionalBuffer;
            byte* scan0 = null;
            var pitch = 0;
            var locked2D = ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)MediaFoundation.Table(buffer)[0])(buffer, &iid, &planar) >= 0
                           && ((delegate* unmanaged[Stdcall]<void*, byte**, int*, int>)MediaFoundation.Table(planar)[3])(planar, &scan0, &pitch) >= 0;
            if (!locked2D)
            {
                uint maximum, current;
                byte* data;
                MediaFoundation.Check(((delegate* unmanaged[Stdcall]<void*, byte**, uint*, uint*, int>)MediaFoundation.Table(buffer)[3])(buffer, &data, &maximum, &current),
                    "IMFMediaBuffer::Lock");
                // A negative default stride means bottom-up rows: the first row in memory is the last one.
                pitch = _stride;
                scan0 = pitch < 0 ? data + (long)(-pitch) * (_codedHeight - 1) : data;
            }
            try
            {
                var row = Width * 4;
                for (var y = 0; y < Height; y++)
                {
                    var source = new ReadOnlySpan<byte>(scan0 + (long)(y + _top) * pitch + _left * 4, row);
                    var target = destination.Slice(y * row, row);
                    source.CopyTo(target);
                    for (var x = 3; x < row; x += 4)
                    {
                        target[x] = 255;
                    }
                }
            }
            finally
            {
                if (locked2D)
                {
                    _ = ((delegate* unmanaged[Stdcall]<void*, int>)MediaFoundation.Table(planar)[4])(planar);
                }
                else
                {
                    _ = ((delegate* unmanaged[Stdcall]<void*, int>)MediaFoundation.Table(buffer)[4])(buffer);
                }
            }
        }
        finally
        {
            MediaFoundation.Release(ref planar);
            MediaFoundation.Release(ref buffer);
        }
    }
}

/// <summary>
/// Encodes 32-bit BGRA frames, top row first, as an H.264 MP4 file at a constant frame rate with the built-in encoder in
/// quality mode. Width and height must be even.
/// </summary>
internal sealed unsafe class VideoWriter : IDisposable
{
    private const uint Progressive = 2;
    private const uint ProfileHigh = 100;
    private readonly IDisposable _session;
    private readonly int _rate;
    private void* _writer;
    private uint _stream;
    private long _written;

    private VideoWriter(IDisposable session, void* writer, int width, int height, int rate)
    {
        _session = session;
        _writer = writer;
        Width = width;
        Height = height;
        _rate = rate;
    }

    public int Width { get; }

    public int Height { get; }

    /// <param name="quality">The encoder's quality, 0 to 100 (CODECAPI_AVEncCommonQuality in quality mode).</param>
    public static VideoWriter Create(string path, int width, int height, int rate = 60, uint quality = 85)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException($"H.264 needs an even frame size, not {width} x {height}.");
        }
        IDisposable session = MediaFoundation.Start();
        void* attributes = null, writer = null, output = null, input = null, parameters = null;
        try
        {
            File.Delete(path);
            attributes = MediaFoundation.CreateAttributes(2);
            MediaFoundation.SetGuid(attributes, MediaFoundation.ContainerType, MediaFoundation.ContainerMpeg4);
            MediaFoundation.SetUInt32(attributes, MediaFoundation.DisableThrottling, 1);
            MediaFoundation.Check(MediaFoundation.MFCreateSinkWriterFromURL(Path.GetFullPath(path), null, attributes, &writer), "MFCreateSinkWriterFromURL");
            var size = ((ulong)(uint)width << 32) | (uint)height;
            var frameRate = ((ulong)(uint)rate << 32) | 1;
            output = MediaFoundation.CreateMediaType();
            MediaFoundation.SetGuid(output, MediaFoundation.MajorType, MediaFoundation.Video);
            MediaFoundation.SetGuid(output, MediaFoundation.Subtype, MediaFoundation.H264);
            // The encoder needs a bit rate even in quality mode; it is the budget's starting point, not a cap.
            MediaFoundation.SetUInt32(output, MediaFoundation.AverageBitRate, (uint)Math.Min(uint.MaxValue, (long)width * height * rate / 4));
            MediaFoundation.SetUInt32(output, MediaFoundation.InterlaceMode, Progressive);
            MediaFoundation.SetUInt32(output, MediaFoundation.Mpeg2Profile, ProfileHigh);
            MediaFoundation.SetUInt64(output, MediaFoundation.FrameSize, size);
            MediaFoundation.SetUInt64(output, MediaFoundation.FrameRate, frameRate);
            MediaFoundation.SetUInt64(output, MediaFoundation.PixelAspectRatio, (1UL << 32) | 1);
            uint stream;
            var table = MediaFoundation.Table(writer);
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<void*, void*, uint*, int>)table[3])(writer, output, &stream), "IMFSinkWriter::AddStream (H.264)");
            input = MediaFoundation.CreateMediaType();
            MediaFoundation.SetGuid(input, MediaFoundation.MajorType, MediaFoundation.Video);
            MediaFoundation.SetGuid(input, MediaFoundation.Subtype, MediaFoundation.Rgb32);
            MediaFoundation.SetUInt32(input, MediaFoundation.InterlaceMode, Progressive);
            MediaFoundation.SetUInt64(input, MediaFoundation.FrameSize, size);
            MediaFoundation.SetUInt64(input, MediaFoundation.FrameRate, frameRate);
            MediaFoundation.SetUInt64(input, MediaFoundation.PixelAspectRatio, (1UL << 32) | 1);
            // A positive stride says the rows are top-down.
            MediaFoundation.SetUInt32(input, MediaFoundation.DefaultStride, (uint)(width * 4));
            parameters = MediaFoundation.CreateAttributes(2);
            MediaFoundation.SetUInt32(parameters, MediaFoundation.RateControlMode, 3); // eAVEncCommonRateControlMode_Quality
            MediaFoundation.SetUInt32(parameters, MediaFoundation.Quality, Math.Min(100u, quality));
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<void*, uint, void*, void*, int>)table[4])(writer, stream, input, parameters),
                "IMFSinkWriter::SetInputMediaType (32-bit BGR to H.264)");
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<void*, int>)table[5])(writer), "IMFSinkWriter::BeginWriting");
            var video = new VideoWriter(session, writer, width, height, rate) { _stream = stream };
            writer = null;
            return video;
        }
        catch
        {
            session.Dispose();
            throw;
        }
        finally
        {
            MediaFoundation.Release(ref attributes);
            MediaFoundation.Release(ref output);
            MediaFoundation.Release(ref input);
            MediaFoundation.Release(ref parameters);
            MediaFoundation.Release(ref writer);
        }
    }

    /// <summary>
    /// Writes the next frame: the top-left Width x Height pixels of <paramref name="bgra"/>, whose rows are
    /// <paramref name="stride"/> bytes apart, shown for one frame at the writer's rate.
    /// </summary>
    public void Write(ReadOnlySpan<byte> bgra, int stride)
    {
        ObjectDisposedException.ThrowIf(_writer is null, this);
        if (stride < Width * 4 || bgra.Length < (long)stride * (Height - 1) + Width * 4)
        {
            throw new ArgumentException("The frame is smaller than the video.", nameof(bgra));
        }
        var start = _written * 10_000_000 / _rate;
        var end = (_written + 1) * 10_000_000 / _rate;
        var row = Width * 4;
        var length = (uint)(row * Height);
        void* sample;
        fixed (byte* source = bgra)
        {
            var from = (IntPtr)source;
            sample = MediaFoundation.CreateSample(length, data =>
            {
                for (var y = 0; y < Height; y++)
                {
                    new ReadOnlySpan<byte>((byte*)from + (long)y * stride, row).CopyTo(new Span<byte>((byte*)data + (long)y * row, row));
                }
            }, start, end - start);
        }
        try
        {
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<void*, uint, void*, int>)MediaFoundation.Table(_writer)[6])(_writer, _stream, sample),
                "IMFSinkWriter::WriteSample");
            _written++;
        }
        finally
        {
            MediaFoundation.Release(ref sample);
        }
    }

    /// <summary>Encodes what is left and closes the file.</summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_writer is null, this);
        try
        {
            MediaFoundation.Check(((delegate* unmanaged[Stdcall]<void*, int>)MediaFoundation.Table(_writer)[11])(_writer), "IMFSinkWriter::Finalize");
        }
        finally
        {
            MediaFoundation.Release(ref _writer);
            _session.Dispose();
        }
    }

    public void Dispose()
    {
        if (_writer is null)
        {
            return;
        }
        MediaFoundation.Release(ref _writer);
        _session.Dispose();
    }
}
