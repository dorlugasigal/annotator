namespace Annotator.Core.Geometry;

/// <summary>
/// A 3x2 affine transform with Core Graphics semantics: a point maps to
/// <c>(A*x + C*y + Tx, B*x + D*y + Ty)</c>. Direct2D's <c>D2D1_MATRIX_3X2_F</c> uses the same layout
/// (M11 = A, M12 = B, M21 = C, M22 = D, Dx = Tx, Dy = Ty), so a pushed transform T becomes
/// <c>T * current</c> in Direct2D.
/// </summary>
public readonly record struct AffineTransform(double A, double B, double C, double D, double Tx, double Ty)
{
    public static AffineTransform Identity => new(1, 0, 0, 1, 0, 0);

    public bool IsIdentity => A == 1 && B == 0 && C == 0 && D == 1 && Tx == 0 && Ty == 0;

    public static AffineTransform Translation(double tx, double ty) => new(1, 0, 0, 1, tx, ty);

    public static AffineTransform Scale(double sx, double sy) => new(sx, 0, 0, sy, 0, 0);

    /// <summary><c>CGAffineTransformMakeRotation</c>.</summary>
    public static AffineTransform Rotation(double angle)
    {
        var cosine = Math.Cos(angle);
        var sine = Math.Sin(angle);
        return new AffineTransform(cosine, sine, -sine, cosine, 0, 0);
    }

    /// <summary><c>CGAffineTransformConcat(first, second)</c>: applies <paramref name="first"/>, then <paramref name="second"/>.</summary>
    public static AffineTransform Concat(in AffineTransform first, in AffineTransform second) => new(
        first.A * second.A + first.B * second.C,
        first.A * second.B + first.B * second.D,
        first.C * second.A + first.D * second.C,
        first.C * second.B + first.D * second.D,
        first.Tx * second.A + first.Ty * second.C + second.Tx,
        first.Tx * second.B + first.Ty * second.D + second.Ty);

    /// <summary>
    /// <c>translatedBy(x:y:)</c> (<c>CGAffineTransformTranslate</c>): the translation happens before this transform.
    /// Core Graphics computes <c>tx + a*x + c*y</c> with fused multiply-adds (corpus <c>platform/cgpath.jsonl</c>).
    /// </summary>
    public AffineTransform Translated(double tx, double ty) => new(
        A, B, C, D, Math.FusedMultiplyAdd(C, ty, Math.FusedMultiplyAdd(A, tx, Tx)), Math.FusedMultiplyAdd(D, ty, Math.FusedMultiplyAdd(B, tx, Ty)));

    /// <summary><c>scaledBy(x:y:)</c>: the scale happens before this transform.</summary>
    public AffineTransform Scaled(double sx, double sy) => Concat(Scale(sx, sy), this);

    /// <summary><c>rotated(by:)</c>: the rotation happens before this transform.</summary>
    public AffineTransform Rotated(double angle) => Concat(Rotation(angle), this);

    /// <summary>
    /// The Mac renderer's element transform, <c>translateBy(center)</c>, <c>rotate(by:)</c> and
    /// <c>translateBy(-center)</c>: a rotation about <paramref name="center"/>.
    /// </summary>
    public static AffineTransform RotationAbout(Point center, double angle) =>
        Translation(center.X, center.Y).Rotated(angle).Translated(-center.X, -center.Y);

    /// <summary>
    /// <c>CGPointApplyAffineTransform</c>. Core Graphics on the Mac computes <c>a*x + c*y + tx</c> as
    /// <c>fma(a, x, c*y) + tx</c>, so this does too and transformed paths match the corpus bit for bit.
    /// </summary>
    public Point Transform(Point point) => new(
        Math.FusedMultiplyAdd(A, point.X, C * point.Y) + Tx, Math.FusedMultiplyAdd(B, point.X, D * point.Y) + Ty);

    /// <summary><c>CGRectApplyAffineTransform</c>: the bounding box of the four transformed corners.</summary>
    public Rect TransformBounds(Rect rect)
    {
        var p0 = Transform(new Point(rect.X, rect.Y));
        var p1 = Transform(new Point(rect.MaxX, rect.Y));
        var p2 = Transform(new Point(rect.X, rect.MaxY));
        var p3 = Transform(new Point(rect.MaxX, rect.MaxY));
        var minX = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
        var maxX = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
        var minY = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
        var maxY = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary><c>CGAffineTransformInvert</c>; null when the transform cannot be inverted.</summary>
    public AffineTransform? Inverted()
    {
        var determinant = A * D - B * C;
        if (determinant == 0 || !double.IsFinite(determinant))
        {
            return null;
        }
        return new AffineTransform(
            D / determinant, -B / determinant, -C / determinant, A / determinant,
            (C * Ty - D * Tx) / determinant, (B * Tx - A * Ty) / determinant);
    }

    public bool Equals(AffineTransform other) =>
        A == other.A && B == other.B && C == other.C && D == other.D && Tx == other.Tx && Ty == other.Ty;

    public override int GetHashCode() => HashCode.Combine(A, B, C, D, Tx, Ty);
}
