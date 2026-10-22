namespace Annotator.Rendering.Tests.Conformance;

/// <summary>A CIELAB color (D65 white, CIE 1976 L*a*b*).</summary>
internal readonly record struct Lab(double L, double A, double B);

/// <summary>
/// sRGB to CIELAB and the CIEDE2000 color difference (Sharma, Wu and Dalal, 2005), used by the WIN-REND-05
/// comparison. sRGB uses IEC 61966-2-1's transfer curve and matrix with the D65 white point.
/// </summary>
internal static class ColorDifference
{
    private static readonly double[] LinearTable = BuildLinearTable();

    /// <summary>The linear-light value of an 8-bit sRGB-encoded component.</summary>
    public static double Linear(int encoded) => LinearTable[encoded];

    /// <summary>The CIELAB color of an opaque 8-bit sRGB color.</summary>
    public static Lab FromSrgb(int red, int green, int blue)
    {
        double r = LinearTable[red], g = LinearTable[green], b = LinearTable[blue];
        var x = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047;
        var y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        var z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883;
        double fx = F(x), fy = F(y), fz = F(z);
        return new Lab(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>CIEDE2000 with the parametric factors kL = kC = kH = 1.</summary>
    public static double DeltaE2000(in Lab first, in Lab second)
    {
        const double Pow25To7 = 6103515625.0;
        double l1 = first.L, a1 = first.A, b1 = first.B, l2 = second.L, a2 = second.A, b2 = second.B;
        var c1 = Math.Sqrt(a1 * a1 + b1 * b1);
        var c2 = Math.Sqrt(a2 * a2 + b2 * b2);
        var meanC = (c1 + c2) / 2;
        var meanC7 = Math.Pow(meanC, 7);
        var g = 0.5 * (1 - Math.Sqrt(meanC7 / (meanC7 + Pow25To7)));
        double a1Prime = (1 + g) * a1, a2Prime = (1 + g) * a2;
        double c1Prime = Math.Sqrt(a1Prime * a1Prime + b1 * b1), c2Prime = Math.Sqrt(a2Prime * a2Prime + b2 * b2);
        var h1Prime = Hue(b1, a1Prime);
        var h2Prime = Hue(b2, a2Prime);

        var deltaL = l2 - l1;
        var deltaC = c2Prime - c1Prime;
        double deltaHue;
        if (c1Prime * c2Prime == 0)
        {
            deltaHue = 0;
        }
        else
        {
            deltaHue = h2Prime - h1Prime;
            if (deltaHue > 180)
            {
                deltaHue -= 360;
            }
            else if (deltaHue < -180)
            {
                deltaHue += 360;
            }
        }
        var deltaH = 2 * Math.Sqrt(c1Prime * c2Prime) * Math.Sin(Radians(deltaHue / 2));

        var meanL = (l1 + l2) / 2;
        var meanCPrime = (c1Prime + c2Prime) / 2;
        double meanH;
        if (c1Prime * c2Prime == 0)
        {
            meanH = h1Prime + h2Prime;
        }
        else if (Math.Abs(h1Prime - h2Prime) <= 180)
        {
            meanH = (h1Prime + h2Prime) / 2;
        }
        else
        {
            meanH = h1Prime + h2Prime < 360 ? (h1Prime + h2Prime + 360) / 2 : (h1Prime + h2Prime - 360) / 2;
        }
        var t = 1 - 0.17 * Math.Cos(Radians(meanH - 30)) + 0.24 * Math.Cos(Radians(2 * meanH))
            + 0.32 * Math.Cos(Radians(3 * meanH + 6)) - 0.20 * Math.Cos(Radians(4 * meanH - 63));
        var deltaTheta = 30 * Math.Exp(-Math.Pow((meanH - 275) / 25, 2));
        var meanCPrime7 = Math.Pow(meanCPrime, 7);
        var rC = 2 * Math.Sqrt(meanCPrime7 / (meanCPrime7 + Pow25To7));
        var lightness = (meanL - 50) * (meanL - 50);
        var sL = 1 + 0.015 * lightness / Math.Sqrt(20 + lightness);
        var sC = 1 + 0.045 * meanCPrime;
        var sH = 1 + 0.015 * meanCPrime * t;
        var rT = -Math.Sin(Radians(2 * deltaTheta)) * rC;
        double termL = deltaL / sL, termC = deltaC / sC, termH = deltaH / sH;
        return Math.Sqrt(termL * termL + termC * termC + termH * termH + rT * termC * termH);
    }

    private static double Hue(double b, double a)
    {
        if (a == 0 && b == 0)
        {
            return 0;
        }
        var degrees = Math.Atan2(b, a) * 180 / Math.PI;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    private static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;

    private static double[] BuildLinearTable()
    {
        var table = new double[256];
        for (var index = 0; index < 256; index++)
        {
            var c = index / 255.0;
            table[index] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return table;
    }
}
