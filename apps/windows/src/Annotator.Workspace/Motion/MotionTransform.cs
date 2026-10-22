namespace Annotator.Workspace.Motion;

/// <summary>
/// A layer transform of the visibility motion: uniform scale, rotation about z and translation, in Core Animation's
/// conventions and the Mac's y-up space (<c>CATransform3D</c> with m11 = m22 = <see cref="A"/>, m12 = <see cref="B"/>,
/// m21 = -<see cref="B"/>, m41 = <see cref="Tx"/>, m42 = <see cref="Ty"/>). It applies around the layer's center, then
/// moves it by the translation. Every Mac motion transform has this form, so the formulas port without change; use
/// <see cref="ToScreen"/> for Windows' y-down screens.
/// </summary>
public readonly record struct MotionTransform(double A, double B, double Tx, double Ty)
{
    public static MotionTransform Identity { get; } = new(1, 0, 0, 0);

    /// <summary><c>CATransform3DMakeScale(s, s, 1)</c>.</summary>
    public static MotionTransform MakeScale(double scale) => new(scale, 0, 0, 0);

    /// <summary><c>CATransform3DMakeRotation(angle, 0, 0, 1)</c>: counterclockwise in the Mac's y-up space.</summary>
    public static MotionTransform MakeRotation(double angle) => new(Math.Cos(angle), Math.Sin(angle), 0, 0);

    /// <summary><c>CATransform3DMakeTranslation(x, y, 0)</c>.</summary>
    public static MotionTransform MakeTranslation(double x, double y) => new(1, 0, x, y);

    /// <summary>The scale factor, <c>hypot(m11, m12)</c>.</summary>
    public double Scale => Math.Sqrt(A * A + B * B);

    /// <summary>The rotation in radians, <c>atan2(m12, m11)</c>.</summary>
    public double Angle => Math.Atan2(B, A);

    public bool IsIdentity => A == 1 && B == 0 && Tx == 0 && Ty == 0;

    /// <summary><c>CATransform3DScale(self, s, s, 1)</c>: scales first, keeping the translation.</summary>
    public MotionTransform Scaled(double scale) => this with { A = A * scale, B = B * scale };

    /// <summary>The same transform with translation (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public MotionTransform WithTranslation(double x, double y) => this with { Tx = x, Ty = y };

    /// <summary>
    /// The transform in a y-down space such as a Windows screen: the same picture seen with y flipped, so the rotation
    /// and the vertical movement change sign.
    /// </summary>
    public MotionTransform ToScreen() => new(A, -B, Tx, -Ty);

    /// <summary>Swift <c>VisibilityPose.mix</c>: element-wise interpolation of the two matrices.</summary>
    public static MotionTransform Mix(MotionTransform a, MotionTransform b, double t) =>
        new(a.A + (b.A - a.A) * t, a.B + (b.B - a.B) * t, a.Tx + (b.Tx - a.Tx) * t, a.Ty + (b.Ty - a.Ty) * t);

    /// <summary>
    /// Core Animation's interpolation between two transforms: scale, rotation (the shorter way round) and translation
    /// separately, each linear in <paramref name="t"/>.
    /// </summary>
    public static MotionTransform Interpolate(MotionTransform a, MotionTransform b, double t)
    {
        if (t <= 0)
        {
            return a;
        }
        if (t >= 1)
        {
            return b;
        }
        var scale = a.Scale + (b.Scale - a.Scale) * t;
        var from = a.Angle;
        var turn = b.Angle - from;
        if (turn > Math.PI)
        {
            turn -= 2 * Math.PI;
        }
        else if (turn < -Math.PI)
        {
            turn += 2 * Math.PI;
        }
        var angle = from + turn * t;
        return new MotionTransform(scale * Math.Cos(angle), scale * Math.Sin(angle), a.Tx + (b.Tx - a.Tx) * t, a.Ty + (b.Ty - a.Ty) * t);
    }

    /// <summary>Whether every component is within <paramref name="tolerance"/> of <paramref name="other"/>.</summary>
    public bool IsClose(MotionTransform other, double tolerance = 1e-9) =>
        Math.Abs(A - other.A) <= tolerance && Math.Abs(B - other.B) <= tolerance
        && Math.Abs(Tx - other.Tx) <= tolerance && Math.Abs(Ty - other.Ty) <= tolerance;
}

/// <summary>A point in the Mac's y-up space.</summary>
public readonly record struct MotionPoint(double X, double Y)
{
    public double Distance(MotionPoint other) => double.Hypot(X - other.X, Y - other.Y);
}

/// <summary>A rectangle in the Mac's y-up space: <see cref="Y"/> is the bottom edge.</summary>
public readonly record struct MotionRect(double X, double Y, double Width, double Height)
{
    public double MinX => X;

    public double MinY => Y;

    public double MaxX => X + Width;

    public double MaxY => Y + Height;

    public double MidX => X + Width / 2;

    public double MidY => Y + Height / 2;

    public MotionPoint Center => new(MidX, MidY);

    public bool IsEmpty => !(Width > 0) || !(Height > 0);

    /// <summary><c>CGRect.intersects</c>: the open interiors overlap.</summary>
    public bool Intersects(MotionRect other) =>
        !IsEmpty && !other.IsEmpty && X < other.MaxX && other.X < MaxX && Y < other.MaxY && other.Y < MaxY;

    public MotionRect Union(MotionRect other)
    {
        var minX = Math.Min(X, other.X);
        var minY = Math.Min(Y, other.Y);
        return new MotionRect(minX, minY, Math.Max(MaxX, other.MaxX) - minX, Math.Max(MaxY, other.MaxY) - minY);
    }

    /// <summary><c>CGRect.insetBy(dx:dy:)</c>; negative values grow the rectangle.</summary>
    public MotionRect Inset(double dx, double dy) => new(X + dx, Y + dy, Width - 2 * dx, Height - 2 * dy);

    public MotionRect Offset(double dx, double dy) => new(X + dx, Y + dy, Width, Height);
}
