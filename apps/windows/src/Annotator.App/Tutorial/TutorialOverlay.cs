using System.Numerics;
using Annotator.Core.Tutorial;
using Annotator.Platform.Displays;
using Annotator.Platform.Windowing;
using Windows.UI;
using Windows.UI.Composition;
using CorePoint = Annotator.Core.Point;
using CoreSize = Annotator.Core.Geometry.Size;

namespace Annotator.App.Tutorial;

/// <summary>A key press the card plays at <paramref name="Time"/> seconds into a demonstration.</summary>
internal readonly record struct DemoKeyCue(double Time, IReadOnlyList<string> Keys);

/// <summary>
/// A demonstration resolved to pixels on the overlay (Mac TutorialDemoTimeline): where the ghost is over time, when it presses,
/// where its clicks ripple, what its ink draws and when the card's keycaps go down. Times are seconds from the start.
/// </summary>
internal sealed class DemoTimeline
{
    public List<(double Time, Vector2 Position)> Path { get; } = [];

    public List<(double Time, float Scale)> Press { get; } = [];

    public List<(double Time, Vector2 Position)> Ripples { get; } = [];

    /// <summary>Ink lines, each shown from its time.</summary>
    public List<(double Time, Vector2 From, Vector2 To)> Ink { get; } = [];

    /// <summary>A rectangle growing from a corner to the ghost between two times.</summary>
    public List<(double Start, double End, Vector2 From, Vector2 To)> Boxes { get; } = [];

    public List<DemoKeyCue> Keys { get; } = [];

    public double Appear { get; set; }

    public double Vanish { get; set; }

    public double Duration { get; set; }
}

/// <summary>
/// The tutorial's layer on one display (Mac TutorialOverlay): the ring around the control to use, the ghost's demonstrations
/// and the confetti. Every animation runs once in the compositor and is removed when it ends or is stopped; with nothing to
/// show, the window hides. All positions are physical pixels from the display's top-left corner.
/// </summary>
internal sealed class TutorialOverlay : IDisposable
{
    /// <summary>The ghost's pointer outline in points, tip at the origin (Mac TutorialGhost.outline).</summary>
    private static readonly Vector2[] s_pointer =
    [
        new(0, 0), new(0, 16), new(4, 12.5f), new(7, 18.5f), new(9.2f, 17.5f), new(6.3f, 11.6f), new(11.5f, 11.6f),
    ];

    private readonly WindowRegistry _registry;
    private readonly Compositor _compositor;
    private readonly Color _accent;
    private TutorialOverlayWindow? _window;
    private PixelRect _frame;
    private double _scale = 1;
    private ContainerVisual? _demo;
    private ShapeVisual? _ring;
    private readonly List<ContainerVisual> _confetti = [];
    private int _generation;

    public TutorialOverlay(WindowRegistry registry, Compositor compositor, Color accent)
    {
        _registry = registry;
        _compositor = compositor;
        _accent = accent;
    }

    /// <summary>Whether a demonstration is playing.</summary>
    public bool IsPlaying => _demo is not null;

    /// <summary>Moves the layer to the display at <paramref name="frame"/> (physical pixels) with <paramref name="scale"/> pixels per point.</summary>
    public void Place(PixelRect frame, double scale)
    {
        if (frame == _frame && scale == _scale)
        {
            return;
        }
        _frame = frame;
        _scale = scale;
        if (_window is { IsVisible: true } window)
        {
            window.Show(frame);
        }
    }

