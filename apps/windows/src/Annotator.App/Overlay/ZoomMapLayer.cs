using System.Numerics;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Motion;
using Annotator.Platform.Capture;
using Annotator.Platform.Displays;
using Annotator.Rendering.Effects;
using Annotator.Rendering.Zoom;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Annotator.App.Overlay;

/// <summary>
/// Screen zoom's map (Mac LivePresentation.renderZoomMap and ZoomMapOutlineView): the whole display with the zoomed screen's
/// protection, in a corner, darkened outside the zoomed part, which is outlined in white. It has rounded corners and a thin
/// white border, sits above the canvas and takes no input. It fades in once its picture is drawn, and turns see-through
/// while the pointer is over it.
/// </summary>
internal sealed class ZoomMapLayer : IDisposable
{
    private readonly Compositor _compositor;
    private readonly ContainerVisual _parent;
    private readonly double _scale;
    private readonly ContainerVisual _root;
    private readonly CompositionRoundedRectangleGeometry _clip;
    private readonly CompositionColorBrush _dimBrush;
    private readonly SpriteVisual[] _dim = new SpriteVisual[4];
    private readonly ShapeVisual _lines;
    private readonly CompositionRectangleGeometry _outline;
    private readonly CompositionSpriteShape _outlineShape;
    private readonly CompositionRoundedRectangleGeometry _border;
    private readonly CompositionSpriteShape _borderShape;
    private readonly CapturedViewPresenter _picture;
    private PixelRect _frame;
    private Rect _zoomed;
    private bool _hovered;
    private bool _revealed;
    private float _opacity;
    private bool _disposed;

    /// <param name="parent">The canvas's container: the map goes on top of everything in it.</param>
    /// <param name="frame">The map's frame on its display, in display points (<see cref="ZoomMap.Frame"/>).</param>
    public ZoomMapLayer(Compositor compositor, ContainerVisual parent, IDisplayCapture capture, DisplayInfo display, double scale, Rect frame)
    {
        _compositor = compositor;
        _parent = parent;
        _scale = scale;
        _frame = Pixels(frame);
        _root = compositor.CreateContainerVisual();
        _root.IsVisible = false;
        _root.Opacity = 0;
        _clip = compositor.CreateRoundedRectangleGeometry();
        _root.Clip = compositor.CreateGeometricClip(_clip);
        _dimBrush = compositor.CreateColorBrush(Color.FromArgb(Alpha(ZoomMap.DimOpacity), 0, 0, 0));
        for (int index = 0; index < _dim.Length; index++)
        {
            _dim[index] = compositor.CreateSpriteVisual();
            _dim[index].Brush = _dimBrush;
            _root.Children.InsertAtTop(_dim[index]);
        }
        _outline = compositor.CreateRectangleGeometry();
        _outlineShape = compositor.CreateSpriteShape(_outline);
        _outlineShape.StrokeBrush = compositor.CreateColorBrush(Color.FromArgb(255, 255, 255, 255));
        _outlineShape.StrokeThickness = (float)(ZoomMap.OutlineWidth * scale);
        _border = compositor.CreateRoundedRectangleGeometry();
        _borderShape = compositor.CreateSpriteShape(_border);
        _borderShape.StrokeBrush = compositor.CreateColorBrush(Color.FromArgb(Alpha(ZoomMap.BorderOpacity), 255, 255, 255));
        _borderShape.StrokeThickness = (float)(ZoomMap.BorderWidth * scale);
        _lines = compositor.CreateShapeVisual();
        _lines.Shapes.Add(_outlineShape);
        _lines.Shapes.Add(_borderShape);
        _root.Children.InsertAtTop(_lines);
        try
        {
            _picture = CapturedViewPresenter.Create(new CapturedViewOptions
            {
                Compositor = compositor,
                Root = _root,
                AtBottom = true,
                Capture = capture,
                Display = display,
                PixelWidth = _frame.Width,
                PixelHeight = _frame.Height,
            });
        }
        catch
        {
            _root.Dispose();
            throw;
        }
        _picture.Changed += OnPictureChanged;
        parent.Children.InsertAtTop(_root);
        Layout();
    }

    /// <summary>The map's picture of the display, for the reason it failed.</summary>
    public CapturedViewPresenter Picture => _picture;

    /// <summary>Raised on the overlay thread when the map's picture fails.</summary>
    public event Action? Failed;

