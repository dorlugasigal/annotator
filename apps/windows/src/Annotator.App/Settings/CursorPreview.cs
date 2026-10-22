using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Annotator.App.Motion;
using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Platform;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using CorePoint = Annotator.Core.Point;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace Annotator.App.Settings;

/// <summary>
/// The Pointer page's interactive preview (Mac CursorHighlightPreview and CursorPreviewCanvas): a 200-point square with a
/// dark gradient and the halo as the settings draw it. The halo follows the pointer inside the square and sits in the
/// middle with an arrow when the pointer is outside. A click, a right-click, Space or Enter plays one click, and changing
/// the click effect plays one too. Moving and Clicks visibility apply as on the desktop. Nothing draws on the screen's
/// overlays. The halo is drawn by <see cref="CursorPreviewRenderer"/>, which exists only while the preview shows; nothing
/// runs while the pointer rests and no click plays.
/// </summary>
internal sealed partial class CursorPreview : Grid
{
    public const double Side = 200;
    private const double MovingVisibleSeconds = 0.75;

    private readonly Image _image = new() { Stretch = Stretch.Fill, IsHitTestVisible = false, Width = Side, Height = Side };
    private readonly Canvas _arrowLayer = new() { IsHitTestVisible = false, Width = Side, Height = Side };
    private readonly XamlPath _arrow;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private CursorHighlightSettings _settings;
    private CursorPreviewRenderer? _renderer;
    private WriteableBitmap? _bitmap;
    private CorePoint? _point;
    private CorePoint? _clickPoint;
    private double? _lastMove;
    private double? _lastClick;
    private double? _clickStarted;
    private bool _animated;
    private bool _ticking;
    private bool _pointerInside;
    private Exception? _failure;
    private XamlRoot? _root;