    /// <summary>
    /// Shows the ring around <paramref name="anchor"/> (physical pixels on the screen), with one pulse in unless
    /// <paramref name="reduceMotion"/>; null removes it.
    /// </summary>
    public void Ring(Windows.Graphics.RectInt32? anchor, bool reduceMotion)
    {
        RemoveRing(animated: anchor is null && !reduceMotion);
        if (anchor is not { } rect || _frame.IsEmpty)
        {
            Refresh();
            return;
        }
        float outset = (float)(TutorialMotion.TargetOutset * _scale);
        float width = (float)(TutorialMotion.TargetWidth * _scale);
        var size = new Vector2(rect.Width + outset * 2, rect.Height + outset * 2);
        ShapeVisual visual = _compositor.CreateShapeVisual();
        visual.Size = size + new Vector2(width * 2);
        visual.Offset = new Vector3(rect.X - _frame.Left - outset - width, rect.Y - _frame.Top - outset - width, 0);
        CompositionRoundedRectangleGeometry geometry = _compositor.CreateRoundedRectangleGeometry();
        geometry.Offset = new Vector2(width);
        geometry.Size = size;
        geometry.CornerRadius = new Vector2((float)(TutorialMotion.TargetCornerRadius * _scale));
        CompositionSpriteShape shape = _compositor.CreateSpriteShape(geometry);
        shape.StrokeBrush = _compositor.CreateColorBrush(_accent);
        shape.StrokeThickness = width;
        visual.Shapes.Add(shape);
        visual.CenterPoint = new Vector3(visual.Size / 2, 0);
        Show().Root.Children.InsertAtTop(visual);
        _ring = visual;
        if (reduceMotion)
        {
            return;
        }
        TimeSpan pulse = TimeSpan.FromSeconds(TutorialMotion.TargetPulseDuration);
        Vector3KeyFrameAnimation grow = _compositor.CreateVector3KeyFrameAnimation();
        grow.InsertKeyFrame(0, new Vector3((float)TutorialMotion.TargetPulseScale, (float)TutorialMotion.TargetPulseScale, 1));
        grow.InsertKeyFrame(1, Vector3.One, Easing(TutorialMotion.Pop));
        grow.Duration = pulse;
        visual.StartAnimation("Scale", grow);
        ScalarKeyFrameAnimation fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(0.5f, 1, Easing(Annotator.Core.Motion.MotionCurves.Fade));
        fade.InsertKeyFrame(1, 1);
        fade.Duration = pulse;
        visual.StartAnimation("Opacity", fade);
    }

