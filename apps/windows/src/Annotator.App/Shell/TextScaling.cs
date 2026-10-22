using Windows.UI.ViewManagement;

namespace Annotator.App.Shell;

/// <summary>
/// Windows' Text size setting (Settings > Accessibility > Text size, 100% to 225%) for app windows (WIN-A11Y-03). WinUI grows
/// every text by it; the parts that keep the Mac's exact metrics (line heights, fixed rows and buttons) grow with it here, so
/// larger text never overlaps or gets cut off, and they keep the Mac's sizes at 100%. Development builds can simulate a size
/// for checks (the UX reference's <c>--text-scale</c>), which never changes the Windows setting.
/// </summary>
internal static class TextScaling
{
    private static UISettings? s_settings;
    private static double? s_simulated;

    /// <summary>Development only: the text size WinUI was told to simulate.</summary>
    public static void Simulate(double factor) => s_simulated = factor;

    /// <summary>The text size factor, 1 to 2.25.</summary>
    public static double Factor => s_simulated ?? (s_settings ??= new UISettings()).TextScaleFactor;

    /// <summary>True while Windows shows text larger than 100%.</summary>
    public static bool Scaled => Factor > 1;

    /// <summary>
    /// How much app windows grow (<see cref="ZoomPanel"/>): the text size factor. The windows keep the Mac's layouts, so at 150%
    /// text a window is 1.5 times as large, with its text, symbols and spacing, instead of letting its text outgrow its
    /// controls.
    /// </summary>
    public static double Zoom => Factor;

    /// <summary>
    /// The zoom for a window of <paramref name="width"/> by <paramref name="height"/> points at 100%: <see cref="Zoom"/>, or less
    /// when the window would not fit in <paramref name="room"/> points; never below 1.
    /// </summary>
    public static double Fit(double width, double height, Windows.Foundation.Size room) =>
        Math.Max(1, Math.Min(Zoom, Math.Min(width > 0 ? room.Width / width : Zoom, height > 0 ? room.Height / height : Zoom)));

    /// <summary>
    /// The size WinUI draws <paramref name="size"/>-point text at: small text grows more than large text
    /// (TextFormatting::GetScaledFontSize in WinUI: size + max(18 - e ln size, 0) (factor - 1)).
    /// </summary>
    public static double Size(double size)
    {
        double factor = Factor;
        return factor <= 1 ? size : size + Math.Max(18 - Math.E * Math.Log(Math.Max(size, 1)), 0) * (factor - 1);
    }

    /// <summary>A Mac line height for <paramref name="size"/>-point text, grown in proportion to the text.</summary>
    public static double Line(double lineHeight, double size) => Scaled ? lineHeight * Size(size) / size : lineHeight;

    /// <summary>A fixed Mac height around one line of <paramref name="size"/>-point text, grown by as much as the line grows.</summary>
    public static double Around(double height, double size, double lineHeight) => Scaled ? height + Line(lineHeight, size) - lineHeight : height;
}
