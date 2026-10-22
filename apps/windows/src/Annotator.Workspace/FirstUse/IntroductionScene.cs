using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Ease = Annotator.Workspace.FirstUse.IntroductionEase;

namespace Annotator.Workspace.FirstUse;

/// <summary>
/// Port of the Mac's IntroductionScene (IntroductionIllustrations.swift): a small scene for one tour page, drawn for one
/// moment of its story. The artwork is the Mac's own; text uses the Windows fonts (D-4) on the Mac's baselines, SF
/// Symbols their Windows icons (D-6), and rounded corners are circular where SwiftUI's are continuous.
/// </summary>
public sealed class IntroductionScene
{
    /// <summary>The illustration's size in points (the Mac's frame(height: 120) across the page's 472 points).</summary>
    public static readonly Size StandardSize = new(472, 120);

    public static readonly RGBA Accent = new(0.17, 0.45, 0.88);
    public static readonly RGBA Ink = new(0.94, 0.27, 0.22);
    public static readonly RGBA Halo = new(1, 0.82, 0);

    private static readonly RGBA White = new(1, 1, 1);
    private static readonly RGBA Black = new(0, 0, 0);
    private static readonly IllustrationStop[] s_sunset = IllustrationPaint.Even([.. GradientPreset.Sunset.Gradient.Colors]);
    private static readonly IllustrationStop[] s_wallpaperLight = IllustrationPaint.Even(new RGBA(0.87, 0.91, 0.98), new RGBA(0.97, 0.91, 0.95));
    private static readonly IllustrationStop[] s_wallpaperDark = IllustrationPaint.Even(new RGBA(0.11, 0.15, 0.23), new RGBA(0.2, 0.18, 0.3));
    private static readonly IllustrationStop[] s_glowLight = IllustrationPaint.Even(new RGBA(1, 1, 1, 0.5), new RGBA(1, 1, 1, 0));
    private static readonly IllustrationStop[] s_glowDark = IllustrationPaint.Even(new RGBA(1, 1, 1, 0.07), new RGBA(1, 1, 1, 0));
    private static readonly IllustrationStop[] s_wheelLight = IllustrationPaint.Even(
        Hex(0xFF383C), Hex(0xFFCC00), Hex(0x34C759), Hex(0x32ADE6), Hex(0x0088FF), Hex(0xCB30E0), Hex(0xFF383C));
    private static readonly IllustrationStop[] s_wheelDark = IllustrationPaint.Even(
        Hex(0xFF4245), Hex(0xFFD600), Hex(0x30D158), Hex(0x64D2FF), Hex(0x0091FF), Hex(0xDB34F2), Hex(0xFF4245));
    private static readonly TextFont s_send = new(FontDesign.System, FontWeight.Semibold, 7.5);
    private static readonly TextFont s_label = new(FontDesign.System, (FontWeight)700, 16);
    private static readonly TextFont s_small = new(FontDesign.System, (FontWeight)500, 6.5);
    private static readonly TextFont s_bold = new(FontDesign.System, (FontWeight)700, 6.5);
    private static readonly string[] s_toolbarSymbols = ["pencil.tip", "highlighter", "rectangle", "arrow.up.right", "textformat"];
    private static readonly Point[] s_pointer =
        [new(0, 0), new(0, 15.5), new(3.7, 12), new(6.3, 17.6), new(8.8, 16.5), new(6.3, 11), new(11.2, 11)];

    private readonly Size _size;
    private readonly double _progress;
    private readonly bool _dark;

    public IntroductionScene(Size size, double progress, bool dark)
    {
        _size = size;
        _progress = progress;
        _dark = dark;
    }

    /// <summary>The label the color page types.</summary>
    public const string Label = "Make your point";

    private Rect Bounds => new(0, 0, _size.Width, _size.Height);

    private RGBA WindowFill => _dark ? new RGBA(0.17, 0.19, 0.24) : White;

    private RGBA Edge => _dark ? new RGBA(1, 1, 1, 0.09) : new RGBA(0, 0, 0, 0.07);

