using Annotator.App.Shell;
using Annotator.Core;
using Annotator.Workspace.Settings;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Glyphs = Annotator.App.Icons.Glyphs;
using SystemMotion = Annotator.App.Motion.SystemMotion;

namespace Annotator.App.Settings;

/// <summary>
/// Port of ToolbarShrinkDemo: what Shrink does, on a loop (<see cref="ShrinkDemoPose"/>). The pointer leaves the toolbar,
/// the toolbar turns into one button, and as soon as the pointer is back the tools open again. It plays only while it is
/// on screen and starts with a still second; with animation effects off it shows the toolbar and its one button side by
/// side. UI Automation reads one image, "Shrink preview", with what it shows as its help text.
/// </summary>
internal sealed class ShrinkDemo
{
    private const double StageHeight = 92;
    private const double Top = 14;
    private static readonly (double Width, double Height) s_toolbar = (214, 36);
    private static readonly (double Width, double Height) s_button = (82, 28);
    private readonly Stage? _stage;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private bool _playing;

    public ShrinkDemo()
    {
        Root = new AccessibleElement(AutomationControlType.Image) { InSettings = true, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(Root, ShrinkDemoText.Name);
        // The Mac's accessibilityHint.
        AutomationProperties.SetHelpText(Root, ShrinkDemoText.Help);
        AutomationProperties.SetAutomationId(Root, ShrinkDemoText.Identifier);
        if (SystemMotion.ReducesMotion())
        {
            var row = new MacStack { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new Stage(s_toolbar.Width + 36, ShrinkDemoPose.Open).Root);
            FrameworkElement arrow = Glyphs.ForSymbol("arrow.right", 11, "AnnotatorGlyphSecondary");
            arrow.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(arrow);
            row.Children.Add(new Stage(s_button.Width + 36, ShrinkDemoPose.Shrunk).Root);
            Root.Children.Add(row);
            return;
        }
        _stage = new Stage(300, ShrinkDemoPose.At(0));
        Root.Children.Add(_stage.Root);
        Root.Loaded += (_, _) => Play(true);
        Root.Unloaded += (_, _) => Play(false);
    }

    public AccessibleElement Root { get; }

    /// <summary>Starts the loop from its still second when the demo appears, and stops it when it goes.</summary>
    private void Play(bool playing)
    {
        if (playing == _playing || _stage is null)
        {
            return;
        }
        _playing = playing;
        if (playing)
        {
            _clock.Restart();
            _stage.Show(ShrinkDemoPose.At(0));
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
            _clock.Stop();
        }
    }

    private void OnRendering(object? sender, object args) => _stage?.Show(ShrinkDemoPose.At(_clock.Elapsed.TotalSeconds));

    /// <summary>A small desktop with the toolbar, or its one button, docked at the top, and the pointer.</summary>
    private sealed class Stage
    {
        private readonly double _width;
        private readonly Grid _shape = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        private readonly Border _rim = new() { Background = SettingsColors.Black(0.06), Margin = new Thickness(-1, 0, -1, -2) };
        private readonly Border _background = new() { Background = SettingsColors.Window };
        private readonly Border _outline = new() { BorderBrush = SettingsColors.Primary(0.08), BorderThickness = new Thickness(1) };
        private readonly Grid _faces = new();
        private readonly RectangleGeometry _clip = new();
        private readonly FrameworkElement _toolbarFace = ToolbarFace();
        private readonly FrameworkElement _buttonFace = ButtonFace();
        private readonly Microsoft.UI.Xaml.Shapes.Path _pointer = Pointer();

        public Stage(double width, ShrinkDemoPose pose)
        {
            _width = width;
            Root = new Grid { Width = width, Height = StageHeight, IsHitTestVisible = false };
            Root.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = SettingsColors.AccentBrush(0.07),
                BorderBrush = SettingsColors.Primary(0.07),
                BorderThickness = new Thickness(1),
            });
            // Like the real toolbar docked at the top, both faces keep the top middle while the background changes.
            _toolbarFace.HorizontalAlignment = HorizontalAlignment.Center;
            _toolbarFace.VerticalAlignment = VerticalAlignment.Top;
            _buttonFace.HorizontalAlignment = HorizontalAlignment.Center;
            _buttonFace.VerticalAlignment = VerticalAlignment.Top;
            _faces.Children.Add(_toolbarFace);
            _faces.Children.Add(_buttonFace);
            _faces.Clip = _clip;
            _shape.Margin = new Thickness(0, Top, 0, 0);
            _shape.Children.Add(_rim);
            _shape.Children.Add(_background);
            _shape.Children.Add(_faces);
            _shape.Children.Add(_outline);
            Root.Children.Add(_shape);
            var pointer = new Canvas { IsHitTestVisible = false };
            pointer.Children.Add(_pointer);
            Root.Children.Add(pointer);
            Root.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, StageHeight) };
            AutomationProperties.SetAccessibilityView(Root, AccessibilityView.Raw);
            Show(pose);
        }

        public Grid Root { get; }

        public void Show(ShrinkDemoPose pose)
        {
            double width = s_toolbar.Width + (s_button.Width - s_toolbar.Width) * pose.Shrink;
            double height = s_toolbar.Height + (s_button.Height - s_toolbar.Height) * pose.Shrink;
            var radius = new CornerRadius(9 + (s_button.Height / 2 - 9) * pose.Shrink);
            _shape.Width = width;
            _shape.Height = height;
            _rim.CornerRadius = radius;
            _background.CornerRadius = radius;
            _outline.CornerRadius = radius;
            _clip.Rect = new Windows.Foundation.Rect(0, 0, width, height);
            _toolbarFace.Opacity = pose.ToolbarOpacity;
            _buttonFace.Opacity = pose.ButtonOpacity;
            Canvas.SetLeft(_pointer, pose.PointerX * _width);
            Canvas.SetTop(_pointer, pose.PointerY * StageHeight);
        }

        /// <summary>The toolbar's face, small: a few tools with Pen chosen, then Draw and three buttons.</summary>
        private static FrameworkElement ToolbarFace()
        {
            var tools = new[] { Tool.Select, Tool.Rectangle, Tool.Arrow, Tool.Pen, Tool.Text };
            var inner = new MacStack { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            for (int index = 0; index < tools.Length; index++)
            {
                var slot = new Grid { Width = 18, Height = 24, VerticalAlignment = VerticalAlignment.Center };
                if (index == 3)
                {
                    slot.Children.Add(new Border { CornerRadius = new CornerRadius(5), Background = SettingsColors.AccentBrush(0.2) });
                }
                FrameworkElement glyph = Glyphs.ForTool(tools[index], 11);
                glyph.HorizontalAlignment = HorizontalAlignment.Center;
                glyph.VerticalAlignment = VerticalAlignment.Center;
                slot.Children.Add(glyph);
                inner.Children.Add(slot);
            }
            inner.Children.Add(new Rectangle { Width = 1, Height = 18, Fill = SettingsColors.Primary(0.15), VerticalAlignment = VerticalAlignment.Center });
            inner.Children.Add(Draw(40, 24, 5));
            for (int index = 0; index < 3; index++)
            {
                var square = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = SettingsColors.Primary(0.25) };
                inner.Children.Add(new Grid { Width = 12, VerticalAlignment = VerticalAlignment.Center, Children = { square } });
            }
            var face = new Grid { Width = s_toolbar.Width, Height = s_toolbar.Height };
            face.Children.Add(inner);
            return face;
        }

        /// <summary>The one button's face: the Annotator mark and Draw, as the toolbar's capsule draws them.</summary>
        private static FrameworkElement ButtonFace()
        {
            const double MarkSize = 14;
            PathGeometry geometry = Glyphs.Geometry(Annotator.Workspace.Artwork.MarkGlyph.PathData);
            geometry.FillRule = FillRule.EvenOdd;
            (double scale, Annotator.Core.Point offset) = Annotator.Workspace.Artwork.MarkGlyph.Fit(MarkSize);
            var mark = new Canvas { Width = MarkSize, Height = MarkSize, VerticalAlignment = VerticalAlignment.Center };
            mark.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometry,
                Style = AppResources.Style("AnnotatorGlyphFilled"),
                RenderTransform = new MatrixTransform { Matrix = new Matrix(scale, 0, 0, scale, offset.X, offset.Y) },
            });
            var inner = new MacStack { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            inner.Children.Add(mark);
            inner.Children.Add(Draw(44, 20, 10));
            var face = new Grid { Width = s_button.Width, Height = s_button.Height };
            face.Children.Add(inner);
            return face;
        }

        /// <summary>Draw with its green dot, in the Draw button's colors.</summary>
        private static FrameworkElement Draw(double width, double height, double corner)
        {
            var pill = new Grid { Width = width, Height = height, VerticalAlignment = VerticalAlignment.Center };
            pill.Children.Add(new Border { Style = AppResources.Style("AnnotatorModeDraw"), CornerRadius = new CornerRadius(corner) });
            var words = new MacStack { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new Ellipse { Width = 4, Height = 4, Style = AppResources.Style("AnnotatorDotDraw"), VerticalAlignment = VerticalAlignment.Center });
            TextBlock word = SettingsText.Label("Draw", 8, "AnnotatorPrimaryText", FontWeights.SemiBold);
            word.VerticalAlignment = VerticalAlignment.Center;
            words.Children.Add(word);
            pill.Children.Add(words);
            return pill;
        }

        /// <summary>The Windows arrow pointer, small, with its tip at the canvas position.</summary>
        private static Microsoft.UI.Xaml.Shapes.Path Pointer() => new()
        {
            Data = Glyphs.Geometry("M 0.5,0.5 L 0.5,14.5 L 4,11 L 6.5,16.5 L 8.5,15.5 L 6,10.5 L 10.5,10.5 Z"),
            Fill = new SolidColorBrush(Colors.White),
            Stroke = new SolidColorBrush(Colors.Black),
            StrokeThickness = 1,
            StrokeLineJoin = PenLineJoin.Round,
        };
    }
}
