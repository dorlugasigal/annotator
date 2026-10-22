using System.Buffers.Binary;
using Annotator.App.Native;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Rendering.Imaging;
using Annotator.Workspace;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Ole;
using Windows.Win32.UI.Shell;

namespace Annotator.App.Shell;

/// <summary>
/// The Windows clipboard for the workspace (WIN-CORE-07, WIN-INP-10): copied objects in the registered format
/// <c>app.annotator.annotations</c> with the UTF-8 board payload, and images from other apps as PNG, bitmap or an image
/// file, decoded with WIC within Core's raster limits and stored as PNG (Mac ImageClipboard and insertImage). Use it on
/// the app thread; a message-only window owns what Annotator copies.
/// </summary>
internal sealed unsafe class SystemClipboard : IWorkspaceClipboard, IDisposable
{
    private const int MaximumBytes = 128 * 1_024 * 1_024;
    private const int OpenAttempts = 5;
    private const string BusyMessage = "Another app is using the clipboard. Try again.";
    private const string ImageSizeMessage = "The copied image is empty or exceeds 128 MB.";
    private const string ImageFileSizeMessage = "The copied image file exceeds 128 MB.";
    private const string BoardSizeMessage = "The file exceeds the safe import memory budget.";

    private readonly Owner _owner = new();
    private readonly uint _annotations = Register(AnnotationClipboard.TypeName);
    private readonly uint _png = Register("PNG");
    private readonly uint _pngType = Register("image/png");

    public ulong ChangeCount => PInvoke.GetClipboardSequenceNumber();

    public byte[]? ReadAnnotations()
    {
        if (!PInvoke.IsClipboardFormatAvailable(_annotations))
        {
            return null;
        }
        using Session session = Open();
        byte[]? data = Read(_annotations, BoardSizeMessage);
        if (data is null)
        {
            return null;
        }
        // Clipboard memory can be larger than the payload; JSON never ends in NUL bytes.
        int length = data.Length;
        while (length > 0 && data[length - 1] == 0)
        {
            length--;
        }
        return length == data.Length ? data : data[..length];
    }