    private RGBA Chrome => _dark ? new RGBA(1, 1, 1, 0.2) : new RGBA(0, 0, 0, 0.13);

    private RGBA Line => _dark ? new RGBA(1, 1, 1, 0.13) : new RGBA(0, 0, 0, 0.08);

    private RGBA StrongLine => _dark ? new RGBA(1, 1, 1, 0.24) : new RGBA(0, 0, 0, 0.15);

    private RGBA Bar => _dark ? new RGBA(0.29, 0.35, 0.47) : new RGBA(0.8, 0.85, 0.93);

    private RGBA PrintColor => _dark ? new RGBA(1, 1, 1, 0.78) : new RGBA(0.2, 0.23, 0.28);

    /// <summary>Draws <paramref name="page"/>; the setup page has no illustration.</summary>
    public void Draw(IntroductionPage page, IIllustrationCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        switch (page)
        {
            case IntroductionPage.Draw:
                DrawShortcuts(canvas);
                break;
            case IntroductionPage.Color:
                DrawColor(canvas);
                break;
            case IntroductionPage.Focus:
                DrawFocus(canvas);
                break;
        }
    }

    // MARK: Shared pieces

    private static PathData RoundedRect(Rect rect, double radius) => new PathBuilder(10, 21).AddRoundedRect(rect, radius, radius).ToPathData();

    private static PathData Ellipse(Rect rect) => new PathBuilder(6, 13).AddEllipse(rect).ToPathData();