    /// <summary>Plays <paramref name="timeline"/> once; a demonstration already playing stops first.</summary>
    public void Play(DemoTimeline timeline)
    {
        StopDemo();
        if (_frame.IsEmpty || timeline.Duration <= 0)
        {
            return;
        }
        int generation = ++_generation;
        ContainerVisual demo = _compositor.CreateContainerVisual();
        demo.RelativeSizeAdjustment = Vector2.One;
        Show().Root.Children.InsertAtTop(demo);
        _demo = demo;
        CompositionScopedBatch batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        double total = timeline.Duration;
        AddInk(demo, timeline, total);
        AddRipples(demo, timeline);
        AddGhost(demo, timeline, total);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (generation == _generation)
            {
                StopDemo();
            }
        };
    }

    /// <summary>Stops a demonstration at once and removes the ghost and its ink.</summary>
    public void StopDemo()
    {
        _generation++;
        if (_demo is { } demo)
        {
            _demo = null;
            _window?.Root.Children.Remove(demo);
            demo.Dispose();
        }
        Refresh();
    }

    /// <summary>
    /// Confetti pops from both bottom corners of the display, drifts to the middle and floats down (Mac
    /// TutorialOverlay.celebrate). It goes when its last piece lands.
    /// </summary>
    public void Celebrate()
    {
        if (_frame.IsEmpty)
        {
            return;
        }
        var size = new CoreSize(_frame.Width / _scale, _frame.Height / _scale);
        ContainerVisual confetti = _compositor.CreateContainerVisual();
        confetti.RelativeSizeAdjustment = Vector2.One;
        Show().Root.Children.InsertAtTop(confetti);
        _confetti.Add(confetti);
        CompositionScopedBatch batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        foreach (ConfettiPiece piece in ConfettiBurst.Pieces(size))
        {
            confetti.Children.InsertAtTop(Piece(piece, size.Height));
        }
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (_confetti.Remove(confetti))
            {
                _window?.Root.Children.Remove(confetti);
                confetti.Dispose();
                Refresh();
            }
        };
    }

    /// <summary>Removes everything and hides the layer.</summary>
    public void Clear()
    {
        StopDemo();
        RemoveRing(animated: false);
        RemoveConfetti();
        Refresh();
    }

    public void Dispose()
    {
        Clear();
        _window?.Dispose();
        _window = null;
    }

    private TutorialOverlayWindow Show()
    {
        _window ??= new TutorialOverlayWindow(_registry, _compositor);
        // Shown again each time so the layer stays above the toolbar, which also keeps itself on top.
        _window.Show(_frame);
        return _window;
    }

    /// <summary>Hides the layer once it has nothing to show, so an idle tutorial costs nothing.</summary>
    private void Refresh()
    {
        if (_demo is null && _ring is null && _confetti.Count == 0 && _window is { IsVisible: true } window)
        {
            window.Hide();
        }
    }

    private void RemoveRing(bool animated)
    {
        if (_ring is not { } ring)
        {
            return;
        }
        _ring = null;
        if (!animated || _window is null)
        {
            _window?.Root.Children.Remove(ring);
            ring.Dispose();
            return;
        }
        CompositionScopedBatch batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        ScalarKeyFrameAnimation fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 0, Easing(Annotator.Core.Motion.MotionCurves.Fade));
        fade.Duration = TimeSpan.FromSeconds(TutorialMotion.TargetFadeDuration);
        ring.StartAnimation("Opacity", fade);
        batch.End();
        batch.Completed += (_, _) =>
        {
            _window?.Root.Children.Remove(ring);
            ring.Dispose();
            Refresh();
        };
    }

    private void RemoveConfetti()
    {
        foreach (ContainerVisual confetti in _confetti)
        {
            _window?.Root.Children.Remove(confetti);
            confetti.Dispose();
        }
        _confetti.Clear();
    }

    /// <summary>The ghost: a soft accent halo under a hollow pointer outline, dark under white, moving along the timeline.</summary>
    private void AddGhost(ContainerVisual demo, DemoTimeline timeline, double total)
    {
        float s = (float)_scale;
        float tip = 24 * s;
        ShapeVisual ghost = _compositor.CreateShapeVisual();
        ghost.Size = new Vector2(72 * s);
        ghost.CenterPoint = new Vector3(tip, tip, 0);
        ghost.Opacity = 0;
        CompositionEllipseGeometry halo = _compositor.CreateEllipseGeometry();
        halo.Center = new Vector2(tip);
        halo.Radius = new Vector2(18 * s);
        CompositionSpriteShape haloShape = _compositor.CreateSpriteShape(halo);
        haloShape.FillBrush = _compositor.CreateColorBrush(WithAlpha(_accent, 0.22));
        ghost.Shapes.Add(haloShape);
        AddOutline(ghost, Color.FromArgb(140, 0, 0, 0), 3.5f * s, tip);
        AddOutline(ghost, Color.FromArgb(255, 255, 255, 255), 1.8f * s, tip);
        demo.Children.InsertAtTop(ghost);

        TimeSpan duration = TimeSpan.FromSeconds(total);
        Vector3KeyFrameAnimation move = _compositor.CreateVector3KeyFrameAnimation();
        foreach ((double time, Vector2 position) in timeline.Path)
        {
            move.InsertKeyFrame(Normalized(time, total), new Vector3(position - new Vector2(tip), 0), _compositor.CreateLinearEasingFunction());
        }
        move.Duration = duration;
        ghost.StartAnimation("Offset", move);

        Vector3KeyFrameAnimation press = _compositor.CreateVector3KeyFrameAnimation();
        float appearScale = (float)TutorialMotion.GhostAppearScale;
        press.InsertKeyFrame(0, new Vector3(appearScale, appearScale, 1));
        press.InsertKeyFrame(Normalized(timeline.Appear + TutorialMotion.GhostAppearDuration, total), Vector3.One, Easing(Annotator.Core.Motion.MotionCurves.Enter));
        foreach ((double time, float scale) in timeline.Press)
        {
            press.InsertKeyFrame(Normalized(time, total), new Vector3(scale, scale, 1), Easing(Annotator.Core.Motion.MotionCurves.Standard));
        }
        press.Duration = duration;
        ghost.StartAnimation("Scale", press);

        ScalarKeyFrameAnimation opacity = _compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0);
        opacity.InsertKeyFrame(Normalized(timeline.Appear, total), 0);
        opacity.InsertKeyFrame(Normalized(timeline.Appear + TutorialMotion.GhostAppearDuration, total), 1, Easing(Annotator.Core.Motion.MotionCurves.Fade));
        opacity.InsertKeyFrame(Normalized(timeline.Vanish, total), 1);
        opacity.InsertKeyFrame(Normalized(timeline.Vanish + TutorialMotion.GhostVanishDuration, total), 0, Easing(Annotator.Core.Motion.MotionCurves.Fade));
        opacity.Duration = duration;
        ghost.StartAnimation("Opacity", opacity);
    }

    private void AddOutline(ShapeVisual ghost, Color color, float width, float tip)
    {
        float s = (float)_scale;
        for (int index = 0; index < s_pointer.Length; index++)
        {
            CompositionLineGeometry line = _compositor.CreateLineGeometry();
            line.Start = s_pointer[index] * s + new Vector2(tip);
            line.End = s_pointer[(index + 1) % s_pointer.Length] * s + new Vector2(tip);
            CompositionSpriteShape shape = _compositor.CreateSpriteShape(line);
            shape.StrokeBrush = _compositor.CreateColorBrush(color);
            shape.StrokeThickness = width;
            shape.StrokeStartCap = CompositionStrokeCap.Round;
            shape.StrokeEndCap = CompositionStrokeCap.Round;
            ghost.Shapes.Add(shape);
        }
    }

    /// <summary>One accent ring per click, growing and fading once.</summary>
    private void AddRipples(ContainerVisual demo, DemoTimeline timeline)
    {
        if (timeline.Ripples.Count == 0)
        {
            return;
        }
        ShapeVisual layer = _compositor.CreateShapeVisual();
        layer.RelativeSizeAdjustment = Vector2.One;
        demo.Children.InsertAtTop(layer);
        float s = (float)_scale;
        TimeSpan duration = TimeSpan.FromSeconds(TutorialMotion.RippleDuration);
        foreach ((double time, Vector2 position) in timeline.Ripples)
        {
            CompositionEllipseGeometry ring = _compositor.CreateEllipseGeometry();
            ring.Center = position;
            ring.Radius = new Vector2((float)TutorialMotion.RippleStartRadius * s);
            CompositionSpriteShape shape = _compositor.CreateSpriteShape(ring);
            CompositionColorBrush brush = _compositor.CreateColorBrush(WithAlpha(_accent, 0));
            shape.StrokeBrush = brush;
            shape.StrokeThickness = 2 * s;
            layer.Shapes.Add(shape);
            Vector2KeyFrameAnimation grow = _compositor.CreateVector2KeyFrameAnimation();
            grow.InsertKeyFrame(0, new Vector2((float)TutorialMotion.RippleStartRadius * s));
            grow.InsertKeyFrame(1, new Vector2((float)TutorialMotion.RippleEndRadius * s), Easing(Annotator.Core.Motion.MotionCurves.Enter));
            grow.Duration = duration;
            grow.DelayTime = TimeSpan.FromSeconds(time);
            ring.StartAnimation("Radius", grow);
            ColorKeyFrameAnimation fade = _compositor.CreateColorKeyFrameAnimation();
            fade.InsertKeyFrame(0, WithAlpha(_accent, TutorialMotion.RippleStartOpacity));
            fade.InsertKeyFrame(1, WithAlpha(_accent, 0), Easing(Annotator.Core.Motion.MotionCurves.Fade));
            fade.Duration = duration;
            fade.DelayTime = TimeSpan.FromSeconds(time);
            brush.StartAnimation("Color", fade);
        }
    }

    /// <summary>The ghost's ink: lines that appear as the ghost draws them and rectangles that follow it, all fading at the end.</summary>
    private void AddInk(ContainerVisual demo, DemoTimeline timeline, double total)
    {
        if (timeline.Ink.Count == 0 && timeline.Boxes.Count == 0)
        {
            return;
        }
        ShapeVisual layer = _compositor.CreateShapeVisual();
        layer.RelativeSizeAdjustment = Vector2.One;
        demo.Children.InsertAtTop(layer);
        float width = (float)(TutorialMotion.InkWidth * _scale);
        Color ink = WithAlpha(_accent, TutorialMotion.InkOpacity);
        TimeSpan duration = TimeSpan.FromSeconds(total);
        foreach ((double time, Vector2 from, Vector2 to) in timeline.Ink)
        {
            CompositionLineGeometry line = _compositor.CreateLineGeometry();
            line.Start = from;
            line.End = to;
            CompositionSpriteShape shape = _compositor.CreateSpriteShape(line);
            CompositionColorBrush brush = _compositor.CreateColorBrush(WithAlpha(ink, 0));
            shape.StrokeBrush = brush;
            shape.StrokeThickness = width;
            shape.StrokeStartCap = CompositionStrokeCap.Round;
            shape.StrokeEndCap = CompositionStrokeCap.Round;
            layer.Shapes.Add(shape);
            ColorKeyFrameAnimation show = _compositor.CreateColorKeyFrameAnimation();
            show.InsertKeyFrame(0, WithAlpha(ink, 0));
            show.InsertKeyFrame(Normalized(time, total), WithAlpha(ink, 0));
            show.InsertKeyFrame(Normalized(time + 0.03, total), ink);
            show.InsertKeyFrame(Normalized(timeline.Vanish, total), ink);
            show.InsertKeyFrame(Normalized(timeline.Vanish + TutorialMotion.InkFadeDuration, total), WithAlpha(ink, 0));
            show.Duration = duration;
            brush.StartAnimation("Color", show);
        }
        foreach ((double start, double end, Vector2 from, Vector2 to) in timeline.Boxes)
        {
            CompositionRectangleGeometry box = _compositor.CreateRectangleGeometry();
            box.Offset = Vector2.Min(from, to);
            box.Size = Vector2.Zero;
            CompositionSpriteShape shape = _compositor.CreateSpriteShape(box);
            CompositionColorBrush brush = _compositor.CreateColorBrush(WithAlpha(ink, 0));
            shape.StrokeBrush = brush;
            shape.StrokeThickness = width;
            layer.Shapes.Add(shape);
            Vector2KeyFrameAnimation grow = _compositor.CreateVector2KeyFrameAnimation();
            grow.InsertKeyFrame(0, Vector2.Zero);
            grow.InsertKeyFrame(Normalized(start, total), Vector2.Zero);
            grow.InsertKeyFrame(Normalized(end, total), Vector2.Abs(to - from), Easing(Annotator.Core.Motion.MotionCurves.Standard));
            grow.InsertKeyFrame(1, Vector2.Abs(to - from));
            grow.Duration = duration;
            box.StartAnimation("Size", grow);
            ColorKeyFrameAnimation show = _compositor.CreateColorKeyFrameAnimation();
            show.InsertKeyFrame(0, WithAlpha(ink, 0));
            show.InsertKeyFrame(Normalized(start, total), WithAlpha(ink, 0));
            show.InsertKeyFrame(Normalized(start + 0.03, total), ink);
            show.InsertKeyFrame(Normalized(timeline.Vanish, total), ink);
            show.InsertKeyFrame(Normalized(timeline.Vanish + TutorialMotion.InkFadeDuration, total), WithAlpha(ink, 0));
            show.Duration = duration;
            brush.StartAnimation("Color", show);
        }
    }

    /// <summary>One piece of confetti, its path sampled from the closed form 20 times a second.</summary>
    private Visual Piece(ConfettiPiece piece, double height)
    {
        float s = (float)_scale;
        CoreSize shapeSize = ConfettiBurst.ShapeSizes[piece.Shape];
        var size = new Vector2((float)shapeSize.Width * s, (float)shapeSize.Height * s);
        ShapeVisual visual = _compositor.CreateShapeVisual();
        visual.Size = size;
        visual.CenterPoint = new Vector3(size / 2, 0);
        visual.Opacity = 0;
        CompositionGeometry geometry;
        if (piece.Shape == 1)
        {
            CompositionEllipseGeometry dot = _compositor.CreateEllipseGeometry();
            dot.Center = size / 2;
            dot.Radius = size / 2;
            geometry = dot;
        }
        else
        {
            CompositionRoundedRectangleGeometry strip = _compositor.CreateRoundedRectangleGeometry();
            strip.Size = size;
            strip.CornerRadius = new Vector2(1.5f * s);
            geometry = strip;
        }
        CompositionSpriteShape shape = _compositor.CreateSpriteShape(geometry);
        Annotator.Core.RGBA color = ConfettiBurst.Palette[piece.Color];
        shape.FillBrush = _compositor.CreateColorBrush(Color.FromArgb(255, Channel(color.Red), Channel(color.Green), Channel(color.Blue)));
        visual.Shapes.Add(shape);

        TimeSpan duration = TimeSpan.FromSeconds(piece.Lifetime);
        TimeSpan delay = TimeSpan.FromSeconds(piece.Delay);
        Vector3KeyFrameAnimation move = _compositor.CreateVector3KeyFrameAnimation();
        Vector3KeyFrameAnimation flutter = _compositor.CreateVector3KeyFrameAnimation();
        int samples = Math.Max(2, (int)Math.Ceiling(piece.Lifetime * 20));
        for (int index = 0; index <= samples; index++)
        {
            double t = piece.Lifetime * index / samples;
            if (ConfettiBurst.State(piece, piece.Delay + t, height) is not { } state)
            {
                continue;
            }
            float progress = (float)(index / (double)samples);
            var center = new Vector2((float)(state.Position.X * _scale), (float)(state.Position.Y * _scale));
            move.InsertKeyFrame(progress, new Vector3(center - size / 2, 0), _compositor.CreateLinearEasingFunction());
            float width = (float)Math.Max(0.08, Math.Abs(state.WidthScale));
            flutter.InsertKeyFrame(progress, new Vector3(width, 1, 1), _compositor.CreateLinearEasingFunction());
        }
        move.Duration = duration;
        move.DelayTime = delay;
        flutter.Duration = duration;
        flutter.DelayTime = delay;
        visual.StartAnimation("Offset", move);
        visual.StartAnimation("Scale", flutter);

        ScalarKeyFrameAnimation spin = _compositor.CreateScalarKeyFrameAnimation();
        spin.InsertKeyFrame(0, (float)piece.Angle);
        spin.InsertKeyFrame(1, (float)(piece.Angle + piece.Spin * piece.Lifetime), _compositor.CreateLinearEasingFunction());
        spin.Duration = duration;
        spin.DelayTime = delay;
        visual.StartAnimation("RotationAngleInDegrees", spin);

        ScalarKeyFrameAnimation fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 1);
        fade.InsertKeyFrame((float)Math.Max(0, 1 - ConfettiBurst.FadeDuration / piece.Lifetime), 1);
        fade.InsertKeyFrame(1, 0, _compositor.CreateLinearEasingFunction());
        fade.Duration = duration;
        fade.DelayTime = delay;
        visual.StartAnimation("Opacity", fade);
        return visual;
    }

    private CubicBezierEasingFunction Easing(Annotator.Core.Motion.CubicBezier curve) =>
        _compositor.CreateCubicBezierEasingFunction(new Vector2((float)curve.X1, (float)curve.Y1), new Vector2((float)curve.X2, (float)curve.Y2));

    private static float Normalized(double time, double total) => (float)Math.Clamp(time / total, 0, 1);

    private static Color WithAlpha(Color color, double opacity) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * color.A), color.R, color.G, color.B);

    private static byte Channel(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);

    /// <summary>Converts a point on the display (points) to the layer's pixels.</summary>
    public Vector2 FromDisplay(CorePoint point) => new((float)(point.X * _scale), (float)(point.Y * _scale));

    public double Scale => _scale;

    public PixelRect Frame => _frame;
}
