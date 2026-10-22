using System.Globalization;
using System.Text.Json;
using Annotator.App.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Reference;

/// <summary>
/// Development only (UX reference states, <c>--accessibility</c>): what the accessibility checks of Annotator.App.Tests need
/// from inside the window (WIN-A11Y-01 to WIN-A11Y-03). The keyboard path is walked with WinUI's own Tab navigation in the
/// window, which is never active, so no key reaches another app. It lists:
/// <list type="bullet">
/// <item>the tab order, element by element;</item>
/// <item>controls that a person can use but the keyboard cannot reach (neither Tab nor the arrow keys of a group);</item>
/// <item>context menus and their items, where the Mac's VoiceOver actions live on Windows;</item>
/// <item>text cut off by its own box, its parents or the window, for larger Windows text;</item>
/// <item>with a contrast theme, colors that are not the theme's, outside parts that keep their own colors.</item>
/// </list>
/// </summary>
internal static class AccessibilityReport
{
    private const double Tolerance = 0.5;

    public static void Write(Utf8JsonWriter writer, FrameworkElement surface, ReferenceRequest request)
    {
        writer.WriteStartObject("accessibility");
        writer.WriteString("contrast", request.Contrast?.Name);
        writer.WriteNumber("textScale", request.TextScale);
        List<DependencyObject> order = TabOrder(surface);
        writer.WriteStartArray("tabOrder");
        foreach (DependencyObject element in order)
        {
            WriteElement(writer, element, surface, null);
        }
        writer.WriteEndArray();
        writer.WriteStartArray("unreachable");
        foreach (UIElement element in Interactive(surface))
        {
            if (!Reachable(element, order))
            {
                WriteElement(writer, element, surface, null);
            }
        }
        writer.WriteEndArray();
        writer.WriteStartArray("contextMenus");
        foreach (UIElement element in Descendants(surface).OfType<UIElement>())
        {
            if (element.ContextFlyout is MenuFlyout menu && Shown(element))
            {
                writer.WriteStartObject();
                WriteIdentity(writer, element);
                writer.WriteStartArray("items");
                foreach (MenuFlyoutItemBase item in menu.Items)
                {
                    if (item is MenuFlyoutItem entry)
                    {
                        writer.WriteStringValue(entry.Text);
                    }
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();
        writer.WriteStartArray("clipped");
        foreach (TextBlock block in Descendants(surface).OfType<TextBlock>())
        {
            if (Shown(block) && block.Text.Length > 0 && Clipping(block, surface) is { } reason)
            {
                WriteElement(writer, block, surface, reason);
            }
        }
        writer.WriteEndArray();
        writer.WriteStartArray("ownColors");
        foreach (FrameworkElement element in Descendants(surface).OfType<FrameworkElement>())
        {
            if (OwnColors.IsMarked(element) && Shown(element))
            {
                WriteFrame(writer, null, Frame(element, surface));
            }
        }
        writer.WriteEndArray();
        if (request.Contrast is { } palette)
        {
            writer.WriteStartArray("offPalette");
            var colors = new HashSet<uint>(palette.Colors.Select(color => color & 0xFFFFFF));
            WriteOffPalette(writer, surface, surface, colors);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// The report of a window without XAML content (the shared region's output, drawn with Direct2D): it has no controls, so
    /// nothing to reach, and its picture shows shared screen content, which a contrast theme does not recolor.
    /// </summary>
    public static void WriteNative(Utf8JsonWriter writer, ReferenceRequest request, double width, double height)
    {
        writer.WriteStartObject("accessibility");
        writer.WriteString("contrast", request.Contrast?.Name);
        writer.WriteNumber("textScale", request.TextScale);
        foreach (string empty in new[] { "tabOrder", "unreachable", "contextMenus", "clipped", "offPalette" })
        {
            writer.WriteStartArray(empty);
            writer.WriteEndArray();
        }
        writer.WriteStartArray("ownColors");
        WriteFrame(writer, null, new WinRect(0, 0, width, height));
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>The window's tab order from its first focusable element, as Tab presses move focus in it.</summary>
    private static List<DependencyObject> TabOrder(FrameworkElement surface)
    {
        var order = new List<DependencyObject>();
        if (surface.XamlRoot is not { Content: { } content } root || FocusManager.FindFirstFocusableElement(content) is not UIElement first)
        {
            return order;
        }
        if (!first.Focus(FocusState.Keyboard))
        {
            return order;
        }
        var options = new FindNextElementOptions { SearchRoot = content };
        for (int step = 0; step < 400; step++)
        {
            if (FocusManager.GetFocusedElement(root) is not DependencyObject focused || order.Contains(focused))
            {
                break;
            }
            order.Add(focused);
            if (!FocusManager.TryMoveFocus(FocusNavigationDirection.Next, options))
            {
                break;
            }
        }
        return order;
    }

    /// <summary>Shown elements that a person can use: buttons, toggles, fields, sliders, pop-ups and selectable items.</summary>
    private static IEnumerable<UIElement> Interactive(FrameworkElement surface)
    {
        foreach (UIElement element in Descendants(surface).OfType<UIElement>())
        {
            if (!Shown(element) || element is ScrollBar or Thumb or RepeatButton || Inside<ScrollBar>(element)
                || AutomationProperties.GetAccessibilityView(element) == AccessibilityView.Raw)
            {
                continue;
            }
            if (FrameworkElementAutomationPeer.CreatePeerForElement(element) is not { } peer || !peer.IsEnabled() || peer.IsOffscreen())
            {
                continue;
            }
            bool usable = peer.GetPattern(PatternInterface.Invoke) is not null || peer.GetPattern(PatternInterface.Toggle) is not null
                || peer.GetPattern(PatternInterface.ExpandCollapse) is not null || peer.GetPattern(PatternInterface.SelectionItem) is not null
                || peer.GetPattern(PatternInterface.Value) is IValueProvider { IsReadOnly: false }
                || peer.GetPattern(PatternInterface.RangeValue) is IRangeValueProvider { IsReadOnly: false };
            if (usable)
            {
                yield return element;
            }
        }
    }

    /// <summary>
    /// In the tab order itself, inside or around an element that is, or a focusable member of a group whose arrow keys move
    /// between its members (one tab stop for the group, like radio buttons and the swatch grid).
    /// </summary>
    private static bool Reachable(UIElement element, List<DependencyObject> order)
    {
        foreach (DependencyObject stop in order)
        {
            if (ReferenceEquals(stop, element) || IsAncestor(element, stop) || IsAncestor(stop, element))
            {
                return true;
            }
        }
        if (element is not Control { IsTabStop: true })
        {
            return false;
        }
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is UIElement group && (group.TabFocusNavigation == KeyboardNavigationMode.Once
                || group.XYFocusKeyboardNavigation == XYFocusKeyboardNavigationMode.Enabled))
            {
                return order.Any(stop => IsAncestor(group, stop));
            }
        }
        return false;
    }

    private static bool IsAncestor(DependencyObject ancestor, DependencyObject element)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (ReferenceEquals(parent, ancestor))
            {
                return true;
            }
        }
        return false;
    }

    private static bool Inside<T>(DependencyObject element) where T : DependencyObject
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is T)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Why the text does not show whole: its lines are closer than its letters are tall (a fixed line height that did not grow
    /// with larger text), its box is smaller than the text, or the box reaches outside a parent or the window. A scrolling
    /// parent shows the rest, so the search stops there. Text that does not grow on its own must be in a window that grows
    /// as a whole (ZoomPanel).
    /// </summary>
    private static string? Clipping(TextBlock block, FrameworkElement surface)
    {
        if (block.IsTextTrimmed)
        {
            return "trimmed";
        }
        if (!block.IsTextScaleFactorEnabled && TextScaling.Scaled && !Zoomed(block))
        {
            return "does not grow with the text size";
        }
        double size = block.IsTextScaleFactorEnabled ? TextScaling.Size(block.FontSize) : block.FontSize;
        if (block.LineHeight > 0 && block.LineStackingStrategy != LineStackingStrategy.MaxHeight && block.LineHeight < 1.15 * size)
        {
            return string.Create(CultureInfo.InvariantCulture, $"lines {block.LineHeight:0.##} apart for {size:0.#}-point text");
        }
        var probe = new TextBlock
        {
            Text = block.Text,
            FontFamily = block.FontFamily,
            FontSize = block.FontSize,
            FontWeight = block.FontWeight,
            FontStyle = block.FontStyle,
            FontStretch = block.FontStretch,
            CharacterSpacing = block.CharacterSpacing,
            LineHeight = block.LineHeight,
            LineStackingStrategy = block.LineStackingStrategy,
            TextLineBounds = block.TextLineBounds,
            Padding = block.Padding,
            IsTextScaleFactorEnabled = block.IsTextScaleFactorEnabled,
            TextWrapping = block.TextWrapping,
            // Unrounded, as the block's own size is rounded to the window's pixels rather than to whole points.
            UseLayoutRounding = false,
        };
        // A wrapping block is given its own width plus the tolerance: its width is rounded down to the pixel grid, and text
        // that just fits would otherwise wrap in the probe.
        probe.Measure(new Windows.Foundation.Size(
            block.TextWrapping == TextWrapping.NoWrap ? double.PositiveInfinity : block.ActualWidth + Tolerance, double.PositiveInfinity));
        double width = probe.DesiredSize.Width, height = probe.DesiredSize.Height;
        if (width > block.ActualWidth + Tolerance || height > block.ActualHeight + Tolerance)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"needs {width:0.##} x {height:0.##}, has {block.ActualWidth:0.##} x {block.ActualHeight:0.##}");
        }
        WinRect text = Frame(block, surface);
        for (DependencyObject? parent = VisualTreeHelper.GetParent(block); parent is not null && !ReferenceEquals(parent, surface);
             parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer or ScrollPresenter or ScrollContentPresenter)
            {
                return null;
            }
            if (parent is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 } box and not Canvas && Outside(text, Frame(box, surface)))
            {
                return $"outside {box.GetType().Name}";
            }
        }
        return Outside(text, new WinRect(0, 0, surface.ActualWidth, surface.ActualHeight)) ? "outside the window" : null;
    }

    private static bool Zoomed(DependencyObject element)
    {
        for (DependencyObject? parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ZoomPanel)
            {
                return true;
            }
        }
        return false;
    }

    private static bool Outside(WinRect inner, WinRect outer) =>
        inner.X < outer.X - Tolerance || inner.Y < outer.Y - Tolerance
        || inner.X + inner.Width > outer.X + outer.Width + Tolerance || inner.Y + inner.Height > outer.Y + outer.Height + Tolerance;

    /// <summary>Brushes of shown elements outside parts with their own colors that the contrast theme does not have.</summary>
    private static void WriteOffPalette(Utf8JsonWriter writer, DependencyObject node, FrameworkElement surface, HashSet<uint> palette)
    {
        if (node is UIElement { Visibility: Visibility.Collapsed } || OwnColors.IsMarked(node))
        {
            return;
        }
        if (node is FrameworkElement element && element.ActualWidth > 0 && element.ActualHeight > 0)
        {
            foreach ((string property, Brush? brush) in Brushes(element))
            {
                if (Foreign(brush, palette) is { } color)
                {
                    WriteElement(writer, element, surface, $"{property} #{color:X8}");
                }
            }
        }
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int index = 0; index < count; index++)
        {
            WriteOffPalette(writer, VisualTreeHelper.GetChild(node, index), surface, palette);
        }
    }

    private static IEnumerable<(string Property, Brush? Brush)> Brushes(FrameworkElement element)
    {
        switch (element)
        {
            case TextBlock text:
                yield return ("Foreground", text.Foreground);
                break;
            case Border border:
                yield return ("Background", border.Background);
                yield return ("BorderBrush", border.BorderBrush);
                break;
            case Panel panel:
                yield return ("Background", panel.Background);
                break;
            case Shape shape:
                yield return ("Fill", shape.Fill);
                yield return ("Stroke", shape.Stroke);
                break;
            case ContentPresenter presenter:
                yield return ("Foreground", presenter.Foreground);
                yield return ("Background", presenter.Background);
                yield return ("BorderBrush", presenter.BorderBrush);
                break;
        }
    }

    /// <summary>
    /// The first visible color of <paramref name="brush"/> that the palette does not have, or null. A translucent brush counts
    /// as foreign: over the window it shows a blend, which a contrast theme does not have, such as a faint button fill.
    /// </summary>
    private static uint? Foreign(Brush? brush, HashSet<uint> palette)
    {
        static uint Argb(Windows.UI.Color color) => (uint)(color.A << 24 | color.R << 16 | color.G << 8 | color.B);
        switch (brush)
        {
            case SolidColorBrush solid when solid.Color.A > 0 && solid.Opacity > 0:
                return palette.Contains(Argb(solid.Color) & 0xFFFFFF) && solid.Color.A == 255 && solid.Opacity >= 1
                    ? null : Argb(solid.Color) & 0xFFFFFF | (uint)Math.Round(solid.Color.A * Math.Min(solid.Opacity, 1)) << 24;
            case GradientBrush gradient when gradient.Opacity > 0:
                foreach (GradientStop stop in gradient.GradientStops)
                {
                    if (stop.Color.A > 0 && (!palette.Contains(Argb(stop.Color) & 0xFFFFFF) || stop.Color.A < 255))
                    {
                        return Argb(stop.Color);
                    }
                }
                return null;
            default:
                return null;
        }
    }

    private static bool Shown(UIElement element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: Visibility.Collapsed } or UIElement { Opacity: 0 })
            {
                return false;
            }
        }
        return element is not FrameworkElement { ActualWidth: <= 0 } and not FrameworkElement { ActualHeight: <= 0 };
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            DependencyObject node = stack.Pop();
            yield return node;
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int index = count - 1; index >= 0; index--)
            {
                stack.Push(VisualTreeHelper.GetChild(node, index));
            }
        }
    }

    private static WinRect Frame(FrameworkElement element, FrameworkElement surface) =>
        element.TransformToVisual(surface).TransformBounds(new WinRect(0, 0, element.ActualWidth, element.ActualHeight));

    private static void WriteElement(Utf8JsonWriter writer, DependencyObject element, FrameworkElement surface, string? reason)
    {
        writer.WriteStartObject();
        WriteIdentity(writer, element);
        if (element is FrameworkElement framework && framework.IsLoaded)
        {
            WriteFrame(writer, "frame", Frame(framework, surface));
        }
        if (reason is not null)
        {
            writer.WriteString("reason", reason);
        }
        writer.WriteEndObject();
    }

    private static void WriteIdentity(Utf8JsonWriter writer, DependencyObject element)
    {
        writer.WriteString("type", element.GetType().Name);
        AutomationPeer? peer = element is UIElement ui ? FrameworkElementAutomationPeer.CreatePeerForElement(ui) : null;
        string name = peer?.GetName() ?? string.Empty;
        if (name.Length == 0 && element is TextBlock text)
        {
            name = text.Text;
        }
        writer.WriteString("name", name);
        writer.WriteString("id", peer?.GetAutomationId() ?? string.Empty);
        writer.WriteString("control", peer?.GetAutomationControlType().ToString() ?? string.Empty);
    }

    private static void WriteFrame(Utf8JsonWriter writer, string? name, WinRect frame)
    {
        if (name is null)
        {
            writer.WriteStartArray();
        }
        else
        {
            writer.WriteStartArray(name);
        }
        writer.WriteNumberValue(Math.Round(frame.X, 2));
        writer.WriteNumberValue(Math.Round(frame.Y, 2));
        writer.WriteNumberValue(Math.Round(frame.Width, 2));
        writer.WriteNumberValue(Math.Round(frame.Height, 2));
        writer.WriteEndArray();
    }
}