    public bool WriteAnnotations(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!TryOpen())
        {
            return false;
        }
        using var session = new Session();
        if (!PInvoke.EmptyClipboard())
        {
            return false;
        }
        HGLOBAL memory = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)Math.Max(1, payload.Length));
        if (memory.IsNull)
        {
            return false;
        }
        void* target = PInvoke.GlobalLock(memory);
        if (target is null)
        {
            PInvoke.GlobalFree(memory);
            return false;
        }
        payload.AsSpan().CopyTo(new Span<byte>(target, payload.Length));
        PInvoke.GlobalUnlock(memory);
        if (PInvoke.SetClipboardData(_annotations, new HANDLE((nint)memory.Value)).IsNull)
        {
            // The clipboard did not take the memory, so it is still ours to free.
            PInvoke.GlobalFree(memory);
            return false;
        }
        return true;
    }

    public ClipboardImage? ReadImage()
    {
        byte[]? encoded;
        string? file = null;
        using (Session session = Open())
        {
            encoded = Available(_png) ? Read(_png, ImageSizeMessage)
                : Available(_pngType) ? Read(_pngType, ImageSizeMessage)
                : Available((uint)CLIPBOARD_FORMAT.CF_DIBV5) ? Bitmap(Read((uint)CLIPBOARD_FORMAT.CF_DIBV5, ImageSizeMessage))
                : Available((uint)CLIPBOARD_FORMAT.CF_DIB) ? Bitmap(Read((uint)CLIPBOARD_FORMAT.CF_DIB, ImageSizeMessage))
                : null;
            if (encoded is null && Available((uint)CLIPBOARD_FORMAT.CF_HDROP))
            {
                file = FirstFile();
            }
        }
        if (encoded is null && file is not null)
        {
            if (new FileInfo(file).Length > MaximumBytes)
            {
                throw CanvasError.InvalidDocument(ImageFileSizeMessage);
            }
            encoded = File.ReadAllBytes(file);
        }
        if (encoded is null)
        {
            return null;
        }
        if (encoded.Length == 0)
        {
            throw CanvasError.InvalidDocument(ImageSizeMessage);
        }
        // Mac insertImage reports RasterAsset's own failure, such as the raster budget's message.
        return Files.ImageImport.Prepare(encoded);
    }

    public void Dispose() => _owner.Dispose();

    private static uint Register(string name)
    {
        uint format = PInvoke.RegisterClipboardFormat(name);
        return format != 0 ? format : throw CallbackErrors.LastError($"RegisterClipboardFormat({name})");
    }

    private static bool Available(uint format) => PInvoke.IsClipboardFormatAvailable(format);

    /// <summary>Opens the clipboard for reading; another app can hold it for a moment, so this tries a few times.</summary>
    private Session Open() => TryOpen() ? new Session() : throw new IOException(BusyMessage);

    private bool TryOpen()
    {
        for (int attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (PInvoke.OpenClipboard(_owner.Handle))
            {
                return true;
            }
            Thread.Sleep(10);
        }
        return false;
    }

    /// <summary>A copy of the clipboard's data in <paramref name="format"/>, or null when it is gone.</summary>
    private static byte[]? Read(uint format, string tooLarge)
    {
        HANDLE handle = PInvoke.GetClipboardData(format);
        if (handle.IsNull)
        {
            return null;
        }
        var memory = new HGLOBAL((void*)handle.Value);
        nuint size = PInvoke.GlobalSize(memory);
        if (size > MaximumBytes)
        {
            throw CanvasError.InvalidDocument(tooLarge);
        }
        void* source = PInvoke.GlobalLock(memory);
        if (source is null)
        {
            throw new IOException(BusyMessage);
        }
        try
        {
            return new ReadOnlySpan<byte>(source, (int)size).ToArray();
        }
        finally
        {
            PInvoke.GlobalUnlock(memory);
        }
    }

    /// <summary>A bitmap file for WIC from clipboard DIB data: the 14-byte file header the clipboard leaves out.</summary>
    private static byte[]? Bitmap(byte[]? dib)
    {
        if (dib is null)
        {
            return null;
        }
        if (dib.Length < 40)
        {
            throw new RenderFailure(RenderFailureKind.InvalidSize);
        }
        uint header = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
        uint used = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
        // BITMAPINFOHEADER keeps BI_BITFIELDS (3) and BI_ALPHABITFIELDS (6) masks after the header; later headers hold them.
        long masks = header == 40 ? compression == 3 ? 12 : compression == 6 ? 16 : 0 : 0;
        long colors = used != 0 ? used : bits <= 8 ? 1L << bits : 0;
        long offset = 14 + header + masks + colors * 4;
        if (offset > 14 + dib.Length)
        {
            throw new RenderFailure(RenderFailureKind.InvalidSize);
        }
        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)file.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), (uint)offset);
        dib.CopyTo(file, 14);
        return file;
    }

    /// <summary>The first file of a copied file list (Mac: the pasteboard's file URL).</summary>
    private static string? FirstFile()
    {
        HANDLE handle = PInvoke.GetClipboardData((uint)CLIPBOARD_FORMAT.CF_HDROP);
        if (handle.IsNull)
        {
            return null;
        }
        var drop = new HDROP((void*)handle.Value);
        uint length = PInvoke.DragQueryFile(drop, 0, null, 0);
        if (length == 0)
        {
            return null;
        }
        var path = new char[length + 1];
        fixed (char* buffer = path)
        {
            uint copied = PInvoke.DragQueryFile(drop, 0, buffer, (uint)path.Length);
            return copied == 0 ? null : new string(path, 0, (int)copied);
        }
    }

    /// <summary>Closes the clipboard when a read or write is done.</summary>
    private readonly struct Session : IDisposable
    {
        public void Dispose() => PInvoke.CloseClipboard();
    }

    private sealed class Owner() : HiddenWindow("Annotator.App.Clipboard", messageOnly: true);
}