    /// <summary>
    /// Shows the display through <paramref name="effects"/> (the zoomed screen's protection), cropped to
    /// <paramref name="crop"/> (the whole frame, in frame pixels), in <paramref name="frame"/> (display points), with
    /// <paramref name="zoomed"/> (display points, <see cref="ZoomMap.ZoomedArea"/>) outlined. While
    /// <paramref name="hovered"/>, the map is see-through.
    /// </summary>
    public void Show(EffectList effects, Rect crop, Rect frame, Rect zoomed, bool hovered)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PixelRect pixels = Pixels(frame);
        if (pixels != _frame)
        {
            _frame = pixels;
            _picture.Resize(pixels.Width, pixels.Height);
            Layout();
        }
        var local = new Rect(zoomed.X - frame.X, zoomed.Y - frame.Y, zoomed.Width, zoomed.Height);
        if (local != _zoomed)
        {
            _zoomed = local;
            Outline();
        }
        _picture.Show(new CapturedView(effects, crop));
        _hovered = hovered;
        Fade();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _picture.Changed -= OnPictureChanged;
        _picture.Dispose();
        _parent.Children.Remove(_root);
        _lines.Dispose();
        foreach (SpriteVisual dim in _dim)
        {
            dim.Dispose();
        }
        _dimBrush.Dispose();
        _root.Dispose();
    }

    private static byte Alpha(double opacity) => (byte)Math.Round(opacity * 255);

    private PixelRect Pixels(Rect frame)
    {
        int left = (int)Math.Round(frame.X * _scale), top = (int)Math.Round(frame.Y * _scale);
        return PixelRect.FromSize(left, top, Math.Max(1, (int)Math.Round(frame.MaxX * _scale) - left),
                                  Math.Max(1, (int)Math.Round(frame.MaxY * _scale) - top));
    }

    /// <summary>The border's center line runs half its width inside the frame, so it is drawn inside, as a Core Animation border is.</summary>
    private void Layout()
    {
        var size = new Vector2(_frame.Width, _frame.Height);
        float radius = (float)(ZoomMap.CornerRadius * _scale);
        float border = (float)(ZoomMap.BorderWidth * _scale);
        _root.Offset = new Vector3(_frame.Left, _frame.Top, 0);
        _root.Size = size;
        _clip.Size = size;
        _clip.CornerRadius = new Vector2(radius);
        _lines.Size = size;
        _border.Offset = new Vector2(border / 2);
        _border.Size = Vector2.Max(Vector2.Zero, size - new Vector2(border));
        _border.CornerRadius = new Vector2(Math.Max(0, radius - border / 2));
        Outline();
    }

    /// <summary>Darkens the map around the zoomed part and outlines the part inside the map.</summary>
    private void Outline()
    {
        float width = _frame.Width, height = _frame.Height;
        float left = Math.Clamp((float)(_zoomed.X * _scale), 0, width), right = Math.Clamp((float)(_zoomed.MaxX * _scale), left, width);
        float top = Math.Clamp((float)(_zoomed.Y * _scale), 0, height), bottom = Math.Clamp((float)(_zoomed.MaxY * _scale), top, height);
        Place(_dim[0], 0, 0, width, top);
        Place(_dim[1], 0, bottom, width, height - bottom);
        Place(_dim[2], 0, top, left, bottom - top);
        Place(_dim[3], right, top, width - right, bottom - top);
        float stroke = (float)(ZoomMap.OutlineWidth * _scale);
        _outline.Offset = new Vector2(left + stroke / 2, top + stroke / 2);
        _outline.Size = new Vector2(Math.Max(0, right - left - stroke), Math.Max(0, bottom - top - stroke));
    }

    private static void Place(SpriteVisual visual, float x, float y, float width, float height)
    {
        visual.Offset = new Vector3(x, y, 0);
        visual.Size = new Vector2(Math.Max(0, width), Math.Max(0, height));
    }

    /// <summary>Mac updateMapOpacity: the map fades in, and to see-through while the pointer is over it.</summary>
    private void Fade()
    {
        if (!_revealed)
        {
            return;
        }
        float target = (float)(_hovered ? ZoomMap.HoverOpacity : 1);
        if (target == _opacity)
        {
            return;
        }
        _opacity = target;
        CubicBezier curve = MotionCurves.Fade;
        ScalarKeyFrameAnimation fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertExpressionKeyFrame(0, "this.StartingValue");
        fade.InsertKeyFrame(1, target, _compositor.CreateCubicBezierEasingFunction(new Vector2((float)curve.X1, (float)curve.Y1),
                                                                                    new Vector2((float)curve.X2, (float)curve.Y2)));
        fade.Duration = TimeSpan.FromSeconds(ZoomMap.FadeDuration);
        _root.StartAnimation("Opacity", fade);
    }

    private void OnPictureChanged(object? sender, EventArgs args)
    {
        if (_disposed)
        {
            return;
        }
        if (_picture.State == CapturedViewState.Failed)
        {
            Failed?.Invoke();
        }
        else if (_picture.Presented && !_revealed)
        {
            // The map's first picture is drawn: it fades in now.
            _revealed = true;
            _root.IsVisible = true;
            Fade();
        }
    }
}
