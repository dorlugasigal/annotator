using System.Numerics;
using Annotator.App.Pickers;
using Annotator.App.Shell;
using Annotator.App.Toolbar;
using Annotator.Workspace.Motion;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinColor = Windows.UI.Color;

namespace Annotator.App.Settings;

/// <summary>
/// Colors of the Toolbar &amp; Shortcuts page from the Mac source and design-values.json, for the app's current theme. In a
/// Windows contrast theme each takes the theme's color for its role instead (WIN-UI-07): status colors become the window
/// text color, and shadows go.
/// </summary>
internal static class SettingsColors
{
    /// <summary>SwiftUI's Color.primary (labelColor) at <paramref name="opacity"/>.</summary>
    public static SolidColorBrush Primary(double opacity) => PickerColors.Primary(opacity);

    /// <summary>The window background (windowBackgroundColor: #FFFFFF light, #1E1E1E dark).</summary>
    public static Brush Window => AppResources.Brush("AnnotatorWindowBackgroundBrush");

    /// <summary>controlBackgroundColor: #FFFFFF light, #1E1E1E dark.</summary>
    public static SolidColorBrush ControlBackground => ContrastTheme.Pick(ContrastColor.Window,
        new SolidColorBrush(PickerColors.Dark ? WinColor.FromArgb(255, 0x1E, 0x1E, 0x1E) : WinColor.FromArgb(255, 0xFF, 0xFF, 0xFF)));

    public static WinColor Accent => Library.LibraryColors.Accent;

    /// <summary>The accent at <paramref name="opacity"/>; the whole Highlight color in a contrast theme.</summary>
    public static SolidColorBrush AccentBrush(double opacity = 1) => new(Accent) { Opacity = ContrastTheme.IsOn ? 1 : opacity };

    /// <summary>systemGreen, systemOrange and systemRed of design-values.json; the window text color in a contrast theme.</summary>
    public static WinColor Green => ContrastTheme.Pick(ContrastColor.WindowText,
        PickerColors.Dark ? WinColor.FromArgb(255, 0x30, 0xD1, 0x58) : WinColor.FromArgb(255, 0x34, 0xC7, 0x59));

    public static WinColor Orange => ContrastTheme.Pick(ContrastColor.WindowText,
        PickerColors.Dark ? WinColor.FromArgb(255, 0xFF, 0x92, 0x30) : WinColor.FromArgb(255, 0xFF, 0x8D, 0x28));

    public static WinColor Red => ContrastTheme.Pick(ContrastColor.WindowText,
        PickerColors.Dark ? WinColor.FromArgb(255, 0xFF, 0x42, 0x45) : WinColor.FromArgb(255, 0xFF, 0x38, 0x3C));

    /// <summary>A brush of <paramref name="color"/>; whole in a contrast theme, which has no translucent colors.</summary>
    public static SolidColorBrush Brush(WinColor color, double opacity = 1) => new(color) { Opacity = ContrastTheme.IsOn ? 1 : opacity };

    /// <summary>A shadow of black at <paramref name="alpha"/>; none in a contrast theme.</summary>
    public static SolidColorBrush Black(double alpha) => new(WinColor.FromArgb(ContrastTheme.IsOn ? (byte)0 : (byte)Math.Round(alpha * 255), 0, 0, 0));

    /// <summary>Paints a glyph in one color, such as a symbol in systemGreen, systemOrange or the accent.</summary>
    public static void Tint(FrameworkElement glyph, WinColor color)
    {
        color = ContrastTheme.Pick(ContrastColor.WindowText, color);
        if (glyph is Viewbox { Child: Canvas canvas })
        {
            foreach (UIElement child in canvas.Children)
            {
                if (child is Microsoft.UI.Xaml.Shapes.Path path)
                {
                    path.Stroke = new SolidColorBrush(color);
                    path.Fill = path.Fill is null ? null : new SolidColorBrush(color);
                }
            }
        }
    }
}

