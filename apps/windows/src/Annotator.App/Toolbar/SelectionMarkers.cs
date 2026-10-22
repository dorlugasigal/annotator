using System.Numerics;
using Annotator.Workspace.Motion;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Annotator.App.Toolbar;

/// <summary>
/// Mac selectionMarker (ToolbarView): one highlight behind the current tool that slides from the tool it left to the chosen
/// tool (matchedGeometryEffect) and changes between the Draw look, an accent fill, and the Use apps look, a gray outline,
/// on the same spring. It lives in a layer between the toolbar's background and its controls, so no button clips it, and
/// moves to the new layer when the toolbar is rebuilt for the new tool's properties.
/// </summary>
internal sealed class SelectionMarkers
{
    private readonly UIElement _root;
    private readonly Grid _marker;
    private readonly Border _fill;
    private readonly Border _outline;
    private readonly Tween _fillShown;
    private readonly Tween _outlineShown;
    private Canvas? _layer;
    private FrameworkElement? _current;
    private FrameworkElement? _chosen;
    private double _radius = 10;
    private bool _drawing = true;
    /// <summary>Where the marker rests, in the toolbar's coordinates; null until its tool button is laid out.</summary>
    private Point? _origin;
    private Vector2 _slideFrom;
    private MotionSamples? _slide;
    private double _slideStarted;

    public SelectionMarkers(UIElement root)
    {
        _root = root;
        _fill = new Border { Style = AppResources.Style("AnnotatorSelectionDraw") };
        _outline = new Border { Style = AppResources.Style("AnnotatorSelectionApps") };
        _marker = new Grid { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        _marker.Children.Add(_fill);
        _marker.Children.Add(_outline);
        _fillShown = Tween.Opacity(_fill, 1);
        _outlineShown = Tween.Opacity(_outline, 0);
    }

    /// <summary>The marker's layer for a new toolbar surface, which goes between its background and its controls.</summary>
    public Canvas Layer()
    {
        // Parent is not reliable once the old surface left the window, so the marker remembers its layer.
        _layer?.Children.Remove(_marker);
        var layer = new Canvas { IsHitTestVisible = false };
        layer.Children.Add(_marker);
        _layer = layer;
        return layer;
    }

    /// <summary>One tool button's part of an update: <paramref name="anchor"/> is its content, which the marker covers.</summary>
    public void Update(FrameworkElement anchor, bool current, bool drawing, double radius)
    {
        if (current)
        {
            _chosen = anchor;
            _drawing = drawing;
            _radius = radius;
        }
    }

    /// <summary>
    /// After the tool buttons' updates: the marker takes its look and moves to the chosen tool, sliding from where it is on
    /// screen along <paramref name="motion"/>, or at once without it.
    /// </summary>
    public void Settle(MotionSamples? motion)
    {
        FrameworkElement? chosen = _chosen;
        _chosen = null;
        var radius = new CornerRadius(_radius);
        _fill.CornerRadius = radius;
        _outline.CornerRadius = radius;
        _fillShown.To(_drawing ? 1 : 0, motion);
        _outlineShown.To(_drawing ? 0 : 1, motion);
        if (ReferenceEquals(chosen, _current))
        {
            if (chosen is not null && LaidOut(chosen) && !Sliding)
            {
                // The same tool: keep the marker on its button if the layout moved it.
                Place(chosen);
            }
            return;
        }
        Point? from = OnScreen();
        _current = chosen;
        _origin = null;
        _slide = null;
        if (chosen is null)
        {
            _marker.Visibility = Visibility.Collapsed;
            return;
        }
        if (LaidOut(chosen))
        {
            Arrive(chosen, from, motion);
            return;
        }
        // A rebuilt toolbar lays out its new buttons first (they are loaded before that); the marker moves after layout,
        // before they are drawn.
        _marker.Visibility = Visibility.Collapsed;
        void SizeChanged(object sender, SizeChangedEventArgs args)
        {
            chosen.SizeChanged -= SizeChanged;
            if (ReferenceEquals(chosen, _current))
            {
                Arrive(chosen, from, motion);
            }
        }
        chosen.SizeChanged += SizeChanged;
    }

    private static bool LaidOut(FrameworkElement element) => element.ActualWidth > 0 && element.ActualHeight > 0;

    private bool Sliding => _slide is { } slide && ToolbarMotion.Now - _slideStarted < slide.Duration;

    /// <summary>The marker's place on screen now, partway through its slide if it is still sliding.</summary>
    private Point? OnScreen()
    {
        if (_origin is not { } origin)
        {
            return null;
        }
        if (Sliding)
        {
            var left = 1 - _slide!.At(ToolbarMotion.Now - _slideStarted);
            return new Point(origin.X + _slideFrom.X * left, origin.Y + _slideFrom.Y * left);
        }
        return origin;
    }

    private void Arrive(FrameworkElement anchor, Point? from, MotionSamples? motion)
    {
        if (Place(anchor) is not { } origin)
        {
            return;
        }
        _marker.Visibility = Visibility.Visible;
        if (from is not { } start || motion is null)
        {
            return;
        }
        var offset = new Vector2((float)(start.X - origin.X), (float)(start.Y - origin.Y));
        if (offset.LengthSquared() < 0.01f)
        {
            return;
        }
        _slideFrom = offset;
        _slide = motion;
        _slideStarted = ToolbarMotion.Now;
        ToolbarMotion.Vector(_marker, "Translation", motion, share => new Vector3(offset * (float)(1 - share), 0));
    }

    /// <summary>Puts the marker over <paramref name="anchor"/> in its layer; returns its place in the toolbar's coordinates.</summary>
    private Point? Place(FrameworkElement anchor)
    {
        if (_layer is not { } layer)
        {
            return null;
        }
        // Bounds, not the origin: a right-to-left rail's origin is its right edge.
        var size = new Rect(0, 0, anchor.ActualWidth, anchor.ActualHeight);
        Rect place = anchor.TransformToVisual(layer).TransformBounds(size);
        Canvas.SetLeft(_marker, place.X);
        Canvas.SetTop(_marker, place.Y);
        _marker.Width = place.Width;
        _marker.Height = place.Height;
        Rect onRoot = anchor.TransformToVisual(_root).TransformBounds(size);
        _origin = new Point(onRoot.X, onRoot.Y);
        return _origin;
    }
}
