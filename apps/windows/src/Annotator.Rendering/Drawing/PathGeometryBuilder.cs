using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Rendering.Interop;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;

namespace Annotator.Rendering.Drawing;

/// <summary>
/// Builds Direct2D path geometries from <see cref="PathData"/> with Core Graphics element semantics: a move ends any
/// open figure, a close ends the figure closed, a segment after a close starts a new figure at the closed subpath's
/// start, and a subpath that is only a move draws nothing. Not thread-safe (reuses a point buffer).
/// </summary>
internal sealed unsafe class PathGeometryBuilder
{
    private const int BatchSize = 512;
    private readonly D2D_POINT_2F[] batch = new D2D_POINT_2F[BatchSize];

    /// <summary>The whole path. The caller owns the returned geometry.</summary>
    public ID2D1PathGeometry1* Build(ID2D1Factory1* factory, PathData path, FillRule rule) =>
        Build(factory, path, rule, cull: false, default, 0);

    /// <summary>
    /// Only the segments whose control-point bounds, grown by <paramref name="padding"/>, reach
    /// <paramref name="clip"/>, as open figures. For strokes only: a skipped segment cannot paint inside the clip, and
    /// every cut lies more than <paramref name="padding"/> from it, so cut joins and caps never change clipped pixels.
    /// </summary>
    public ID2D1PathGeometry1* BuildCulled(ID2D1Factory1* factory, PathData path, Rect clip, double padding) =>
        Build(factory, path, FillRule.NonZero, cull: true, RectMath.Outset(clip, padding, padding), padding);

    private ID2D1PathGeometry1* Build(ID2D1Factory1* factory, PathData path, FillRule rule, bool cull, Rect reach, double padding)
    {
        ID2D1PathGeometry1* geometry;
        Native.Check(factory->CreatePathGeometry(&geometry), "ID2D1Factory1::CreatePathGeometry");
        ID2D1GeometrySink* sink = null;
        try
        {
            Native.Check(geometry->Open(&sink), "ID2D1PathGeometry::Open");
            sink->SetFillMode(D2D.FillMode(rule));
            var writer = new FigureWriter(sink, batch);
            var points = path.Points;
            var index = 0;
            Point current = default, start = default;
            // Whether the open figure began at the subpath start and kept every segment, so a close may end it closed.
            var whole = false;
            foreach (var verb in path.Verbs)
            {
                switch (verb)
                {
                    case PathVerb.MoveTo:
                        writer.End(closed: false);
                        writer.ResetSubpath();
                        current = start = points[index++];
                        whole = true;
                        break;
                    case PathVerb.LineTo:
                    {
                        var end = points[index++];
                        if (!cull || Reaches(current, end, reach))
                        {
                            writer.Line(current, end);
                        }
                        else
                        {
                            writer.End(closed: false);
                            whole = false;
                        }
                        current = end;
                        break;
                    }
                    case PathVerb.QuadTo:
                    {
                        Point control = points[index], end = points[index + 1];
                        index += 2;
                        if (!cull || Reaches(current, control, end, reach))
                        {
                            writer.Quad(current, control, end);
                        }
                        else
                        {
                            writer.End(closed: false);
                            whole = false;
                        }
                        current = end;
                        break;
                    }
                    case PathVerb.CubicTo:
                    {
                        Point first = points[index], second = points[index + 1], end = points[index + 2];
                        index += 3;
                        if (!cull || Reaches(current, first, second, end, reach))
                        {
                            writer.Cubic(current, first, second, end);
                        }
                        else
                        {
                            writer.End(closed: false);
                            whole = false;
                        }
                        current = end;
                        break;
                    }
                    case PathVerb.Close:
                        if (writer.IsOpen && whole)
                        {
                            writer.End(closed: true);
                        }
                        else if (writer.IsOpen || (cull && writer.HasSegmentsSinceMove))
                        {
                            // A partly kept figure: the closing segment becomes a line if it reaches the clip.
                            if (current != start && Reaches(current, start, reach))
                            {
                                writer.Line(current, start);
                            }
                            writer.End(closed: false);
                        }
                        writer.ResetSubpath();
                        current = start;
                        whole = true;
                        break;
                }
            }
            writer.End(closed: false);
            Native.Check(sink->Close(), "ID2D1GeometrySink::Close");
        }
        catch
        {
            Native.Release(ref sink);
            Native.Release(ref geometry);
            throw;
        }
        Native.Release(ref sink);
        return geometry;
    }

    private static bool Reaches(Point a, Point b, Rect reach) =>
        Math.Max(a.X, b.X) >= reach.X && Math.Min(a.X, b.X) <= reach.MaxX && Math.Max(a.Y, b.Y) >= reach.Y && Math.Min(a.Y, b.Y) <= reach.MaxY;

    private static bool Reaches(Point a, Point b, Point c, Rect reach) =>
        Math.Max(a.X, Math.Max(b.X, c.X)) >= reach.X && Math.Min(a.X, Math.Min(b.X, c.X)) <= reach.MaxX
        && Math.Max(a.Y, Math.Max(b.Y, c.Y)) >= reach.Y && Math.Min(a.Y, Math.Min(b.Y, c.Y)) <= reach.MaxY;

    private static bool Reaches(Point a, Point b, Point c, Point d, Rect reach) =>
        Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) >= reach.X && Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) <= reach.MaxX
        && Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)) >= reach.Y && Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) <= reach.MaxY;

    /// <summary>Begins figures lazily at their first segment and batches consecutive lines into AddLines calls.</summary>
    private ref struct FigureWriter
    {
        private readonly ID2D1GeometrySink* sink;
        private readonly Span<D2D_POINT_2F> batch;
        private int pending;

        public FigureWriter(ID2D1GeometrySink* sink, Span<D2D_POINT_2F> batch)
        {
            this.sink = sink;
            this.batch = batch;
        }

        public bool IsOpen { get; private set; }

        public bool HasSegmentsSinceMove { get; private set; }

        public void Line(Point from, Point to)
        {
            Begin(from);
            if (pending == batch.Length)
            {
                Flush();
            }
            batch[pending++] = D2D.Point(to);
        }

        public void Quad(Point from, Point control, Point to)
        {
            Begin(from);
            Flush();
            var segment = new D2D1_QUADRATIC_BEZIER_SEGMENT { point1 = D2D.Point(control), point2 = D2D.Point(to) };
            sink->AddQuadraticBezier(&segment);
        }

        public void Cubic(Point from, Point first, Point second, Point to)
        {
            Begin(from);
            Flush();
            var segment = new D2D1_BEZIER_SEGMENT { point1 = D2D.Point(first), point2 = D2D.Point(second), point3 = D2D.Point(to) };
            sink->AddBezier(&segment);
        }

        public void End(bool closed)
        {
            if (!IsOpen)
            {
                return;
            }
            Flush();
            sink->EndFigure(closed ? D2D1_FIGURE_END.D2D1_FIGURE_END_CLOSED : D2D1_FIGURE_END.D2D1_FIGURE_END_OPEN);
            IsOpen = false;
        }

        public void ResetSubpath() => HasSegmentsSinceMove = false;

        private void Begin(Point from)
        {
            HasSegmentsSinceMove = true;
            if (!IsOpen)
            {
                sink->BeginFigure(D2D.Point(from), D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_FILLED);
                IsOpen = true;
            }
        }

        private void Flush()
        {
            if (pending == 0)
            {
                return;
            }
            fixed (D2D_POINT_2F* points = batch)
            {
                sink->AddLines(points, (uint)pending);
            }
            pending = 0;
        }
    }
}
