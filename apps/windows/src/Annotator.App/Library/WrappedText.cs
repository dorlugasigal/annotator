using Annotator.App.Pickers;
using Annotator.App.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Annotator.App.Library;

/// <summary>
/// Caption text that wraps where the Mac's would. Segoe UI's words are narrower than SF Pro's, so the text wraps at the
/// available width scaled by how much narrower a reference line of the Mac capture is in Segoe UI. The last two words stay
/// together, as the Mac's standard line breaking avoids a single word on the last line. UI Automation reads the words as
/// written.
/// </summary>
internal sealed partial class WrappedText : Panel
{
    /// <summary>A caption line of the Mac capture and its width in SF Pro at 10 points (tools-window/section-my-shapes).</summary>
    private const string ReferenceLine = "Save drawings or text. Covers, pixel effects and images cannot";
    private const double ReferenceWidth = 305.5;
    private static double? s_ratio;
    private readonly TextBlock _block;

    public WrappedText(string text, double size, string style)
    {
        _block = PickerText.Line(string.Empty, size, style);
        _block.TextWrapping = TextWrapping.Wrap;
        _block.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_block);
        Text = text;
    }

    public string Text
    {
        get => AutomationProperties.GetName(_block);
        set
        {
            AutomationProperties.SetName(_block, value);
            int last = value.LastIndexOf(' ');
            _block.Text = last > 0 && value.IndexOf(' ', StringComparison.Ordinal) < last ? value[..last] + '\u00A0' + value[(last + 1)..] : value;
            InvalidateMeasure();
        }
    }

    public TextBlock Block => _block;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? availableSize.Width : availableSize.Width * Ratio();
        _block.Measure(new Size(width, double.PositiveInfinity));
        return _block.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _block.Arrange(new Rect(0, 0, Math.Min(finalSize.Width, _block.DesiredSize.Width), _block.DesiredSize.Height));
        return finalSize;
    }

    /// <summary>Segoe UI's width of the reference line against SF Pro's, at most 1.</summary>
    private static double Ratio()
    {
        if (s_ratio is { } known)
        {
            return known;
        }
        TextBlock probe = PickerText.Line(ReferenceLine, 10, "AnnotatorSecondaryText");
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double ratio = probe.DesiredSize.Width > 0 ? Math.Min(1, probe.DesiredSize.Width / ReferenceWidth) : 1;
        s_ratio = ratio;
        return ratio;
    }
}