    public CursorPreview(CursorHighlightSettings settings)
    {
        _settings = settings;
        Width = Side;
        Height = Side;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        CornerRadius = new CornerRadius(16);
        // NSGradient(from (0.09, 0.14, 0.23) to (0.17, 0.24, 0.34)).draw(in: bounds, angle: -35) in a flipped view: dark at
        // the bottom left, light at the top right, spread over the square's extent along that direction.
        Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(-0.0704, 0.8998),
            EndPoint = new Windows.Foundation.Point(1.0704, 0.1002),
            GradientStops =
            {
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, 23, 36, 59), Offset = 0 },
                new GradientStop { Color = Windows.UI.Color.FromArgb(255, 43, 61, 87), Offset = 1 },
            },
        };
        BorderBrush = Pickers.PickerColors.Primary(0.08);
        BorderThickness = new Thickness(1);
        // A picture of the halo on the desktop: it keeps its colors in a contrast theme (WIN-UI-07).
        OwnColors.Mark(this);
        Children.Add(_image);
        // The arrow cursor, its tip on the point (Mac NSCursor.arrow at its hot spot), in the Windows arrow's colors.
        _arrow = new XamlPath
        {
            Data = ArrowGeometry(),
            Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.Black),
            StrokeThickness = 1,
            StrokeLineJoin = PenLineJoin.Round,
        };
        _arrowLayer.Children.Add(_arrow);
        Children.Add(_arrowLayer);
        AutomationProperties.SetName(this, "Preview click animation");
        AutomationProperties.SetHelpText(this, "Move here to try the highlight. Click, right-click or press Space to preview a click.");
        _timer = DispatcherQueue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Expire();
        PointerEntered += (_, args) =>
        {
            _pointerInside = true;
            Move(args);
        };
        PointerMoved += (_, args) => Move(args);
        PointerExited += (_, _) =>
        {
            _pointerInside = false;
            _point = null;
            Redraw();
        };
        PointerCanceled += (_, _) =>
        {
            _pointerInside = false;
            _point = null;
            Redraw();
        };
        PointerPressed += OnPressed;
        KeyDown += OnKeyDown;
        Loaded += (_, _) =>
        {
            ClipToCorners();
            // The window may show after the preview loads, and it may hide or move to another display later.
            if (XamlRoot is { } root && _root is null)
            {
                _root = root;
                root.Changed += OnRootChanged;
            }
            Redraw();
        };
        Unloaded += (_, _) =>
        {
            if (_root is { } root)
            {
                root.Changed -= OnRootChanged;
                _root = null;
            }
            Stop();
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility != Visibility.Visible)
            {
                Stop();
            }
        });
    }

    /// <summary>Times the halo was drawn (for the reference check).</summary>
    public long Draws { get; private set; }

    /// <summary>The error from the last drawing, or null; the page shows it.</summary>
    public Exception? Failure => _failure;

    public event Action<Exception>? Failed;

    /// <summary>
    /// New settings (Mac update(_:)): a new click effect plays one click while the preview shows, then the halo draws again.
    /// </summary>
    public void Update(CursorHighlightSettings settings)
    {
        bool changedEffect = _settings.ResolvedClickEffect != settings.ResolvedClickEffect;
        _settings = settings;
        if (changedEffect && Shows)
        {
            PreviewClick();
            return;
        }
        Redraw();
    }

    /// <summary>Mac previewClick(): one click at the pointer, or in the middle; animated unless the effect is Off or Reduce Motion is on.</summary>
    public void PreviewClick()
    {
        if (!Shows)
        {
            return;
        }
        double now = Now();
        StopClick();
        _lastClick = now;
        _clickPoint = _point ?? new CorePoint(Side / 2, Side / 2);
        _clickStarted = now;
        _animated = _settings.ResolvedClickEffect != CursorClickEffect.Off && !SystemMotion.ReducesMotion();
        if (_animated)
        {
            _ticking = true;
            CompositionTarget.Rendering += OnRendering;
        }
        Redraw();
        Schedule(now);
    }

    /// <summary>Stops the click and the timers and gives back the renderer (Mac stop(), viewDidHide); the last picture stays.</summary>
    public void Stop()
    {
        Stops++;
        StopClick();
        _timer.Stop();
        _renderer?.Dispose();
        _renderer = null;
    }

    /// <summary>Times the preview stopped (for the reference check).</summary>
    public long Stops { get; private set; }

    /// <summary>Whether the preview shows now (for the reference check).</summary>
    public bool IsShowing => Shows;

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private bool Shows => IsLoaded && Visibility == Visibility.Visible && XamlRoot is { IsHostVisible: true };

    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (Shows)
        {
            Redraw();
        }
        else
        {
            Stop();
        }
    }

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void Move(PointerRoutedEventArgs args)
    {
        Windows.Foundation.Point position = args.GetCurrentPoint(this).Position;
        _point = new CorePoint(position.X, position.Y);
        _lastMove = Now();
        Redraw();
        Schedule(_lastMove.Value);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs args)
    {
        PointerPointProperties properties = args.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed)
        {
            return;
        }
        Focus(FocusState.Pointer);
        Move(args);
        PreviewClick();
        args.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Space or VirtualKey.Enter)
        {
            PreviewClick();
            args.Handled = true;
        }
    }

    private void OnRendering(object? sender, object args)
    {
        if (!Shows)
        {
            StopClick();
            return;
        }
        if (CursorHighlightSettings.ClickProgress(Now(), _clickStarted) is null)
        {
            StopClick();
            _clickStarted = null;
        }
        Redraw();
    }

    private void StopClick()
    {
        if (_ticking)
        {
            _ticking = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    /// <summary>The one timer: the static click emphasis ends, or Moving visibility hides the halo after a rest.</summary>
    private void Schedule(double now)
    {
        _timer.Stop();
        double? next = null;
        if (!_animated && _clickStarted is { } started)
        {
            next = started + CursorHighlightSettings.ClickDuration;
        }
        if (_settings.Visibility == CursorHighlightVisibility.Moving && _lastMove is { } moved && moved + MovingVisibleSeconds > now)
        {
            next = Math.Max(next ?? 0, moved + MovingVisibleSeconds);
        }
        if (next is { } at)
        {
            _timer.Interval = TimeSpan.FromSeconds(Math.Max(0.001, at - now + 0.001));
            _timer.Start();
        }
    }

    private void Expire()
    {
        double now = Now();
        if (!_animated && CursorHighlightSettings.ClickProgress(now, _clickStarted) is null)
        {
            _clickStarted = null;
        }
        Redraw();
        Schedule(now);
    }

    /// <summary>Draws the halo as it shows now (Mac draw(_:)), or hides it; the arrow shows while the pointer is outside.</summary>
    private void Redraw()
    {
        CorePoint center = _point ?? new CorePoint(Side / 2, Side / 2);
        _arrowLayer.Visibility = _pointerInside ? Visibility.Collapsed : Visibility.Visible;
        Canvas.SetLeft(_arrow, center.X);
        Canvas.SetTop(_arrow, center.Y);
        if (!Shows)
        {
            return;
        }
        double now = Now();
        if (!_settings.IsVisible(now, _lastMove, _lastClick))
        {
            _image.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            // Sharp in Settings that grow with the text size (WIN-A11Y-03).
            double scale = ZoomPanel.ScaleOf(this);
            _renderer ??= new CursorPreviewRenderer();
            RasterImage image = _renderer.Render(_settings, Side, Side, scale, center, CursorHighlightSettings.ClickProgress(now, _clickStarted),
                _clickPoint, SystemMotion.ReducesMotion());
            Show(image);
            Draws++;
            _failure = null;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException or ArgumentException)
        {
            _renderer?.Dispose();
            _renderer = null;
            _image.Visibility = Visibility.Collapsed;
            if (_failure is null)
            {
                _failure = error;
                Failed?.Invoke(error);
            }
        }
    }

    private void Show(RasterImage image)
    {
        if (_bitmap is null || _bitmap.PixelWidth != image.Width || _bitmap.PixelHeight != image.Height)
        {
            _bitmap = new WriteableBitmap(image.Width, image.Height);
            _image.Source = _bitmap;
        }
        using (Stream stream = _bitmap.PixelBuffer.AsStream())
        {
            for (int row = 0; row < image.Height; row++)
            {
                stream.Write(image.Pixels.Slice(row * image.Stride, image.Width * 4));
            }
        }
        _bitmap.Invalidate();
        _image.Visibility = Visibility.Visible;
    }

    /// <summary>The arrow pointer's outline at 1x, its tip at the origin.</summary>
    private static PathGeometry ArrowGeometry()
    {
        var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(0, 0), IsClosed = true, IsFilled = true };
        foreach ((double x, double y) in new[] { (0.0, 16.0), (4.2, 12.1), (6.9, 18.4), (9.4, 17.3), (6.8, 11.2), (12.1, 11.2) })
        {
            figure.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(x, y) });
        }
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>Mac clipShape(RoundedRectangle(cornerRadius: 16)): the halo never draws past the rounded corners.</summary>
    private void ClipToCorners()
    {
        Microsoft.UI.Composition.Visual visual = ElementCompositionPreview.GetElementVisual(this);
        Microsoft.UI.Composition.CompositionRoundedRectangleGeometry geometry = visual.Compositor.CreateRoundedRectangleGeometry();
        geometry.Size = new System.Numerics.Vector2((float)Side, (float)Side);
        geometry.CornerRadius = new System.Numerics.Vector2(16, 16);
        visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
    }

    /// <summary>The Mac's accessibility element: a button named "Preview click animation" that plays a click when pressed.</summary>
    private sealed partial class Peer(CursorPreview owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        public void Invoke() => owner.PreviewClick();

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetClassNameCore() => "Button";

        protected override bool IsControlElementCore() => true;

        protected override bool IsKeyboardFocusableCore() => true;

        protected override IList<AutomationPeer> GetChildrenCore() => new AutomationPeerList();

        protected override object? GetPatternCore(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

        protected override Windows.Foundation.Rect GetBoundingRectangleCore() => AutomationFrames.Of(owner, base.GetBoundingRectangleCore());
    }
}