    private static PathData Circle(Point center, double radius) => Ellipse(new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));

    private static RGBA Opacity(RGBA color, double opacity) => color with { Alpha = color.Alpha * opacity };

    private static AffineTransform ScaleAbout(Point center, double scale) =>
        AffineTransform.Translation(center.X, center.Y).Scaled(scale, scale).Translated(-center.X, -center.Y);

    /// <summary>Clips everything after it to the desktop's rounded shape and fills the wallpaper; pop the clip after the frame.</summary>
    private void DrawDesktop(IIllustrationCanvas canvas)
    {
        PathData desktop = RoundedRect(Bounds, 12);
        canvas.PushClip(desktop);
        canvas.Fill(desktop, IllustrationPaint.Linear(_dark ? s_wallpaperDark : s_wallpaperLight, new Point(0, 0), new Point(_size.Width, _size.Height)));
        canvas.Fill(desktop, IllustrationPaint.Radial(_dark ? s_glowDark : s_glowLight, new Point(_size.Width * 0.18, 0), _size.Width * 0.55));
    }

    private void DrawFrame(IIllustrationCanvas canvas)
    {
        canvas.Stroke(RoundedRect(RectMath.Outset(Bounds, -0.5, -0.5), 11.5), IllustrationPaint.Solid(Edge), 1);
        canvas.Pop();
    }

    private void DrawWindow(IIllustrationCanvas canvas, Rect rect, bool titleBar = true)
    {
        PathData shape = RoundedRect(rect, 7);
        canvas.PushShadow(new RGBA(0, 0, 0, _dark ? 0.35 : 0.1), 7, 0, 2);
        canvas.Fill(shape, IllustrationPaint.Solid(WindowFill));
        canvas.Pop();
        canvas.Stroke(shape, IllustrationPaint.Solid(Edge), 0.75);
        if (!titleBar)
        {
            return;
        }
        for (int index = 0; index < 3; index++)
        {
            canvas.Fill(Circle(new Point(rect.X + 9 + index * 6.5, rect.Y + 7.5), 2), IllustrationPaint.Solid(Chrome));
        }
    }

    private void DrawLines(IIllustrationCanvas canvas, double x, double y, double width, double[] fractions, double step = 9, int? strong = null)
    {
        for (int index = 0; index < fractions.Length; index++)
        {
            var rect = new Rect(x, y + index * step, width * fractions[index], 4);
            canvas.Fill(RoundedRect(rect, 2), IllustrationPaint.Solid(index == strong ? StrongLine : Line));
        }
    }

    private static void DrawPointer(IIllustrationCanvas canvas, Point tip, double scale = 1, double opacity = 1)
    {
        if (!(opacity > 0))
        {
            return;
        }
        PathData path = new PathBuilder(8, 8).AddLines(s_pointer).Close().ToPathData();
        canvas.PushOpacity(opacity);
        canvas.PushTransform(AffineTransform.Translation(tip.X, tip.Y).Scaled(scale, scale));
        canvas.PushShadow(new RGBA(0, 0, 0, 0.3), 1.5, 0, 1);
        canvas.Stroke(path, IllustrationPaint.Solid(White), 2.4, LineCap.Butt, LineJoin.Round);
        canvas.Pop();
        canvas.Fill(path, IllustrationPaint.Solid(Black));
        canvas.Pop();
        canvas.Pop();
    }

    // MARK: Make a point

    /// <summary>Draw circles a chart, Use apps clicks through while the ink stays, Show / hide fades the ink out and back.</summary>
    private void DrawShortcuts(IIllustrationCanvas canvas)
    {
        DrawDesktop(canvas);
        double width = _size.Width, height = _size.Height, progress = _progress;
        var back = new Rect(width * 0.57, 30, width * 0.36, height - 42);
        DrawWindow(canvas, back);
        DrawLines(canvas, back.X + 12, back.Y + 22, back.Width - 24, [0.9, 0.62, 0.78, 0.5, 0.7], strong: 0);

        var front = new Rect(width * 0.07, 16, width * 0.56, height - 28);
        DrawWindow(canvas, front);
        double baseline = front.MaxY - 12, chartHeight = front.Height - 34;
        const double barWidth = 11, gap = 7;
        Rect target = default;
        double[] bars = [0.36, 0.58, 0.44, 0.9, 0.52];
        for (int index = 0; index < bars.Length; index++)
        {
            double barHeight = chartHeight * bars[index];
            var rect = new Rect(front.X + 18 + index * (barWidth + gap), baseline - barHeight, barWidth, barHeight);
            canvas.Fill(RoundedRect(rect, 2.5), IllustrationPaint.Solid(index == 3 ? Accent : Bar));
            if (index == 3)
            {
                target = rect;
            }
        }
        double textX = front.X + 18 + 5 * (barWidth + gap) + 14;
        DrawLines(canvas, textX, front.Y + 24, front.MaxX - 14 - textX, [0.95, 0.7, 0.84], step: 10, strong: 0);

        var button = new Rect(front.MaxX - 52, front.MaxY - 24, 40, 14);
        double click = Ease.Pulse(progress, 0.635, 0.05);
        canvas.Fill(RoundedRect(button, 7), IllustrationPaint.Solid(Opacity(Accent, 1 - 0.3 * click)));
        canvas.DrawText("Send", s_send, IllustrationPaint.Solid(White), button.Center, IllustrationAnchor.Center);

        // Show / hide fades every drawing out, then back in.
        double hidden = Ease.Smooth(Ease.Segment(progress, 0.745, 0.79)) * (1 - Ease.Smooth(Ease.Segment(progress, 0.84, 0.9)));
        Point center = target.Center;
        PathData loop = Loop(center, 17, target.Height / 2 + 9);
        double drawn = Ease.EaseInOut(Ease.Segment(progress, 0.13, 0.38));
        canvas.PushOpacity(1 - hidden);
        canvas.PushTransform(ScaleAbout(center, 1 - 0.05 * hidden));
        if (drawn > 0)
        {
            (PathData stroke, Point? end) = PathTrim.Start(loop, drawn);
            canvas.Stroke(stroke, IllustrationPaint.Solid(Ink), 2.6, LineCap.Round, LineJoin.Round);
            double head = Ease.Smooth(Ease.Segment(progress, 0.12, 0.15)) * (1 - Ease.Smooth(Ease.Segment(progress, 0.38, 0.42)));
            if (head > 0 && end is { } point)
            {
                canvas.PushOpacity(head);
                canvas.Fill(Circle(point, 4), IllustrationPaint.Solid(White));
                canvas.Fill(Circle(point, 2.8), IllustrationPaint.Solid(Ink));
                canvas.Pop();
            }
        }
        canvas.Pop();
        canvas.Pop();

        // The toolbar drops in for drawing and leaves when you go back to your apps.
        double arrive = Ease.Settle(Ease.Segment(progress, 0.02, 0.13));
        double leave = Ease.Smooth(Ease.Segment(progress, 0.42, 0.47));
        double toolbarOpacity = Ease.Clamp(arrive * 1.4) * (1 - leave);
        if (toolbarOpacity > 0)
        {
            var pill = new Rect(width * 0.74 - 52, 8 + (1 - arrive) * -9 - leave * 6, 104, 22);
            PathData pillShape = RoundedRect(pill, 11);
            canvas.PushOpacity(toolbarOpacity);
            canvas.PushShadow(new RGBA(0, 0, 0, 0.25), 5, 0, 2);
            canvas.Fill(pillShape, IllustrationPaint.Solid(new RGBA(0.13, 0.13, 0.13, 0.95)));
            canvas.Pop();
            canvas.Stroke(pillShape, IllustrationPaint.Solid(new RGBA(1, 1, 1, 0.1)), 0.75);
            for (int index = 0; index < s_toolbarSymbols.Length; index++)
            {
                var point = new Point(pill.X + 16 + index * 18, pill.Center.Y);
                if (index == 0)
                {
                    canvas.Fill(RoundedRect(new Rect(point.X - 8, point.Y - 8, 16, 16), 5), IllustrationPaint.Solid(Accent));
                }
                canvas.DrawSymbol(s_toolbarSymbols[index], point, 8.5, new RGBA(1, 1, 1, index == 0 ? 1 : 0.75));
            }
            canvas.Pop();
        }

        // Use apps: the pointer glides to the button and clicks it.
        double travel = Ease.EaseInOut(Ease.Segment(progress, 0.45, 0.62));
        var start = new Point(width * 0.99, height * 1.05);
        var finish = new Point(button.MaxX - 7, button.Center.Y + 2);
        var control = new Point(width * 0.86, height * 0.36);
        Point pointer = Bezier(start, control, finish, travel);
        double ripple = Ease.Segment(progress, 0.64, 0.74);
        if (ripple > 0 && ripple < 1)
        {
            double radius = 5 + 13 * Ease.EaseOut(ripple);
            canvas.Stroke(Circle(finish, radius), IllustrationPaint.Solid(Opacity(_dark ? White : Accent, 0.6 * (1 - ripple))), 1.5);
        }
        DrawPointer(canvas, pointer, 1 - 0.12 * click, Ease.Smooth(Ease.Segment(progress, 0.44, 0.48)));
        DrawFrame(canvas);
    }

    /// <summary>A loose hand-drawn loop that overshoots its start, like a quick circle made with a pen.</summary>
    public static PathData Loop(Point center, double radiusX, double radiusY)
    {
        const int steps = 96;
        const double start = -1.95, sweep = 2 * Math.PI + 0.6, tilt = -0.16;
        var builder = new PathBuilder(steps + 1, steps + 1);
        for (int step = 0; step <= steps; step++)
        {
            double t = step / (double)steps;
            double angle = start + sweep * t;
            double grow = 1 + 0.12 * t;
            double wobble = 1 + 0.035 * Math.Sin(3 * angle + 0.8);
            double x = radiusX * grow * wobble * Math.Cos(angle);
            double y = radiusY * grow * wobble * Math.Sin(angle);
            var point = new Point(center.X + (x * Math.Cos(tilt) - y * Math.Sin(tilt)), center.Y + (x * Math.Sin(tilt) + y * Math.Cos(tilt)));
            if (step == 0)
            {
                builder.MoveTo(point);
            }
            else
            {
                builder.LineTo(point);
            }
        }
        return builder.ToPathData();
    }

    public static Point Bezier(Point start, Point control, Point end, double t)
    {
        double u = 1 - t, v = t;
        return new Point(u * u * start.X + 2 * u * v * control.X + v * v * end.X, u * u * start.Y + 2 * u * v * control.Y + v * v * end.Y);
    }

    // MARK: Make it yours

    /// <summary>Pick the gradient, draw the outline, soften the fill, then type the label.</summary>
    private void DrawColor(IIllustrationCanvas canvas)
    {
        DrawDesktop(canvas);
        double width = _size.Width, height = _size.Height, progress = _progress;
        var card = new Rect(14, 14, 122, height - 28);
        DrawWindow(canvas, card, titleBar: false);

        RGBA[] swatches = [_dark ? White : new RGBA(0.12, 0.15, 0.16), Accent, new RGBA(0, 0.59, 0.62), new RGBA(0.96, 0.89, 0.22), Ink];
        var frames = new Rect[swatches.Length];
        for (int index = 0; index < swatches.Length; index++)
        {
            var frame = new Rect(card.X + 12 + index * 21.5, card.Y + 11, 14, 14);
            frames[index] = frame;
            canvas.Fill(Ellipse(frame), IllustrationPaint.Solid(swatches[index]));
            canvas.Stroke(Ellipse(frame), IllustrationPaint.Solid(Edge), 0.5);
        }
        var chip = new Rect(card.X + 12, card.Y + 33, 80, 14);
        canvas.Fill(RoundedRect(chip, 7), IllustrationPaint.Linear(s_sunset, new Point(chip.X, chip.Y), new Point(chip.MaxX, chip.MaxY)));
        var wheel = new Rect(card.MaxX - 26, chip.Y, 14, 14);
        canvas.Fill(Ellipse(wheel), IllustrationPaint.Conic(_dark ? s_wheelDark : s_wheelLight, wheel.Center));

        // The selection ring moves from the default color to the gradient.
        double pick = Ease.EaseInOut(Ease.Segment(progress, 0.08, 0.22));
        Rect from = RectMath.Outset(frames[4], 3, 3), to = RectMath.Outset(chip, 3, 3);
        var ring = new Rect(Ease.Mix(from.X, to.X, pick), Ease.Mix(from.Y, to.Y, pick), Ease.Mix(from.Width, to.Width, pick),
                            Ease.Mix(from.Height, to.Height, pick));
        canvas.Stroke(RoundedRect(ring, ring.Height / 2), IllustrationPaint.Solid(_dark ? new RGBA(1, 1, 1, 0.85) : new RGBA(0, 0, 0, 0.7)), 1.5);

        // Separate opacity controls: the outline stays full, the fill slides down.
        double fillAmount = 1 - 0.72 * Ease.EaseInOut(Ease.Segment(progress, 0.5, 0.74));
        double[] values = [1.0, fillAmount];
        for (int index = 0; index < values.Length; index++)
        {
            double y = card.Y + 61 + index * 13;
            var icon = new Rect(card.X + 12, y - 4, 8, 8);
            if (index == 0)
            {
                canvas.Stroke(Ellipse(RectMath.Outset(icon, -0.75, -0.75)), IllustrationPaint.Solid(StrongLine), 1.5);
            }
            else
            {
                canvas.Fill(Ellipse(icon), IllustrationPaint.Solid(StrongLine));
            }
            var track = new Rect(card.X + 27, y - 1.5, card.Width - 39, 3);
            canvas.Fill(RoundedRect(track, 1.5), IllustrationPaint.Solid(Line));
            var filled = new Rect(track.X, track.Y, track.Width * values[index], 3);
            canvas.Fill(RoundedRect(filled, 1.5), IllustrationPaint.Linear(s_sunset, new Point(track.X, 0), new Point(track.MaxX, 0)));
            var knob = new Rect(filled.MaxX - 4.5, y - 4.5, 9, 9);
            canvas.PushShadow(new RGBA(0, 0, 0, 0.25), 1.5, 0, 0.5);
            canvas.Fill(Ellipse(knob), IllustrationPaint.Solid(White));
            canvas.Pop();
        }

        var shape = new Rect(card.MaxX + 24, 22, width - card.MaxX - 46, height - 44);
        var start = new Point(shape.X, shape.Y);
        var end = new Point(shape.MaxX, shape.MaxY);
        double fill = Ease.Smooth(Ease.Segment(progress, 0.5, 0.76));
        if (fill > 0)
        {
            canvas.PushOpacity(0.22 * fill);
            canvas.Fill(RoundedRect(shape, 12), IllustrationPaint.Linear(s_sunset, start, end));
            canvas.Pop();
        }
        double drawn = Ease.EaseInOut(Ease.Segment(progress, 0.22, 0.62));
        if (drawn > 0)
        {
            (PathData outline, Point? tip) = PathTrim.Start(Outline(shape, 12), drawn);
            canvas.Stroke(outline, IllustrationPaint.Linear(s_sunset, start, end), 4.5, LineCap.Round, LineJoin.Round);
            if (drawn < 1 && tip is { } point)
            {
                canvas.PushOpacity(0.9 * Math.Sin(Math.PI * drawn));
                canvas.PushBlur(1.5);
                canvas.Fill(Circle(point, 3.5), IllustrationPaint.Solid(White));
                canvas.Pop();
                canvas.Pop();
            }
        }

        double typed = Ease.Segment(progress, 0.64, 0.92);
        int count = (int)Math.Round(Label.Length * typed, MidpointRounding.AwayFromZero);
        double fullWidth = canvas.MeasureText(Label, s_label);
        double left = shape.Center.X - fullWidth / 2;
        if (count > 0)
        {
            canvas.DrawText(Label[..count], s_label, IllustrationPaint.Linear(s_sunset, new Point(left, 0), new Point(left + fullWidth, 0)),
                new Point(left, shape.Center.Y), IllustrationAnchor.Leading);
        }
        double caret = Ease.Smooth(Ease.Segment(progress, 0.6, 0.64)) * (1 - Ease.Smooth(Ease.Segment(progress, 0.93, 0.98)));
        if (caret > 0)
        {
            double typedWidth = count > 0 ? canvas.MeasureText(Label[..count], s_label) : 0;
            var bar = new Rect(left + typedWidth + 1.5, shape.Center.Y - 8.5, 1.6, 17);
            canvas.Fill(RoundedRect(bar, 0.8), IllustrationPaint.Solid(Opacity(Ink, caret)));
        }
        DrawFrame(canvas);
    }

    /// <summary>A rounded outline that starts at the top center, so the stroke draws on in one clockwise pass.</summary>
    public static PathData Outline(Rect rect, double radius) => new PathBuilder(10, 20)
        .MoveTo(new Point(rect.Center.X, rect.Y))
        .AddArcToPoint(new Point(rect.MaxX, rect.Y), new Point(rect.MaxX, rect.MaxY), radius)
        .AddArcToPoint(new Point(rect.MaxX, rect.MaxY), new Point(rect.X, rect.MaxY), radius)
        .AddArcToPoint(new Point(rect.X, rect.MaxY), new Point(rect.X, rect.Y), radius)
        .AddArcToPoint(new Point(rect.X, rect.Y), new Point(rect.MaxX, rect.Y), radius)
        .Close()
        .ToPathData();

    // MARK: Lead the way

    /// <summary>The pointer arrives, its ring pulses once, Spotlight dims the rest and the ring opens into a zoom lens.</summary>
    private void DrawFocus(IIllustrationCanvas canvas)
    {
        DrawDesktop(canvas);
        double width = _size.Width, height = _size.Height, progress = _progress;
        var window = new Rect(width * 0.1, 12, width * 0.8, height - 22);
        var tip = new Point(window.X + window.Width * 0.7, window.Y + 50);
        var amount = new Point(tip.X - 3, tip.Y - 6);
        DrawFocusContent(canvas, window, amount);

        double travel = Ease.EaseInOut(Ease.Segment(progress, 0.02, 0.26));
        Point pointer = Bezier(new Point(width * 0.2, height * 1.15), new Point(width * 0.3, height * 0.3), tip, travel);
        double ringIn = Ease.Settle(Ease.Segment(progress, 0.24, 0.34));
        double spotlight = Ease.Smooth(Ease.Segment(progress, 0.44, 0.62));
        double lens = Ease.Settle(Ease.Segment(progress, 0.62, 0.86));
        double lensRadius = Ease.Mix(13, 40, lens);

        if (spotlight > 0)
        {
            double hole = 28 + 14 * Math.Min(1, lens), feather = 10;
            double dim = (_dark ? 0.55 : 0.32) * spotlight;
            IllustrationStop[] stops =
            [
                new(0, new RGBA(0, 0, 0, 0)), new(hole / (hole + feather), new RGBA(0, 0, 0, 0)), new(1, new RGBA(0, 0, 0, dim)),
            ];
            canvas.Fill(new PathBuilder(5, 4).AddRect(Bounds).ToPathData(), IllustrationPaint.Radial(stops, tip, hole + feather));
        }

        if (lens > 0)
        {
            PathData circle = Circle(tip, lensRadius);
            canvas.PushShadow(new RGBA(0, 0, 0, _dark ? 0.45 : 0.25), 7, 0, 3);
            canvas.Fill(circle, IllustrationPaint.Solid(WindowFill));
            canvas.Pop();
            canvas.PushClip(circle);
            canvas.PushTransform(ScaleAbout(tip, 1.8));
            DrawFocusContent(canvas, window, amount);
            canvas.Pop();
            canvas.Pop();
        }

        if (ringIn > 0)
        {
            PathData ring = Circle(tip, lensRadius);
            canvas.PushOpacity(Ease.Clamp(ringIn * 1.3));
            canvas.PushTransform(ScaleAbout(tip, 0.6 + 0.4 * ringIn));
            canvas.Stroke(ring, IllustrationPaint.Solid(Halo), 3 - 0.75 * Math.Min(1, lens));
            canvas.Pop();
            canvas.Pop();
        }
        double pulse = Ease.Segment(progress, 0.32, 0.46);
        if (pulse > 0 && pulse < 1)
        {
            double radius = 13 + 12 * Ease.EaseOut(pulse);
            canvas.Stroke(Circle(tip, radius), IllustrationPaint.Solid(Opacity(Halo, 0.7 * (1 - pulse))), 2);
        }
        DrawPointer(canvas, pointer, opacity: Ease.Smooth(Ease.Segment(progress, 0, 0.06)));
        DrawFrame(canvas);
    }

    /// <summary>A window of small print. <paramref name="amount"/> is the center of the total the pointer points at.</summary>
    private void DrawFocusContent(IIllustrationCanvas canvas, Rect window, Point amount)
    {
        DrawWindow(canvas, window);
        DrawLines(canvas, window.X + 14, window.Y + 22, window.Width * 0.42, [0.7, 1, 0.86, 0.94, 0.6, 0.8], step: 10, strong: 0);
        double label = amount.X - 62, right = amount.X + 17;
        IllustrationPaint quiet = IllustrationPaint.Solid(Opacity(PrintColor, 0.6));
        IllustrationPaint print = IllustrationPaint.Solid(PrintColor);
        canvas.DrawText("Subtotal", s_small, quiet, new Point(label, amount.Y - 22), IllustrationAnchor.Leading);
        canvas.DrawText("$1,176.00", s_small, quiet, new Point(right, amount.Y - 22), IllustrationAnchor.Trailing);
        canvas.DrawText("Tax", s_small, quiet, new Point(label, amount.Y - 12), IllustrationAnchor.Leading);
        canvas.DrawText("$108.00", s_small, quiet, new Point(right, amount.Y - 12), IllustrationAnchor.Trailing);
        canvas.Fill(new PathBuilder(5, 4).AddRect(new Rect(label, amount.Y - 6.5, right - label, 0.75)).ToPathData(), IllustrationPaint.Solid(Line));
        canvas.DrawText("Total", s_bold, print, new Point(label, amount.Y), IllustrationAnchor.Leading);
        canvas.DrawText("$1,284.00", s_bold, print, new Point(right, amount.Y), IllustrationAnchor.Trailing);
    }

    private static RGBA Hex(int rgb) => new(((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0);
}
