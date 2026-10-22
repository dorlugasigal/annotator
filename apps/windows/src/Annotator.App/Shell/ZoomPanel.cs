using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Annotator.App.Shell;

/// <summary>
/// Shows a window's content <see cref="Zoom"/> times as large, in layout as well as on screen (WIN-A11Y-03): the content is
/// laid out at the Mac's sizes and drawn larger, so nothing in it outgrows its frame. Its text keeps the size it is laid out
/// at and grows with the zoom only (<see cref="FreezeText"/>), so the text size setting does not apply twice. At a zoom of 1
/// the content looks and measures as without the panel. In a window the person can resize, <see cref="Minimum"/> keeps the
/// content at least that large: a smaller window shows it less zoomed.
/// </summary>
internal sealed partial class ZoomPanel : Panel
{
    private readonly FrameworkElement _content;
    private double _zoom = 1;
    private double _shown = 1;

    public ZoomPanel(FrameworkElement content, double zoom)
    {
        _content = content;
        Children.Add(content);
        Zoom = zoom;
    }

    /// <summary>The zoom, 1 to 2.25 (<see cref="TextScaling.Zoom"/>).</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Max(1, value);
            Show(_zoom);
            InvalidateMeasure();
        }
    }

    /// <summary>The smallest size of the content in its own points, or empty for none.</summary>
    public Size Minimum { get; init; } = Size.Empty;

    /// <summary>The zoom the content shows at now: <see cref="Zoom"/>, or less in a window too small for it at that zoom.</summary>
    public double Shown => _shown;

    protected override Size MeasureOverride(Size availableSize)
    {
        double zoom = _zoom;
        if (!Minimum.IsEmpty && !double.IsInfinity(availableSize.Width) && !double.IsInfinity(availableSize.Height))
        {
            zoom = Math.Clamp(Math.Min(availableSize.Width / Minimum.Width, availableSize.Height / Minimum.Height), 1, _zoom);
        }
        Show(zoom);
        var room = new Size(availableSize.Width / zoom, availableSize.Height / zoom);
        // Text added since the last pass (a new page, a rebuilt view) and the templates this pass applies stop growing on
        // their own before they show.
        bool freezes = _zoom > 1;
        if (freezes)
        {
            FreezeText(_content);
        }
        _content.Measure(room);
        if (freezes && FreezeText(_content))
        {
            _content.Measure(room);
        }
        return new Size(_content.DesiredSize.Width * zoom, _content.DesiredSize.Height * zoom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _content.Arrange(new Rect(0, 0, finalSize.Width / _shown, finalSize.Height / _shown));
        return finalSize;
    }

    private void Show(double zoom)
    {
        if (zoom == _shown && _content.RenderTransform is CompositeTransform)
        {
            return;
        }
        _shown = zoom;
        // AutomationFrames.Nudge's 1/64-point offset stays after the zoom.
        _content.RenderTransform = new CompositeTransform
        {
            ScaleX = zoom,
            ScaleY = zoom,
            TranslateX = 1.0 / 64,
            TranslateY = 1.0 / 64,
        };
    }

    /// <summary>
    /// Physical pixels per point of <paramref name="element"/> as it shows: the display's scale times the zoom around it, so
    /// pictures drawn in pixels stay sharp in a zoomed window.
    /// </summary>
    public static double ScaleOf(FrameworkElement element)
    {
        double scale = element.XamlRoot?.RasterizationScale ?? 1;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ZoomPanel panel)
            {
                return scale * panel.Shown;
            }
        }
        return scale;
    }

    /// <summary>
    /// Shows <paramref name="root"/> in <paramref name="window"/>, in a zoom panel while the text size is above 100%
    /// (<paramref name="current"/> when it has one already). Returns the panel, or null while the text size is 100%.
    /// </summary>
    public static ZoomPanel? Host(Window window, FrameworkElement root, ZoomPanel? current, Size minimum)
    {
        double zoom = TextScaling.Zoom;
        if (current is not null)
        {
            current.Zoom = zoom;
            return current;
        }
        if (zoom <= 1)
        {
            if (!ReferenceEquals(window.Content, root))
            {
                window.Content = root;
            }
            return null;
        }
        window.Content = null;
        var panel = new ZoomPanel(root, zoom) { Minimum = minimum };
        window.Content = panel;
        return panel;
    }

    /// <summary>
    /// Turns off WinUI's own text scaling for the text in <paramref name="root"/> and its templates, which the zoom replaces;
    /// true when it changed something, so the caller measures again.
    /// </summary>
    public static bool FreezeText(DependencyObject root)
    {
        bool changed = false;
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            switch (current)
            {
                case TextBlock { IsTextScaleFactorEnabled: true } text:
                    text.IsTextScaleFactorEnabled = false;
                    changed = true;
                    break;
                case RichTextBlock { IsTextScaleFactorEnabled: true } rich:
                    rich.IsTextScaleFactorEnabled = false;
                    changed = true;
                    break;
                case Control { IsTextScaleFactorEnabled: true } control:
                    control.IsTextScaleFactorEnabled = false;
                    changed = true;
                    break;
                case ContentPresenter { IsTextScaleFactorEnabled: true } presenter:
                    presenter.IsTextScaleFactorEnabled = false;
                    changed = true;
                    break;
            }
            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int index = 0; index < count; index++)
            {
                pending.Push(VisualTreeHelper.GetChild(current, index));
            }
        }
        return changed;
    }
}
