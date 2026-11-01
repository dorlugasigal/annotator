using Annotator.Core;

namespace Annotator.Workspace.Toolbar;

/// <summary>
/// What Windows says about see-through materials, for Glass toolbar (WIN-UI-09): Transparency effects (Settings &gt;
/// Personalization &gt; Colors), a contrast theme, the energy saver, and whether this computer can draw the material at all
/// (a remote session or a basic display driver can't).
/// </summary>
public readonly record struct SystemTransparency(bool Effects, bool ContrastTheme, bool EnergySaver, bool Supported = true)
{
    /// <summary>Windows draws see-through materials now: Transparency effects on, no contrast theme and no energy saver.</summary>
    public bool Allows => Supported && Effects && !ContrastTheme && !EnergySaver;
}

/// <summary>
/// Glass toolbar (WIN-UI-09, the Mac's <c>glass</c> preference): the toolbar shows the system's glass material only while the
/// person turned it on and Windows allows transparency. Otherwise it is solid, exactly as with the setting off, as the Mac's
/// toolbar is with Reduce transparency.
/// </summary>
public static class ToolbarGlass
{
    public static bool Shows(in WorkspacePreferences preferences, SystemTransparency system) =>
        preferences.Glass == true && system.Allows;

    /// <summary>
    /// The glass in a snapshot of the toolbar, for the show and hide motion (spec 009). A snapshot can't hold the material that
    /// Windows blurs from behind the window, so <paramref name="tint"/> goes under the snapshot's own pixels inside one glass
    /// surface: <paramref name="pixels"/> are premultiplied BGRA rows <paramref name="width"/> pixels wide, and
    /// <paramref name="surface"/> and <paramref name="radius"/> are in those pixels. The edge is smoothed over one pixel, as
    /// the material's clip is.
    /// </summary>
    public static void TintUnder(Span<byte> pixels, int width, int height, Rect surface, double radius, RGBA tint)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixels.Length, width * height * 4, nameof(pixels));
        int left = Math.Max(0, (int)Math.Floor(surface.X));
        int top = Math.Max(0, (int)Math.Floor(surface.Y));
        int right = Math.Min(width, (int)Math.Ceiling(surface.X + surface.Width));
        int bottom = Math.Min(height, (int)Math.Ceiling(surface.Y + surface.Height));
        double corner = Math.Clamp(radius, 0, Math.Min(surface.Width, surface.Height) / 2);
        double centerX = surface.X + surface.Width / 2, centerY = surface.Y + surface.Height / 2;
        double straightX = surface.Width / 2 - corner, straightY = surface.Height / 2 - corner;
        double alpha = Math.Clamp(tint.Alpha, 0, 1);
        double blue = tint.Blue * alpha * 255, green = tint.Green * alpha * 255, red = tint.Red * alpha * 255, opacity = alpha * 255;
        for (int y = top; y < bottom; y++)
        {
            double beyondY = Math.Abs(y + 0.5 - centerY) - straightY;
            for (int x = left; x < right; x++)
            {
                // The distance from the pixel's center to the rounded rectangle's edge, negative inside.
                double beyondX = Math.Abs(x + 0.5 - centerX) - straightX;
                double outsideX = Math.Max(beyondX, 0), outsideY = Math.Max(beyondY, 0);
                double distance = Math.Sqrt(outsideX * outsideX + outsideY * outsideY) + Math.Min(Math.Max(beyondX, beyondY), 0) - corner;
                double coverage = Math.Clamp(0.5 - distance, 0, 1);
                if (coverage <= 0)
                {
                    continue;
                }
                int index = (y * width + x) * 4;
                double under = (1 - pixels[index + 3] / 255.0) * coverage;
                pixels[index] = Add(pixels[index], blue * under);
                pixels[index + 1] = Add(pixels[index + 1], green * under);
                pixels[index + 2] = Add(pixels[index + 2], red * under);
                pixels[index + 3] = Add(pixels[index + 3], opacity * under);
            }
        }
    }

    private static byte Add(byte value, double more) => (byte)Math.Min(255, Math.Round(value + more));
}
