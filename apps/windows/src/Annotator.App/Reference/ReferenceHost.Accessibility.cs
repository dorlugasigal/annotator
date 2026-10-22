using Annotator.App.Shell;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Annotator.App.Reference;

/// <summary>
/// Development only: a reference state as in a Windows contrast theme or with larger Windows text (WIN-A11Y-03), through
/// WinUI's test hooks (<see cref="XamlTestHooks"/>) and the app's own contrast colors (<see cref="ContrastTheme"/>). Nothing
/// changes in Windows. A simulation that WinUI did not apply fails the state instead of showing a made-up one.
/// </summary>
internal sealed partial class ReferenceHost
{
    private string? _simulationError;

    private void SimulateAccessibility()
    {
        try
        {
            if (_request.Contrast is { } palette)
            {
                ContrastTheme.Simulate(palette.SystemColors);
                XamlTestHooks.SetContrast(palette);
            }
            if (_request.TextScale > 1)
            {
                TextScaling.Simulate(_request.TextScale);
                XamlTestHooks.SetTextScale(_request.TextScale);
            }
        }
        catch (Exception error) when (error is NotSupportedException or System.Runtime.InteropServices.COMException or InvalidCastException
                                      or ArgumentException)
        {
            _simulationError = $"WinUI's test hooks are unavailable: {error.Message}";
        }
    }

    /// <summary>Why the requested simulation does not show, or null when WinUI applied it.</summary>
    private string? SimulationProblem()
    {
        if (_simulationError is not null)
        {
            return _simulationError;
        }
        if (_request.Contrast is { } palette && (AppResources.Brush("AnnotatorWindowBackgroundBrush") as SolidColorBrush)?.Color is { } window
            && (uint)(window.R << 16 | window.G << 8 | window.B) != (palette.Window & 0xFFFFFF))
        {
            return $"WinUI did not apply the {palette.Name} contrast theme (the window background is #{window.R:X2}{window.G:X2}{window.B:X2}).";
        }
        if (_request.TextScale > 1)
        {
            // WinUI scales a 14-point line to 14 + (18 - e ln 14) (scale - 1) points (TextFormatting::GetScaledFontSize).
            double expected = 14 + Math.Max(18 - Math.E * Math.Log(14), 0) * (_request.TextScale - 1);
            double scaled = LineHeight(enabled: true), plain = LineHeight(enabled: false);
            if (Math.Abs(scaled / plain - expected / 14) > 0.1)
            {
                return $"WinUI did not scale text by {_request.TextScale} (a 14-point line is {scaled / plain:0.##} times as tall, not {expected / 14:0.##}).";
            }
        }
        return null;
    }

    private static double LineHeight(bool enabled)
    {
        var probe = new TextBlock { Text = "Annotator", FontSize = 14, IsTextScaleFactorEnabled = enabled };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Height;
    }
}
