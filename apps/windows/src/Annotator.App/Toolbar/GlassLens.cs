using Annotator.Core;
using Annotator.Workspace.Toolbar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinColor = Windows.UI.Color;

namespace Annotator.App.Toolbar;

/// <summary>
/// Glass toolbar's raised lens behind a chosen or highlighted button (WIN-UI-09, <see cref="ToolbarLens"/>): a soft shadow,
/// the body, the role's faint tint and a 1-point rim just inside the edge. It is decoration: it takes no room, input or
/// UI Automation node, fills what its parent gives it (plus an outset) and casts its shadow past that.
/// </summary>
internal sealed partial class GlassLens : Grid
{
    private readonly List<(Border Shape, double Outset)> _shapes = [];
    private double _radius;

    public GlassLens(ToolbarLensLook look, double cornerRadius, double outsetX = 0, double outsetY = 0)
    {
        IsHitTestVisible = false;
        Margin = new Thickness(-outsetX, -outsetY, -outsetX, -outsetY);
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        var shadow = new Grid();
        foreach ((double outset, double share) in ToolbarLens.ShadowSteps)
        {
            shadow.Children.Add(Shape(new Border
            {
                Margin = new Thickness(-outset, ToolbarLens.ShadowOffset - outset, -outset, -ToolbarLens.ShadowOffset - outset),
                Background = new SolidColorBrush(Color(new RGBA(0, 0, 0, look.ShadowOpacity * share))),
            }, outset));
        }
        CastShadow = shadow;
        Children.Add(shadow);
        Children.Add(Shape(new Border { Background = Gradient(look.BodyTop, look.BodyBottom, 1) }));
        if (look.Tint is { } tint)
        {
            Tint = Shape(new Border { Background = new SolidColorBrush(Color(tint)) });
            Children.Add(Tint);
        }
        Children.Add(Shape(new Border { BorderThickness = new Thickness(1), BorderBrush = Gradient(look.RimTop, look.RimBottom, 1) }));
        Radius = cornerRadius;
    }

    /// <summary>The shadow, which a change of role can fade.</summary>
    public UIElement CastShadow { get; }

    /// <summary>The role's tint over the body, which a change of role can fade, or null.</summary>
    public UIElement? Tint { get; }

    /// <summary>The corner radius; the shadow's steps round their corners to match.</summary>
    public double Radius
    {
        get => _radius;
        set
        {
            _radius = value;
            foreach ((Border shape, double outset) in _shapes)
            {
                shape.CornerRadius = new CornerRadius(Math.Max(0, value + outset));
            }
        }
    }

    public static WinColor Color(RGBA color) => WinColor.FromArgb(Byte(color.Alpha), Byte(color.Red), Byte(color.Green), Byte(color.Blue));

    private Border Shape(Border shape, double outset = 0)
    {
        _shapes.Add((shape, outset));
        return shape;
    }

    /// <summary>Top to bottom from <paramref name="top"/> to <paramref name="bottom"/>, which it reaches at <paramref name="end"/>.</summary>
    private static LinearGradientBrush Gradient(RGBA top, RGBA bottom, double end)
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        brush.GradientStops.Add(new GradientStop { Color = Color(top), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Color(bottom), Offset = end });
        return brush;
    }

    private static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