/// <summary>
/// The page's filled status symbols, drawn as the Mac's SF Symbols are (Lucide has outlines only): a white check, minus,
/// plus or info mark on a filled circle, and a white exclamation mark on a filled triangle. Other symbols come from
/// <see cref="Icons.Glyphs"/>, tinted.
/// </summary>
internal static class SettingsSymbols
{
    public static FrameworkElement Filled(string symbol, double size, WinColor color)
    {
        // A contrast theme draws the symbol in the window text color with marks in the window color.
        SolidColorBrush white = ContrastTheme.Pick(ContrastColor.Window, new SolidColorBrush(Microsoft.UI.Colors.White));
        var fill = new SolidColorBrush(ContrastTheme.Pick(ContrastColor.WindowText, color));
        var surface = new Grid { Width = size, Height = size, IsHitTestVisible = false };
        // Marks in a 15-point box, scaled to the symbol's size.
        double unit = size / 15;
        Microsoft.UI.Xaml.Shapes.Polyline Mark(params double[] points)
        {
            var line = new Microsoft.UI.Xaml.Shapes.Polyline
            {
                Stroke = white,
                StrokeThickness = 1.6 * unit,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            for (int index = 0; index + 1 < points.Length; index += 2)
            {
                line.Points.Add(new Point(points[index] * unit, points[index + 1] * unit));
            }
            return line;
        }
        switch (symbol)
        {
            case "checkmark.circle.fill":
                surface.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = fill });
                surface.Children.Add(Mark(4.4, 7.7, 6.6, 9.9, 10.7, 5.3));
                return surface;
            case "minus.circle.fill":
            case "plus.circle.fill":
                surface.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = fill });
                surface.Children.Add(Mark(4.5, 7.5, 10.5, 7.5));
                if (symbol == "plus.circle.fill")
                {
                    surface.Children.Add(Mark(7.5, 4.5, 7.5, 10.5));
                }
                return surface;
            case "info.circle.fill":
                surface.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = fill });
                surface.Children.Add(Mark(7.5, 6.9, 7.5, 10.6));
                surface.Children.Add(Mark(7.5, 4.6, 7.5, 4.65));
                return surface;
            case "exclamationmark.triangle.fill":
                var triangle = new Microsoft.UI.Xaml.Shapes.Polygon { Fill = fill, Stroke = fill, StrokeThickness = 1.5 * unit, StrokeLineJoin = PenLineJoin.Round };
                foreach ((double x, double y) in new[] { (7.5, 1.6), (14.1, 13.2), (0.9, 13.2) })
                {
                    triangle.Points.Add(new Point(x * unit, y * unit));
                }
                surface.Children.Add(triangle);
                surface.Children.Add(Mark(7.5, 5.6, 7.5, 9.0));
                surface.Children.Add(Mark(7.5, 11.2, 7.5, 11.25));
                return surface;
            default:
                FrameworkElement glyph = Icons.Glyphs.ForSymbol(symbol, size, "AnnotatorGlyph");
                SettingsColors.Tint(glyph, color);
                return glyph;
        }
    }
}

/// <summary>
/// Text at the Mac's sizes and line heights (SF Pro lines, from the UX reference frames). With Windows text size above 100%
/// the lines grow with the text instead, so larger text never clips (WIN-A11Y-03).
/// </summary>
internal static class SettingsText
{
    /// <summary>True when the person chose a Windows text size above 100%.</summary>
    public static bool Scaled => Shell.TextScaling.Scaled;

    /// <summary>The Mac's one-line height for a system font size: 13 at 10 points, 19 at 15 points (title3).</summary>
    public static double LineHeight(double size) => size switch
    {
        <= 13 => ToolbarParts.LineHeight(size),
        <= 15 => 19,
        _ => Math.Ceiling(size * 1.25),
    };

