using System.Collections.Immutable;
using Annotator.Platform.Displays;
using Annotator.Rendering.Interop;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;

namespace Annotator.Rendering.Motion;

/// <summary>
/// The part of a snapshot that a copy keeps (Mac SharedWorkspaceTransition.clippedImage): what lies inside
/// <see cref="Keep"/> and outside every rectangle of <see cref="Erase"/>, in the overlay's pixels. The rest of the copy is
/// transparent, so the copy can move without showing anything that was outside the shared region, or under a cover or a
/// pixel effect, where it rested.
/// </summary>
public sealed record StageClip(PixelRect Keep, ImmutableArray<PixelRect> Erase)
{
    /// <summary>
    /// Whether a snapshot resting at <paramref name="pixels"/> keeps nothing: it misses <see cref="Keep"/>, or
    /// <see cref="Erase"/> covers all of it there (Mac unprotectedPath(in: frame ∩ region, excluding: protection).isEmpty).
    /// </summary>
    public bool Hides(PixelRect pixels)
    {
        PixelRect kept = Intersect(pixels, Keep);
        if (kept.IsEmpty)
        {
            return true;
        }
        var rest = new List<PixelRect> { kept };
        foreach (PixelRect erased in Erase)
        {
            for (var index = rest.Count - 1; index >= 0; index--)
            {
                PixelRect piece = rest[index];
                if (Intersect(piece, erased).IsEmpty)
                {
                    continue;
                }
                rest.RemoveAt(index);
                Subtract(piece, erased, rest);
            }
            if (rest.Count == 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Makes transparent what the clip removes from premultiplied BGRA rows of an image resting at
    /// <paramref name="pixels"/> (its top-left pixel is the overlay's pixel <c>(pixels.Left, pixels.Top)</c>).
    /// </summary>
    public void Apply(Span<byte> bgra, int stride, PixelRect pixels)
    {
        if (stride < pixels.Width * 4 || bgra.Length < (long)stride * (pixels.Height - 1) + pixels.Width * 4)
        {
            throw new ArgumentException("The pixels do not fill the image.", nameof(bgra));
        }
        PixelRect kept = Intersect(pixels, Keep);
        for (var row = 0; row < pixels.Height; row++)
        {
            Span<byte> line = bgra.Slice(row * stride, pixels.Width * 4);
            var y = pixels.Top + row;
            if (y < kept.Top || y >= kept.Bottom || kept.IsEmpty)
            {
                line.Clear();
                continue;
            }
            line[..((kept.Left - pixels.Left) * 4)].Clear();
            line[((kept.Right - pixels.Left) * 4)..].Clear();
            foreach (PixelRect erased in Erase)
            {
                if (y < erased.Top || y >= erased.Bottom)
                {
                    continue;
                }
                int left = Math.Max(erased.Left, pixels.Left), right = Math.Min(erased.Right, pixels.Right);
                if (right > left)
                {
                    line[((left - pixels.Left) * 4)..((right - pixels.Left) * 4)].Clear();
                }
            }
        }
    }

    /// <summary>
    /// <paramref name="clip"/> (target pixels) narrowed to <see cref="Keep"/>, for a target whose pixel (0, 0) is the
    /// overlay's pixel (<paramref name="originX"/>, <paramref name="originY"/>).
    /// </summary>
    public PixelRect KeepIn(PixelRect clip, int originX, int originY)
    {
        PixelRect keep = Intersect(clip, new PixelRect(Keep.Left - originX, Keep.Top - originY, Keep.Right - originX, Keep.Bottom - originY));
        return keep.IsEmpty ? new PixelRect(clip.Left, clip.Top, clip.Left, clip.Top) : keep;
    }

    /// <summary>
    /// Clears <see cref="Erase"/> to transparent inside <paramref name="bounds"/> (target pixels) of a Direct2D target
    /// whose pixel (0, 0) is the overlay's pixel (<paramref name="originX"/>, <paramref name="originY"/>), between
    /// BeginDraw and EndDraw. Leaves the transform at identity.
    /// </summary>
    internal unsafe void EraseIn(ID2D1DeviceContext* context, PixelRect bounds, int originX, int originY)
    {
        var identity = D2D.Identity;
        context->SetTransform(&identity);
        var transparent = default(D2D1_COLOR_F);
        foreach (PixelRect erased in Erase)
        {
            PixelRect local = Intersect(bounds, new PixelRect(erased.Left - originX, erased.Top - originY, erased.Right - originX, erased.Bottom - originY));
            if (local.IsEmpty)
            {
                continue;
            }
            var rect = D2D.Rect(local.Left, local.Top, local.Right, local.Bottom);
            context->PushAxisAlignedClip(&rect, D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
            context->Clear(&transparent);
            context->PopAxisAlignedClip();
        }
    }

    /// <summary>
    /// Clips what is drawn next to <see cref="Keep"/> inside <paramref name="bounds"/> (target pixels) of a Direct2D target
    /// whose pixel (0, 0) is the overlay's pixel (<paramref name="originX"/>, <paramref name="originY"/>), with the
    /// transform at identity. Pair it with <see cref="EndKeep"/>.
    /// </summary>
    internal unsafe void BeginKeep(ID2D1DeviceContext* context, PixelRect bounds, int originX, int originY)
    {
        PixelRect keep = KeepIn(bounds, originX, originY);
        var identity = D2D.Identity;
        context->SetTransform(&identity);
        var rect = D2D.Rect(keep.Left, keep.Top, keep.Right, keep.Bottom);
        context->PushAxisAlignedClip(&rect, D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
    }

    /// <summary>Ends <see cref="BeginKeep"/> and clears <see cref="Erase"/> (<see cref="EraseIn"/>).</summary>
    internal unsafe void EndKeep(ID2D1DeviceContext* context, PixelRect bounds, int originX, int originY)
    {
        context->PopAxisAlignedClip();
        EraseIn(context, bounds, originX, originY);
    }

    internal static PixelRect Intersect(PixelRect a, PixelRect b)
    {
        int left = Math.Max(a.Left, b.Left), top = Math.Max(a.Top, b.Top);
        return new PixelRect(left, top, Math.Max(left, Math.Min(a.Right, b.Right)), Math.Max(top, Math.Min(a.Bottom, b.Bottom)));
    }

    /// <summary>Adds the parts of <paramref name="piece"/> outside <paramref name="cut"/> to <paramref name="rest"/>: at most four.</summary>
    private static void Subtract(PixelRect piece, PixelRect cut, List<PixelRect> rest)
    {
        PixelRect inner = Intersect(piece, cut);
        if (inner.Top > piece.Top)
        {
            rest.Add(new PixelRect(piece.Left, piece.Top, piece.Right, inner.Top));
        }
        if (inner.Bottom < piece.Bottom)
        {
            rest.Add(new PixelRect(piece.Left, inner.Bottom, piece.Right, piece.Bottom));
        }
        if (inner.Left > piece.Left)
        {
            rest.Add(new PixelRect(piece.Left, inner.Top, inner.Left, inner.Bottom));
        }
        if (inner.Right < piece.Right)
        {
            rest.Add(new PixelRect(inner.Right, inner.Top, piece.Right, inner.Bottom));
        }
    }
}
