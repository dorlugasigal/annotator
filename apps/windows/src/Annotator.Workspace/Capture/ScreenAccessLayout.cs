namespace Annotator.Workspace.Capture;

/// <summary>
/// Where the explanation goes (Windows only). Windows centers its capture questions in the work area of the display, above
/// every app, so the explanation is centered across and ends <see cref="QuestionClearance"/> points above the middle: both
/// stay readable, and the pointer that chose Allow screen access is never over Windows' Yes or No. A work area too short
/// for that puts the explanation at its top.
/// </summary>
public static class ScreenAccessLayout
{
    /// <summary>
    /// Points between the middle of the work area and the explanation's bottom edge: half of Windows' question (530 by 186
    /// points with a one-line title, measured on build 26200), room for a second title line, and a 20-point gap.
    /// </summary>
    public const double QuestionClearance = 130;

    /// <summary>The window's top-left corner in pixels, for its size and a work area in pixels at <paramref name="scale"/>.</summary>
    public static (int X, int Y) Origin(int workX, int workY, int workWidth, int workHeight, int width, int height, double scale)
    {
        int top = workY + workHeight / 2 - (int)Math.Round(QuestionClearance * scale) - height;
        return (workX + Math.Max(0, (workWidth - width) / 2), Math.Max(workY, top));
    }
}