    /// <summary>
    /// One line that UI Automation reads as static text, exactly one Mac line high, so stacks keep the Mac's positions (text
    /// otherwise measures up to whole pixels and the rows below drift).
    /// </summary>
    public static TextBlock Static(string text, double size, string style = "AnnotatorPrimaryText", Windows.UI.Text.FontWeight? weight = null, double macWidth = 0)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            Style = AppResources.Style(style),
            TextWrapping = TextWrapping.NoWrap,
            // SwiftUI gives a line of text its own width, so UI Automation reads the words' frame, not the row's.
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = macWidth,
        };
        if (!Scaled)
        {
            block.LineHeight = LineHeight(size);
            block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            block.Height = LineHeight(size);
        }
        return block;
    }

    /// <summary>Text that wraps at the available width, with the Mac's line height.</summary>
    public static TextBlock Wrapping(string text, double size, string style = "AnnotatorSecondaryText", Windows.UI.Text.FontWeight? weight = null)
    {
        TextBlock block = Static(text, size, style, weight);
        block.TextWrapping = TextWrapping.Wrap;
        block.Height = double.NaN;
        block.HorizontalAlignment = HorizontalAlignment.Stretch;
        block.VerticalAlignment = VerticalAlignment.Top;
        return block;
    }

    /// <summary>Text that belongs to a control's look; UI Automation reads the control instead.</summary>
    public static TextBlock Label(string text, double size, string style = "AnnotatorPrimaryText", Windows.UI.Text.FontWeight? weight = null)
    {
        TextBlock block = Static(text, size, style, weight);
        block.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(block, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return block;
    }

    /// <summary>The width of a line of text in Segoe UI at <paramref name="size"/>, in points.</summary>
    public static double Width(string text, double size, Windows.UI.Text.FontWeight? weight = null)
    {
        TextBlock probe = Static(text, size, "AnnotatorPrimaryText", weight);
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    /// <summary>
    /// The width SF Pro gives the page's fixed words where a control after them is placed by it (reference/ux-reference/macos/
    /// settings/tab-toolbar), so those controls keep the Mac's positions with Segoe UI (D-4).
    /// </summary>
    public static double MacWidth(string text) => text switch
    {
        "More tools" => 68.5,
        "Direction" => 55,
        "Start at" => 45,
        "Properties" => 63,
        _ => 0,
    };
}

/// <summary>
/// SwiftUI's FlowLayout of ToolbarShortcutSettings.swift: children left to right, <see cref="Spacing"/> apart, wrapping to a
/// new row when the next would pass the available width; each row is as tall as its tallest child, children centered in
/// it, rows <see cref="LineSpacing"/> apart. Exact points, rounded to pixels once (<see cref="MacStack"/>).
/// </summary>
internal sealed partial class FlowPanel : Panel, IExactSize
{
    private readonly List<Rect> _frames = [];

    public double Spacing { get; init; } = 4;

    public double LineSpacing { get; init; } = 6;

    /// <summary>The exact size of the last measure.</summary>
    public Size? ExactSize { get; private set; }

    /// <summary>Where each visible child was placed, in this panel's coordinates.</summary>
    public IReadOnlyList<Rect> Frames => _frames;

    protected override Size MeasureOverride(Size availableSize)
    {
        _frames.Clear();
        double limit = double.IsFinite(availableSize.Width) ? availableSize.Width : double.PositiveInfinity;
        var sizes = new List<(double Width, double Height)>();
        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            sizes.Add(MacStack.Extent(child, horizontal: true));
        }
        double y = 0, widest = 0;
        int start = 0;
        while (start < sizes.Count)
        {
            int end = start;
            double width = sizes[start].Width, height = sizes[start].Height;
            while (end + 1 < sizes.Count && width + Spacing + sizes[end + 1].Width <= limit)
            {
                end++;
                width += Spacing + sizes[end].Width;
                height = Math.Max(height, sizes[end].Height);
            }
            double x = 0;
            for (int index = start; index <= end; index++)
            {
                _frames.Add(new Rect(x, y + (height - sizes[index].Height) / 2, sizes[index].Width, sizes[index].Height));
                x += sizes[index].Width + Spacing;
            }
            widest = Math.Max(widest, width);
            y += height + LineSpacing;
            start = end + 1;
        }
        var size = new Size(widest, sizes.Count == 0 ? 0 : y - LineSpacing);
        ExactSize = size;
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        Point residual = MacStack.InheritedResidual(this);
        int index = 0;
        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed || index >= _frames.Count)
            {
                continue;
            }
            Rect frame = _frames[index++];
            Thickness margin = child is FrameworkElement element ? element.Margin : default;
            MacStack.ArrangeExact(child, frame.X + residual.X, frame.Y + residual.Y, frame.Width, frame.Height, margin, scale);
        }
        return finalSize;
    }
}

/// <summary>
/// SwiftUI's Grid of equal columns as the page's "Buttons that are always there" uses it: two columns
/// <see cref="Spacing"/> apart filling the width, each row as tall as its tallest cell, rows <see cref="Spacing"/> apart.
/// </summary>
internal sealed partial class ColumnsPanel : Panel, IExactSize
{
    private double _width;
    private readonly List<double> _rows = [];

    public int Columns { get; init; } = 2;

    public double Spacing { get; init; } = 10;

