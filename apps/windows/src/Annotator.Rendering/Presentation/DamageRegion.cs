using Annotator.Platform.Displays;

namespace Annotator.Rendering.Presentation;

/// <summary>
/// A small set of pixel rectangles to redraw, clipped to the surface. Rectangles that overlap or nearly touch are
/// merged; past <see cref="Capacity"/> the two whose union adds the least area are merged, so a frame never issues
/// more than a few draw calls. Reused from frame to frame without allocating.
/// </summary>
internal sealed class DamageRegion
{
    public const int Capacity = 8;

    /// <summary>Rectangles closer than this many pixels are merged.</summary>
    private const int MergeDistance = 8;

    private readonly PixelRect[] rects = new PixelRect[Capacity + 1];

    public int Count { get; private set; }

    public bool IsEmpty => Count == 0;

    public ReadOnlySpan<PixelRect> Rects => rects.AsSpan(0, Count);

    /// <summary>The whole surface damaged at once, for full redraws.</summary>
    public bool IsFull { get; private set; }

    public void Clear()
    {
        Count = 0;
        IsFull = false;
    }

    public void AddFull(int width, int height)
    {
        rects[0] = new PixelRect(0, 0, width, height);
        Count = 1;
        IsFull = true;
    }

    public void Add(PixelRect rect, int width, int height)
    {
        var clipped = new PixelRect(Math.Max(0, rect.Left), Math.Max(0, rect.Top), Math.Min(width, rect.Right), Math.Min(height, rect.Bottom));
        if (clipped.IsEmpty || IsFull)
        {
            return;
        }
        for (var index = 0; index < Count; index++)
        {
            if (Near(rects[index], clipped))
            {
                clipped = Union(rects[index], clipped);
                RemoveAt(index);
                index = -1;
            }
        }
        rects[Count++] = clipped;
        if (Count > Capacity)
        {
            MergeCheapestPair();
        }
    }

    public void Add(DamageRegion other, int width, int height)
    {
        if (other.IsFull)
        {
            AddFull(width, height);
            return;
        }
        foreach (var rect in other.Rects)
        {
            Add(rect, width, height);
        }
    }

    public void CopyFrom(DamageRegion other)
    {
        other.rects.AsSpan(0, other.Count).CopyTo(rects);
        Count = other.Count;
        IsFull = other.IsFull;
    }

    /// <summary>The total area of the rectangles, in pixels.</summary>
    public long Area
    {
        get
        {
            long area = 0;
            foreach (var rect in Rects)
            {
                area += (long)rect.Width * rect.Height;
            }
            return area;
        }
    }

    internal static PixelRect Union(in PixelRect a, in PixelRect b) =>
        new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    private static bool Near(in PixelRect a, in PixelRect b) =>
        a.Left - MergeDistance <= b.Right && b.Left - MergeDistance <= a.Right && a.Top - MergeDistance <= b.Bottom && b.Top - MergeDistance <= a.Bottom;

    private void RemoveAt(int index)
    {
        rects[index] = rects[Count - 1];
        Count--;
    }

    private void MergeCheapestPair()
    {
        int bestA = 0, bestB = 1;
        var bestCost = long.MaxValue;
        for (var a = 0; a < Count; a++)
        {
            for (var b = a + 1; b < Count; b++)
            {
                var union = Union(rects[a], rects[b]);
                var cost = (long)union.Width * union.Height - (long)rects[a].Width * rects[a].Height - (long)rects[b].Width * rects[b].Height;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestA = a;
                    bestB = b;
                }
            }
        }
        var merged = Union(rects[bestA], rects[bestB]);
        RemoveAt(bestB);
        rects[bestA] = merged;
    }
}
