using System.Runtime.InteropServices.WindowsRuntime;
using Annotator.App.Shell;
using Annotator.App.Tutorial;
using Annotator.Core.Tutorial;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;
using Annotator.Platform.Graphics;
using Annotator.Platform.Windowing;
using Annotator.Rendering.Export;
using Annotator.Rendering.Imaging;
using Annotator.Rendering.Text;
using Annotator.Workspace;
using Annotator.Workspace.Canvas;
using Annotator.Workspace.FirstUse;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using CoreRect = Annotator.Core.Rect;

namespace Annotator.App.Reference;

/// <summary>The tutorial cards, the on-screen tip and the coffee reminder for the UX reference (spec 005, WIN-UI-04).</summary>
internal sealed partial class ReferenceHost
{
    private void ShowFirstUse(WorkspaceModel model, ReferenceScenario scenario)
    {
        switch (scenario.Surface)
        {
            case ReferenceSurface.Tutorial:
                ShowTutorial(model, scenario.TutorialState);
                break;
            case ReferenceSurface.CanvasHint:
                ShowCanvasHint(model);
                break;
            case ReferenceSurface.CoffeeReminder:
                var reminder = new FirstUseReferenceWindow(_registry, SupportReminderText.Title, CoffeeReminder());
                _popover = reminder;
                reminder.ShowForReference(OffscreenOrigin());
                Watch((FrameworkElement)reminder.Content, reminder.Hwnd);
                break;
            case ReferenceSurface.WhatsChanged:
                // The Mac capture's settled view with its fixed notes, without the window's title bar.
                var notes = new FirstUseReferenceWindow(_registry, WhatsChangedText.Title, WhatsChangedWindow.Body(
                    ReferenceStates.WhatsChangedNotes, supportAvailable: true, static () => { }, static () => { }, animate: false));
                _popover = notes;
                notes.ShowForReference(OffscreenOrigin());
                Watch((FrameworkElement)notes.Content, notes.Hwnd);
                break;
        }
    }

    /// <summary>
    /// A tutorial card's surface as the Mac capture shows it, without the room for motion around it: the welcome card, a coach
    /// card (coach-&lt;step&gt; with nothing done, coach-arrow-curved with the arrow's first goal done, coach-shape-done with the
    /// step's check) or the finish card. It waits for the checks to finish drawing in.
    /// </summary>
    private void ShowTutorial(WorkspaceModel model, string state)
    {
        var actions = new TutorialCardActions();
        bool dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        FrameworkElement content;
        if (state == "welcome")
        {
            content = TutorialCardView.Welcome(actions, new TutorialPreferences(AppDefaults.InMemory()), ReferenceUsage());
        }
        else if (state == "finish")
        {
            content = TutorialCardView.Finish(model.Snapshot, actions, dark);
        }
        else
        {
            ProgressView view = state switch
            {
                "coach-arrow-curved" => new ProgressView(TutorialStep.Arrow, 1),
                "coach-shape-done" => new ProgressView(TutorialStep.Shape, 1),
                _ => new ProgressView(TutorialSteps.All.First(step => "coach-" + step.RawValue == state), 0),
            };
            CoachContent coach = TutorialCardView.Coach(view, model.Snapshot, actions, dark);
            if (state == "coach-shape-done")
            {
                coach.TitleCheck.Reveal();
            }
            content = coach.Root;
        }
        var surface = new Border
        {
            Style = AppResources.Style("AnnotatorToolbarSurface"),
            CornerRadius = new CornerRadius(13),
            Child = content,
        };
        var window = new FirstUseReferenceWindow(_registry, "Annotator tutorial", surface);
        _popover = window;
        window.ShowForReference(OffscreenOrigin());
        Microsoft.UI.Dispatching.DispatcherQueueTimer settle = _queue.CreateTimer();
        settle.Interval = TimeSpan.FromSeconds(0.8);
        settle.IsRepeating = false;
        settle.Tick += (_, _) => Watch((FrameworkElement)window.Content, window.Hwnd);
        settle.Start();
    }