    public Size? ExactSize { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        _width = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
        double cell = Math.Max(0, (_width - Spacing * (Columns - 1)) / Columns);
        _rows.Clear();
        for (int index = 0; index < Children.Count; index++)
        {
            Children[index].Measure(new Size(cell, double.PositiveInfinity));
            double height = MacStack.Extent(Children[index], horizontal: false).Along;
            if (index % Columns == 0)
            {
                _rows.Add(height);
            }
            else
            {
                _rows[^1] = Math.Max(_rows[^1], height);
            }
        }
        var size = new Size(_width, _rows.Sum() + Math.Max(0, _rows.Count - 1) * Spacing);
        ExactSize = size;
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        Point residual = MacStack.InheritedResidual(this);
        // The columns share the width the panel gets, which can be a pixel wider than the width it was measured with.
        double cell = Math.Max(0, (Math.Max(_width, finalSize.Width) - Spacing * (Columns - 1)) / Columns);
        double y = 0;
        for (int row = 0; row < _rows.Count; row++)
        {
            for (int column = 0; column < Columns && row * Columns + column < Children.Count; column++)
            {
                UIElement child = Children[row * Columns + column];
                Thickness margin = child is FrameworkElement element ? element.Margin : default;
                MacStack.ArrangeExact(child, column * (cell + Spacing) + residual.X, y + residual.Y, cell, _rows[row], margin, scale);
            }
            y += _rows[row] + Spacing;
        }
        return finalSize;
    }
}

/// <summary>
/// Wrapped text whose height is exactly its lines times the Mac's line height. A wrapped line measures up to whole pixels
/// (26.4 points for two 13-point lines at 125%), so each caption made its row a little taller and the rows below drifted;
/// this gives <see cref="MacStack"/> the exact height, and it rounds positions once. With Windows text size above 100% it
/// keeps the text's own height.
/// </summary>
internal sealed partial class ExactLines : Panel, IExactSize
{
    private readonly FrameworkElement _text;
    private readonly double _line;

    public ExactLines(FrameworkElement text, double lineHeight)
    {
        _text = text;
        _line = lineHeight;
        Children.Add(text);
    }

    public Size? ExactSize { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        _text.Measure(availableSize);
        Size desired = _text.DesiredSize;
        ExactSize = SettingsText.Scaled ? null : new Size(desired.Width, Math.Max(1, Math.Round(desired.Height / _line)) * _line);
        return ExactSize ?? desired;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _text.Arrange(new Rect(0, 0, finalSize.Width, Math.Max(finalSize.Height, _text.DesiredSize.Height)));
        return finalSize;
    }
}

/// <summary>
/// Plays the page's timelines (<see cref="ShortcutEditorMotion"/>) on elements with composition keyframes, as the toolbar's
/// <see cref="ToolbarMotion"/> does: the compositor runs them, and nothing runs once they end, except the listening pulse,
/// which repeats only while a key records.
/// </summary>
internal static class PageMotion
{
    public static bool ReducesMotion => ToolbarMotion.ReducesMotion();

    /// <summary>Scales an element about its center along <paramref name="samples"/>.</summary>
    public static void Scale(FrameworkElement element, MotionSamples samples, Func<double, double> value)
    {
        element.CenterPoint = new Vector3((float)(element.ActualWidth / 2), (float)(element.ActualHeight / 2), 0);
        ToolbarMotion.Vector(element, "Scale", samples, sample => new Vector3((float)value(sample), (float)value(sample), 1));
        double last = value(samples.Values[^1]);
        element.Scale = new Vector3((float)last, (float)last, 1);
    }

    /// <summary>Moves an element along <paramref name="samples"/>: each value is a share of the way from an offset to its place.</summary>
    public static void Slide(UIElement element, Vector3 from, MotionSamples samples)
    {
        ToolbarMotion.Vector(element, "Translation", samples, share => from * (float)(1 - share));
        element.Translation = Vector3.Zero;
    }

    /// <summary>Shakes an element sideways by the shake's offsets.</summary>
    public static void Shake(UIElement element, MotionSamples samples)
    {
        ToolbarMotion.Vector(element, "Translation", samples, offset => new Vector3((float)offset, 0, 0));
        element.Translation = Vector3.Zero;
    }

    /// <summary>Fades an element's opacity from <paramref name="from"/> to <paramref name="to"/> along progress samples.</summary>
    public static void Fade(UIElement element, double from, double to, MotionSamples progress)
    {
        ToolbarMotion.Scalar(element, "Opacity", progress, share => (float)(from + (to - from) * share));
        element.Opacity = to;
    }

