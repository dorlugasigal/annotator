using System.Runtime.InteropServices;
using Annotator.App.Native;
using Annotator.Core;
using Annotator.Workspace.Artwork;
using Annotator.Workspace.Canvas;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.App.Overlay;

/// <summary>
/// The canvas cursors as real system cursors for each monitor scale (WIN-OVR-11): the arrow, text and crosshair from
/// Windows, and the footprint and rotation cursors drawn by <see cref="CursorArtwork"/>. Drawn cursors are cached; like
/// the Mac's ToolCursorFactory, the cache holds at most 32 and then starts over, keeping the cursors canvases still show.
/// Use it on the overlay thread.
/// </summary>
internal sealed unsafe class CanvasCursors : IDisposable
{
    private const int CacheLimit = 32;
    private static readonly HCURSOR s_ibeam = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_IBEAM);
    private static readonly HCURSOR s_crosshair = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_CROSS);

    private readonly Dictionary<(CanvasCursorKind Kind, Tool Tool, double Width, double Scale), nint> _drawn = [];
    private readonly Func<IEnumerable<nint>> _inUse;
    private bool _disposed;

    /// <param name="inUse">The cursors canvases show now; they survive a cache reset.</param>
    public CanvasCursors(Func<IEnumerable<nint>> inUse)
    {
        _inUse = inUse;
    }

    /// <summary>
    /// The HCURSOR for <paramref name="cursor"/> at <paramref name="scale"/>, or 0 for the standard arrow. A cursor that
    /// cannot be created is reported through <see cref="Platform.NativeCallException"/>; the caller keeps the arrow then.
    /// </summary>
    public nint Get(CanvasCursor cursor, double scale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        switch (cursor.Kind)
        {
            case CanvasCursorKind.Arrow:
                return 0;
            case CanvasCursorKind.IBeam:
                return (nint)s_ibeam;
            case CanvasCursorKind.Crosshair:
                return (nint)s_crosshair;
        }
        var key = (cursor.Kind, cursor.Kind == CanvasCursorKind.Footprint ? cursor.Tool : Tool.Select,
                   cursor.Kind == CanvasCursorKind.Footprint ? cursor.Width : 0, scale);
        if (_drawn.TryGetValue(key, out nint known))
        {
            return known;
        }
        CursorArtwork.Image image = cursor.Kind == CanvasCursorKind.Footprint
            ? CursorArtwork.Footprint(cursor, scale)
            : CursorArtwork.Rotation(scale);
        if (_drawn.Count >= CacheLimit)
        {
            Trim();
        }
        nint created = Create(image);
        _drawn[key] = created;
        return created;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (nint cursor in _drawn.Values)
        {
            PInvoke.DestroyIcon((HICON)cursor);
        }
        _drawn.Clear();
    }

    private void Trim()
    {
        var keep = new HashSet<nint>(_inUse());
        foreach ((var key, nint cursor) in _drawn.ToArray())
        {
            if (!keep.Contains(cursor))
            {
                PInvoke.DestroyIcon((HICON)cursor);
                _drawn.Remove(key);
            }
        }
    }

    /// <summary>A 32-bit cursor with straight alpha (the mask is unused when the color bitmap has alpha).</summary>
    private static nint Create(CursorArtwork.Image image)
    {
        var header = new BITMAPV5HEADER
        {
            bV5Size = (uint)sizeof(BITMAPV5HEADER),
            bV5Width = image.Width,
            bV5Height = -image.Height,
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = BI_COMPRESSION.BI_BITFIELDS,
            bV5RedMask = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF,
            bV5AlphaMask = 0xFF000000,
        };
        void* bits;
        HBITMAP color = PInvoke.CreateDIBSection(HDC.Null, (BITMAPINFO*)&header, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        if (color.IsNull)
        {
            throw CallbackErrors.LastError("CreateDIBSection (canvas cursor)");
        }
        HBITMAP mask = HBITMAP.Null;
        try
        {
            Marshal.Copy(image.Bgra, 0, (nint)bits, image.Bgra.Length);
            mask = PInvoke.CreateBitmap(image.Width, image.Height, 1, 1, null);
            if (mask.IsNull)
            {
                throw CallbackErrors.LastError("CreateBitmap (canvas cursor mask)");
            }
            var info = new ICONINFO
            {
                fIcon = false,
                xHotspot = (uint)image.HotspotX,
                yHotspot = (uint)image.HotspotY,
                hbmMask = mask,
                hbmColor = color,
            };
            HICON cursor = PInvoke.CreateIconIndirect(&info);
            if (cursor.IsNull)
            {
                throw CallbackErrors.LastError("CreateIconIndirect (canvas cursor)");
            }
            return (nint)cursor;
        }
        finally
        {
            PInvoke.DeleteObject(color);
            if (!mask.IsNull)
            {
                PInvoke.DeleteObject(mask);
            }
        }
    }
}
