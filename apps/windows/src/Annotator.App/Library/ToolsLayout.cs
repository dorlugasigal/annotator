using Annotator.App.Shell;
using Annotator.Workspace.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Annotator.App.Library;

/// <summary>
/// ToolLibraryPanel's VStack(spacing: 12) with 16 points of padding, in exact points: the "Tools" headline and Paste image,
/// the segmented picker, the section's scroll area (as tall as ToolLibrary.gridHeight, or the whole viewport for My
/// shapes), the Focus section's Spotlight size row, a spacer, the divider and the footer menus. In a window it fills the
/// window, so the footer sits at the bottom (the Mac's GeometryReader); with <see cref="MaximumHeight"/> it takes its ideal
/// height for that maximum, as the Mac capture renders ToolLibraryPanel.
/// </summary>
internal sealed partial class ToolsLayout : Panel
{
    private const double PasteWidth = 108.5;
    private const double FooterWidth = 76 + 8 + 41 + 8 + 87;
    private ToolLibrarySection _section = ToolLibrarySection.Shapes;

    public required FrameworkElement Title { get; init; }

    public required FrameworkElement Paste { get; init; }

    public required FrameworkElement Picker { get; init; }

    public required ScrollViewer Tools { get; init; }

    public required TileGrid Grid { get; init; }

    public required ScrollViewer Shapes { get; init; }

    public required FrameworkElement Spotlight { get; init; }

    public required FrameworkElement Divider { get; init; }

    public required FrameworkElement Footer { get; init; }

    /// <summary>The panel's maximumHeight for an ideal-height panel (reference captures); null fills the available height.</summary>
    public double? MaximumHeight { get; init; }

    /// <summary>The width when the panel is measured without one (reference captures).</summary>
    public double PreferredWidth { get; init; } = ToolLibraryLayout.DefaultWidth;

    public ToolLibrarySection Section
    {
        get => _section;
        set
        {
            _section = value;
            Tools.Visibility = value == ToolLibrarySection.MyShapes ? Visibility.Collapsed : Visibility.Visible;
            Shapes.Visibility = value == ToolLibrarySection.MyShapes ? Visibility.Visible : Visibility.Collapsed;
            Spotlight.Visibility = value == ToolLibrarySection.Focus ? Visibility.Visible : Visibility.Collapsed;
            InvalidateMeasure();
        }
    }

    public void Attach()
    {
        foreach (FrameworkElement part in new[] { Title, Paste, Picker, Tools, Shapes, Spotlight, Divider, Footer })
        {
            Children.Add(part);
        }
        Section = _section;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? PreferredWidth : availableSize.Width;
        double maximum = MaximumHeight ?? (double.IsInfinity(availableSize.Height) ? ToolLibraryLayout.DefaultMaximumHeight : availableSize.Height);
        Frames frames = Plan(width, maximum, height: null);
        Title.Measure(new Size(double.PositiveInfinity, 16));
        Paste.Measure(frames.Paste.Size());
        Picker.Measure(frames.Picker.Size());
        Tools.Measure(frames.Viewport.Size());
        Shapes.Measure(frames.Viewport.Size());
        Spotlight.Measure(frames.Spotlight.Size());
        Divider.Measure(frames.Divider.Size());
        Footer.Measure(frames.Footer.Size());
        double height = MaximumHeight is null && !double.IsInfinity(availableSize.Height) ? availableSize.Height : frames.IdealHeight;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Frames frames = Plan(finalSize.Width, MaximumHeight ?? finalSize.Height, MaximumHeight is null ? finalSize.Height : null);
        double scale = XamlRoot?.RasterizationScale ?? 1;
        Place(Title, new Rect(ToolLibraryLayout.Padding, 18, Title.DesiredSize.Width, 16), scale);
        Place(Paste, frames.Paste, scale);
        Place(Picker, frames.Picker, scale);
        Place(Tools, frames.Viewport, scale);
        Place(Shapes, frames.Viewport, scale);
        Place(Spotlight, frames.Spotlight, scale);
        Place(Divider, frames.Divider, scale);
        Place(Footer, frames.Footer, scale);
        return finalSize;
    }

    private static void Place(FrameworkElement element, Rect frame, double scale) =>
        MacStack.ArrangeExact(element, frame.X, frame.Y, frame.Width, frame.Height, default, scale);

    /// <summary>
    /// The frames for a panel <paramref name="width"/> wide with <paramref name="maximum"/> as its maximumHeight; the footer
    /// sits at the bottom of <paramref name="height"/>, or right after the content for an ideal-height panel.
    /// </summary>
    private Frames Plan(double width, double maximum, double? height)
    {
        double padding = ToolLibraryLayout.Padding;
        double viewportWidth = ToolLibraryLayout.ViewportWidth(width);
        double viewportHeight = ToolLibraryLayout.ViewportHeight(maximum, _section);
        double shown = _section == ToolLibrarySection.MyShapes ? viewportHeight : ToolLibraryLayout.GridHeight(_section, viewportWidth, viewportHeight);
        Grid.SetViewport(viewportWidth, shown);
        var segments = ToolLibraryLayout.Segments(viewportWidth);
        double pickerTop = padding + ToolLibraryLayout.HeaderHeight + ToolLibraryLayout.Spacing;
        double viewportTop = pickerTop + ToolLibraryLayout.PickerHeight + ToolLibraryLayout.Spacing;
        double ideal = ToolLibraryLayout.IdealHeight(_section, shown);
        double bottom = Math.Max(height ?? ideal, ideal);
        double footerTop = bottom - padding - ToolLibraryLayout.FooterHeight;
        return new Frames(
            new Rect(padding + viewportWidth - PasteWidth, padding, PasteWidth, ToolLibraryLayout.HeaderHeight),
            new Rect(segments[0].X, pickerTop, segments[^1].X + segments[^1].Width - segments[0].X, ToolLibraryLayout.PickerHeight),
            new Rect(padding, viewportTop, viewportWidth, shown),
            new Rect(padding, viewportTop + shown + ToolLibraryLayout.Spacing, viewportWidth, ToolLibraryLayout.SpotlightRowHeight),
            new Rect(padding, footerTop - ToolLibraryLayout.Spacing - 1, viewportWidth, 1),
            new Rect(padding + (viewportWidth - FooterWidth) / 2, footerTop, FooterWidth, ToolLibraryLayout.FooterHeight),
            ideal);
    }

    private readonly record struct Frames(Rect Paste, Rect Picker, Rect Viewport, Rect Spotlight, Rect Divider, Rect Footer, double IdealHeight);
}

internal static class RectSizes
{
    public static Size Size(this Rect rect) => new(rect.Width, rect.Height);
}