    /// <summary>
    /// The on-screen tip while drawing in the Mac capture's 540 by 130-point view: the box drawn by the canvas's own display
    /// list with the ✕ that turns tips off, static text with the tip's words for UI Automation, as the Mac's view reads, and
    /// then the ✕'s button over its click area.
    /// </summary>
    private void ShowCanvasHint(WorkspaceModel model)
    {
        PointInt32 origin = OffscreenOrigin();
        double scale = WindowChrome.MonitorAt(origin.X, origin.Y).Scale;
        var measurer = new DirectWriteTextMeasurer();
        // The canvas passes the zoom facts; zoom is off, so the tip has no zoom line, as the Mac's has none.
        string text = WorkspaceHint.Text(model.Snapshot, hasTextEditor: false, hasActiveEdit: false, model.Snapshot.ZoomHint);
        var view = new CoreRect(0, 0, HintBox.MaximumWidth, HintBox.ViewHeight);
        // While drawing the canvas takes clicks, so the tip has its ✕.
        (DisplayList list, CoreRect box) = HintBox.Build(text, view, measurer, closable: true);
        RasterImage image;
        using (GraphicsDevices devices = GraphicsDevices.Create(new GraphicsDeviceOptions { UseWarp = true }))
        using (var exporter = new PngExporter(devices, measurer))
        {
            image = exporter.Render([SceneSnapshot.Create([list])], new Size(view.Width, view.Height), scale);
        }
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using (Stream pixels = bitmap.PixelBuffer.AsStream())
        {
            for (int row = 0; row < image.Height; row++)
            {
                pixels.Write(image.Pixels.Slice(row * image.Stride, image.Width * 4));
            }
        }
        bitmap.Invalidate();
        var surface = new Grid { Width = view.Width, Height = view.Height };
        var picture = new Image { Source = bitmap, Stretch = Stretch.Fill };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(picture, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        surface.Children.Add(picture);
        // The Mac's hint view is one static text element over its whole frame.
        surface.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Width = view.Width,
            Height = view.Height,
            IsHitTestVisible = false,
        });
        // Mac HintCloseButton: a borderless button over the ✕'s click area.
        CoreRect close = HintBox.CloseAccessibleFrame(box);
        var closeButton = new Button
        {
            Width = close.Width,
            Height = close.Height,
            Margin = new Thickness(close.X, close.Y, 0, 0),
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(closeButton, WorkspaceHint.CloseLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(closeButton, WorkspaceHint.CloseHelp);
        surface.Children.Add(closeButton);
        var window = new FirstUseReferenceWindow(_registry, "On-screen tips", surface);
        _popover = window;
        window.ShowForReference(origin);
        Watch((FrameworkElement)window.Content, window.Hwnd);
    }

    /// <summary>The Mac capture's coffee reminder: its 300-point content at (80, 16) in a 460 by 135-point window.</summary>
    private static FrameworkElement CoffeeReminder()
    {
        var surface = new Grid { Width = 460, Height = 135, Background = AppResources.Brush("AnnotatorWindowBackgroundBrush") };
        FrameworkElement body = SupportReminderPopover.Body(static () => false, static () => { });
        body.HorizontalAlignment = HorizontalAlignment.Left;
        body.VerticalAlignment = VerticalAlignment.Top;
        body.Margin = new Thickness(80, 16, 0, 0);
        surface.Children.Add(body);
        return surface;
    }

    /// <summary>Development only (UX reference capture): one first-use surface on its own; nothing closes it.</summary>
    private sealed class FirstUseReferenceWindow : PopoverWindow
    {
        public FirstUseReferenceWindow(WindowRegistry registry, string title, FrameworkElement surface)
            : base(registry, title) => Surface = surface;

        protected override void FocusFirst()
        {
        }
    }
}