    /// <summary>
    /// The listening ring (Mac PulseRing): an accent outline of <paramref name="target"/>, 1.5 points wide, grows to 1.45
    /// times its size while it fades from 70% to nothing, every 1.1 s with ease-out, until the returned handle stops it. It
    /// is a composition shape on <paramref name="host"/>, the key's button, so only the compositor runs it, and the button's
    /// rounded clip does not cut it off as it grows past the key (SwiftUI's overlay is not clipped).
    /// </summary>
    public static IDisposable Pulse(UIElement host, FrameworkElement target, float cornerRadius) =>
        new Pulsing(host, target, cornerRadius);

    private sealed class Pulsing : IDisposable
    {
        private const float Width = 1.5f;
        private readonly UIElement _host;
        private readonly FrameworkElement _target;
        private readonly ShapeVisual _ring;
        private readonly CompositionRoundedRectangleGeometry _geometry;
        private readonly CompositionObject[] _parts;
        private bool _stopped;

        public Pulsing(UIElement host, FrameworkElement target, float cornerRadius)
        {
            _host = host;
            _target = target;
            Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
            _geometry = compositor.CreateRoundedRectangleGeometry();
            // Like the key's own outline, the stroke lies inside the key's edge.
            _geometry.CornerRadius = new Vector2(Math.Max(0, cornerRadius - Width / 2));
            CompositionColorBrush brush = compositor.CreateColorBrush(SettingsColors.Accent);
            CompositionSpriteShape stroke = compositor.CreateSpriteShape(_geometry);
            stroke.StrokeBrush = brush;
            stroke.StrokeThickness = Width;
            _ring = compositor.CreateShapeVisual();
            _ring.Shapes.Add(stroke);
            _ring.Opacity = 0;
            MotionSamples samples = ShortcutEditorMotion.Pulse;
            LinearEasingFunction linear = compositor.CreateLinearEasingFunction();
            Vector3KeyFrameAnimation scale = compositor.CreateVector3KeyFrameAnimation();
            ScalarKeyFrameAnimation opacity = compositor.CreateScalarKeyFrameAnimation();
            for (int index = 0; index < samples.Times.Count; index++)
            {
                float key = (float)(samples.Times[index] / samples.Duration);
                float grown = (float)(1 + (ShortcutEditorMotion.PulseScale - 1) * samples.Values[index]);
                scale.InsertKeyFrame(key, new Vector3(grown, grown, 1), linear);
                opacity.InsertKeyFrame(key, (float)(ShortcutEditorMotion.PulseOpacity * (1 - samples.Values[index])), linear);
            }
            foreach (KeyFrameAnimation animation in new KeyFrameAnimation[] { scale, opacity })
            {
                animation.Duration = TimeSpan.FromSeconds(samples.Duration);
                animation.IterationBehavior = AnimationIterationBehavior.Forever;
            }
            _parts = [_ring, stroke, brush, _geometry, linear, scale, opacity];
            Place();
            _target.SizeChanged += OnSizeChanged;
            ElementCompositionPreview.SetElementChildVisual(_host, _ring);
            _ring.StartAnimation("Scale", scale);
            _ring.StartAnimation("Opacity", opacity);
        }

        public void Dispose()
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;
            _target.SizeChanged -= OnSizeChanged;
            _ring.StopAnimation("Scale");
            _ring.StopAnimation("Opacity");
            ElementCompositionPreview.SetElementChildVisual(_host, null);
            foreach (CompositionObject part in _parts)
            {
                part.Dispose();
            }
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs args) => Place();

        /// <summary>Puts the ring on the key: its label changes the key's width when listening starts.</summary>
        private void Place()
        {
            if (!_target.IsLoaded)
            {
                return;
            }
            Vector2 size = _target.ActualSize;
            Point origin = _target.TransformToVisual(_host).TransformPoint(new Point(0, 0));
            _ring.Size = size;
            _ring.Offset = new Vector3((float)origin.X, (float)origin.Y, 0);
            _ring.CenterPoint = new Vector3(size / 2, 0);
            _geometry.Offset = new Vector2(Width / 2);
            _geometry.Size = Vector2.Max(Vector2.Zero, size - new Vector2(Width));
        }
    }
}
