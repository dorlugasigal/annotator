// Port of apps/macos/Sources/PresentationRendering/DiagramGeometry.swift, DiamondGeometry.swift and AxisTickGeometry.swift.
namespace Annotator.Core.Geometry;

public static class DiagramGeometry
{
    public static PathData Path(in Element element)
    {
        var b = element.Frame;
        var path = new PathBuilder(24, 48);
        switch (element.Tool)
        {
            case Tool.Database:
            {
                var cap = b.Height * 0.24;
                path.MoveTo(new Point(b.X, b.Y + cap / 2));
                path.LineTo(new Point(b.X, b.MaxY - cap / 2));
                path.CubicTo(new Point(b.X, b.MaxY + cap / 3), new Point(b.MaxX, b.MaxY + cap / 3), new Point(b.MaxX, b.MaxY - cap / 2));
                path.LineTo(new Point(b.MaxX, b.Y + cap / 2));
                path.CubicTo(new Point(b.MaxX, b.Y - cap / 3), new Point(b.X, b.Y - cap / 3), new Point(b.X, b.Y + cap / 2));
                path.Close();
                path.AddEllipse(new Rect(b.X, b.Y, b.Width, cap));
                break;
            }
            case Tool.Queue:
            {
                double gap = b.Height * 0.06, rowHeight = (b.Height - gap * 2) / 3;
                for (var row = 0; row < 3; row++)
                {
                    var frame = new Rect(b.X, b.Y + row * (rowHeight + gap), b.Width, rowHeight);
                    var radius = SwiftMath.Min(6, rowHeight / 5);
                    path.AddRoundedRect(frame, radius, radius);
                    path.MoveTo(new Point(b.X + b.Width * 0.2, frame.Y));
                    path.LineTo(new Point(b.X + b.Width * 0.2, frame.MaxY));
                }
                break;
            }
            case Tool.Person:
                path.AddEllipse(new Rect(b.X + b.Width * 0.31, b.Y, b.Width * 0.38, b.Height * 0.34));
                path.MoveTo(new Point(b.X, b.MaxY));
                path.LineTo(new Point(b.X, b.Y + b.Height * 0.82));
                path.CubicTo(new Point(b.X, b.Y + b.Height * 0.32), new Point(b.MaxX, b.Y + b.Height * 0.32),
                             new Point(b.MaxX, b.Y + b.Height * 0.82));
                path.LineTo(new Point(b.MaxX, b.MaxY));
                path.Close();
                break;
            case Tool.Grid:
            {
                path.AddRect(b);
                long columns = element.Style.GridColumns ?? 4, rows = element.Style.GridRows ?? 4;
                for (long column = 1; column < columns; column++)
                {
                    var x = b.X + b.Width * column / columns;
                    path.MoveTo(new Point(x, b.Y));
                    path.LineTo(new Point(x, b.MaxY));
                }
                for (long row = 1; row < rows; row++)
                {
                    var y = b.Y + b.Height * row / rows;
                    path.MoveTo(new Point(b.X, y));
                    path.LineTo(new Point(b.MaxX, y));
                }
                break;
            }
            case Tool.Axes:
            {
                var center = element.Style.NegativeAxes == true ? b.Center : new Point(b.X + b.Width * 0.12, b.Y + b.Height * 0.88);
                path.MoveTo(new Point(b.X, center.Y));
                path.LineTo(new Point(b.MaxX, center.Y));
                path.MoveTo(new Point(center.X, b.MaxY));
                path.LineTo(new Point(center.X, b.Y));
                var tip = SwiftMath.Min(10, SwiftMath.Min(b.Width, b.Height) * 0.12);
                path.MoveTo(new Point(b.MaxX - tip, center.Y - tip / 2));
                path.LineTo(new Point(b.MaxX, center.Y));
                path.LineTo(new Point(b.MaxX - tip, center.Y + tip / 2));
                path.MoveTo(new Point(center.X - tip / 2, b.Y + tip));
                path.LineTo(new Point(center.X, b.Y));
                path.LineTo(new Point(center.X + tip / 2, b.Y + tip));
                var ticks = AxisTickGeometry.Positions(b, element.Style);
                foreach (var x in ticks.Horizontal)
                {
                    path.MoveTo(new Point(x, center.Y - 3));
                    path.LineTo(new Point(x, center.Y + 3));
                }
                foreach (var y in ticks.Vertical)
                {
                    path.MoveTo(new Point(center.X - 3, y));
                    path.LineTo(new Point(center.X + 3, y));
                }
                break;
            }
        }
        return path.ToPathData();
    }
}

public static class DiamondGeometry
{
    public static PathData Path(Rect frame, double cornerRadius)
    {
        Point[] vertices =
        [
            new(frame.Center.X, frame.Y), new(frame.MaxX, frame.Center.Y),
            new(frame.Center.X, frame.MaxY), new(frame.X, frame.Center.Y),
        ];
        var path = new PathBuilder(10, 12);
        if (!(cornerRadius > 0 && frame.Width > 0 && frame.Height > 0))
        {
            path.AddLines(vertices);
            path.Close();
            return path.ToPathData();
        }
        var corners = RoundedDiamond.Corners(frame, cornerRadius);
        for (var index = 0; index < corners.Count; index++)
        {
            ref readonly var corner = ref corners[index];
            if (index == 0)
            {
                path.MoveTo(corner.Entry);
            }
            else
            {
                path.LineTo(corner.Entry);
            }
            path.QuadTo(corner.Control, corner.Exit);
        }
        path.Close();
        return path.ToPathData();
    }
}

/// <summary>Tick positions along the axes, sorted ascending.</summary>
public readonly record struct AxisTicks(ValueList<double> Horizontal, ValueList<double> Vertical);

public static class AxisTickGeometry
{
    public static AxisTicks Positions(Rect frame, in Style style)
    {
        if (style.AxisTickCount is not { } count)
        {
            var horizontal = new double[9];
            var vertical = new double[9];
            for (var step = 1; step < 10; step++)
            {
                horizontal[step - 1] = frame.X + frame.Width * step / 10;
                vertical[step - 1] = frame.Y + frame.Height * step / 10;
            }
            return new AxisTicks(ValueList.Create<double>(horizontal), ValueList.Create<double>(vertical));
        }
        if (count is < 0 or > 24)
        {
            throw new ArgumentOutOfRangeException(nameof(style), count, "Axis tick count must be validated before rendering.");
        }
        if (count == 0)
        {
            return new AxisTicks(default, default);
        }
        var centered = style.NegativeAxes == true;
        var origin = centered ? frame.Center : new Point(frame.X + frame.Width * 0.12, frame.Y + frame.Height * 0.88);
        var ticks = (int)count;
        var size = centered ? ticks * 2 : ticks;
        var x = new double[size];
        var y = new double[size];
        for (var index = 0; index < ticks; index++)
        {
            var fraction = (double)(index + 1) / (count + 1);
            x[index] = origin.X + (frame.MaxX - origin.X) * fraction;
            y[index] = origin.Y - (origin.Y - frame.Y) * fraction;
            if (centered)
            {
                x[ticks + index] = origin.X - (origin.X - frame.X) * fraction;
                y[ticks + index] = origin.Y + (frame.MaxY - origin.Y) * fraction;
            }
        }
        Array.Sort(x);
        Array.Sort(y);
        return new AxisTicks(ValueList.Create<double>(x), ValueList.Create<double>(y));
    }
}
