using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Rendering.Interop;
using Annotator.Rendering.Text;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.DirectWrite;

namespace Annotator.Rendering.Drawing;

/// <summary>
/// The cached device resources of one display list, with its bounds. Created when the list enters a retained set
/// (a layer's scene, an export) and released when the last set lets it go. Resources shared by several lists, such
/// as a path drawn by two lists, are reference counted.
/// </summary>
internal sealed class ListEntry
{
    public ListEntry(DisplayList list, ItemResources[] resources, Rect? bounds, bool usesSystemColors)
    {
        List = list;
        Resources = resources;
        Bounds = bounds;
        UsesSystemColors = usesSystemColors;
    }

    public DisplayList List { get; }

    /// <summary>One slot per item, parallel to <see cref="DisplayList.Items"/>.</summary>
    public ItemResources[] Resources { get; }

    /// <summary>Everything the list can paint, in scene points, before antialiasing; null when it paints nothing.</summary>
    public Rect? Bounds { get; }

    /// <summary>Whether an item paints with a system color (accent, window background, red), which a theme change changes.</summary>
    public bool UsesSystemColors { get; }

    public int RefCount { get; set; }
}

/// <summary>The cache entries one item uses.</summary>
internal struct ItemResources
{
    public GeometryEntry? Geometry;
    public StrokeEntry? Stroke;
    public GradientEntry? Gradient;
    public TextEntry? Text;

    /// <summary>A rectangle mask (ID2D1RectangleGeometry*) for a clip under a rotation, created on first use.</summary>
    public nint ClipGeometry;
}

/// <summary>Direct2D geometries for one <see cref="PathData"/> and, once it pays off, realizations at the current scale.</summary>
internal sealed unsafe class GeometryEntry(PathData path)
{
    public PathData Path { get; } = path;

    public int RefCount;

    public ID2D1PathGeometry1* Winding;
    public ID2D1PathGeometry1* Alternate;
    public ID2D1GeometryRealization* Filled;
    public FillRule FilledRule;
    public float FilledScale;
    public ID2D1GeometryRealization* Stroked;
    public ID2D1StrokeStyle1* StrokedStyle;
    public float StrokedWidth;
    public float StrokedScale;

    /// <summary>The stroke's area as one filled geometry, for paths of several figures that may overlap.</summary>
    public ID2D1PathGeometry1* Widened;
    public ID2D1StrokeStyle1* WidenedStyle;
    public float WidenedWidth;
    public float WidenedScale;

    /// <summary>The number of figures (subpaths that start with a move), counted once.</summary>
    public int Figures = -1;

    /// <summary>Whether a draw's parallel merge (<c>DisplayListRenderer.PrepareMergedStrokes</c>) has this path listed.</summary>
    public bool MergeQueued;

    public void Release()
    {
        Native.Release(ref Winding);
        Native.Release(ref Alternate);
        Native.Release(ref Widened);
        WidenedStyle = null;
        ReleaseRealizations();
    }

    public void ReleaseRealizations()
    {
        Native.Release(ref Filled);
        Native.Release(ref Stroked);
        StrokedStyle = null;
    }
}

/// <summary>A Direct2D stroke style for one <see cref="StrokeStyle"/> value (dashes divided by the width).</summary>
internal sealed unsafe class StrokeEntry
{
    public ID2D1StrokeStyle1* Style;
    public int RefCount;

    public void Release() => Native.Release(ref Style);
}

/// <summary>A gradient brush for one <see cref="LinearGradientPaint"/>.</summary>
internal sealed unsafe class GradientEntry
{
    public ID2D1LinearGradientBrush* Brush;
    public int RefCount;

    public void Release() => Native.Release(ref Brush);
}

/// <summary>
/// A DirectWrite layout for one <see cref="TextRun"/>, the layout's origin (the frame's top-left corner moved so its
/// first baseline lands on <see cref="TextRun.Baseline"/>) and the area its glyphs can paint.
/// </summary>
internal sealed unsafe class TextEntry
{
    public IDWriteTextLayout* Layout;
    public Point Origin;
    public Rect InkBounds;
    public int RefCount;

    public void Release() => Native.Release(ref Layout);
}
