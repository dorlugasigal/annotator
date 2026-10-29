using Annotator.App.Pickers;
using Annotator.Core;
using Annotator.Workspace;
using Annotator.Workspace.Library;
using Annotator.Workspace.Settings;
using Annotator.Workspace.Sharing;
using Size = Windows.Foundation.Size;

namespace Annotator.App.Reference;

/// <summary>Which window a reference state shows.</summary>
internal enum ReferenceSurface
{
    Toolbar,
    Flyout,
    TrayIcon,

    /// <summary>A drawing canvas on a real overlay, beyond the desktop's edge, with the Mac capture's fictional backdrop.</summary>
    Canvas,

    /// <summary>The full properties panel on its own, with 16 points of padding (the Mac's ContextualInspector().padding(16)).</summary>
    Properties,

    /// <summary>The arrowhead choices for the start or end of an arrow (Mac ArrowHeadPalette).</summary>
    ArrowHeads,

    /// <summary>The color popover (Mac InspectorColorPalette) for the stroke or the fill.</summary>
    Picker,

    /// <summary>The Tools window's content (Mac ToolLibraryPanel) at a width and maximum height, on the window background.</summary>
    ToolsWindow,

    /// <summary>The Settings window's content (Mac CustomizationView) on a tab, 840 points wide and 700, 1500 or 2400 high.</summary>
    Settings,

    /// <summary>Launch at sign-in on its own (Mac LoginItemSettings().padding(16)), 620 points wide, for a startup task state.</summary>
    LaunchAtSignIn,

    /// <summary>Report a problem or Share feedback (Mac FeedbackView), 460 by 680 points.</summary>
    Feedback,

    /// <summary>What's changed on its own (Mac ReleaseNotesView().padding(20)), 560 by 640 points.</summary>
    ReleaseNotes,

    /// <summary>The keycaps of one global shortcut with 8 points of padding (Mac ShortcutKeycaps().padding(8)).</summary>
    Keycaps,

    /// <summary>The tutorial card (Mac TutorialViews): the welcome card, a coach card or the finish card.</summary>
    Tutorial,

    /// <summary>The canvas's on-screen tip on its own (Mac WorkspaceHintView), 540 by 130 points.</summary>
    CanvasHint,

    /// <summary>The coffee reminder's content (Mac SupportReminderView) in a 460 by 135-point window.</summary>
    CoffeeReminder,

    /// <summary>What's changed after an update (Mac WhatsChangedView) with fixed notes, 480 by 560 points.</summary>
    WhatsChanged,

    /// <summary>The live zoom level (Mac LiveZoomControls) with 12 points of padding, compact or full.</summary>
    ZoomControls,

    /// <summary>Region sharing: the Share a region window, the region outline or the covered output (spec 012).</summary>
    RegionSharing,

    /// <summary>The error window's content (Mac WorkspaceErrorController) with the workspace's error.</summary>
    ErrorWindow,

    /// <summary>Windows only: the explanation before Windows' screen capture questions (WIN-CAP-04), asking or not.</summary>
    ScreenAccess,

    /// <summary>The full-screen break countdown (Mac BreakScreenFace) in a fixed state at the capture's size (spec 014).</summary>
    BreakTimer,
}

/// <summary>Which part of region sharing a <see cref="ReferenceSurface.RegionSharing"/> state shows.</summary>
internal enum RegionSharingPart
{
    Chooser,
    OutlineChoosing,
    OutlineLive,
    OutputCovered,
}

/// <summary>
/// How a canvas state is built (UXReferenceCaptureTests.test07CanvasChrome): the tool, the changes to the sample scene
/// before it shows, and the pointer path of a gesture that is still in progress when the canvas is captured, in points.
/// </summary>
/// <param name="Prepare">Changes the sample scene: selection, rotation, board and added text.</param>
internal sealed record CanvasScript(Tool Tool, CanvasPreparation Prepare)
{
    public Annotator.Core.Point[] Gesture { get; init; } = [];

    /// <summary>Characters typed through the canvas's key input after the gesture is released as a click, or null.</summary>
    public string? Typing { get; init; }
}

/// <summary>Changes the sample scene's document before it is submitted.</summary>
internal delegate void CanvasPreparation(ref CanvasDocument document, CanvasSample sample);

/// <summary>The Mac capture's sample objects (addSampleScene), for <see cref="CanvasScript.Prepare"/>.</summary>
internal sealed class CanvasSample
{
    public required Element Box { get; init; }

    public required Element Oval { get; init; }

    public required Element Arrow { get; init; }

    public required Element Locked { get; init; }

    public Board Board { get; set; } = Board.Desktop;

    /// <summary>The text "New note" that the Mac's text-editing state types at (480, 330) and commits before the board states.</summary>
    public bool AddsNewNote { get; set; }
}

/// <summary>
/// How one state of reference/ux-reference/macos is built on Windows: a saved profile, then ordinary workspace calls, the
/// same calls a person's clicks make. A state whose feature does not exist yet names the reason instead of faking it.
/// </summary>
internal sealed record ReferenceScenario(ReferenceSurface Surface)
{
    /// <summary>The Mac capture's default available size (ToolbarView.availableSize).</summary>
    public static readonly Size DefaultAvailable = new(1000, 900);

    public Action<ReferenceProfile>? Profile { get; init; }

    public Action<WorkspaceModel>? Setup { get; init; }

    /// <summary>The toolbar's available size in points, as the Mac capture passed it.</summary>
    public Size Available { get; init; } = DefaultAvailable;

    /// <summary>The flyout of a development build: the Local build badge.</summary>
    public bool DeveloperPreview { get; init; } = true;

    /// <summary>Why the state cannot be shown on Windows yet, or null.</summary>
    public string? Unavailable { get; init; }

    /// <summary>How a canvas state is built, for <see cref="ReferenceSurface.Canvas"/>.</summary>
    public CanvasScript? Canvas { get; init; }

    /// <summary>The surface's width in points, for <see cref="ReferenceSurface.Properties"/>.</summary>
    public double Width { get; init; } = 648;

    /// <summary>The start's choices rather than the end's, for <see cref="ReferenceSurface.ArrowHeads"/>.</summary>
    public bool Start { get; init; }

    /// <summary>Which paint the color popover edits, for <see cref="ReferenceSurface.Picker"/>.</summary>
    public ColorPickerRequest? Picker { get; init; }

    /// <summary>The section the Tools window shows, for <see cref="ReferenceSurface.ToolsWindow"/>.</summary>
    public ToolLibrarySection Section { get; init; }

    /// <summary>ToolLibraryPanel's maximumHeight, for <see cref="ReferenceSurface.ToolsWindow"/>.</summary>
    public double MaximumHeight { get; init; } = ToolLibraryLayout.DefaultMaximumHeight;

    /// <summary>The tab Settings shows, for <see cref="ReferenceSurface.Settings"/>.</summary>
    public SettingsTab Tab { get; init; }

    /// <summary>
    /// The surface's height in points, for <see cref="ReferenceSurface.Settings"/>: 700, or 1500 for the full states (2400 for
    /// Toolbar &amp; Shortcuts).
    /// </summary>
    public double Height { get; init; } = 700;

    /// <summary>The startup task's state that launch at sign-in reads; the Mac capture's tabs show it off.</summary>
    public LaunchAtSignInState Launch { get; init; } = LaunchAtSignInState.Off;

    /// <summary>Which feedback window, for <see cref="ReferenceSurface.Feedback"/>.</summary>
    public FeedbackKind Feedback { get; init; }

    /// <summary>
    /// The flyout says why update checks are unavailable under a disabled Check for updates… (the Mac's canCheckForUpdates
    /// false), as a development build does. The other menu states can check, as the Mac capture set canCheckForUpdates for
    /// them and a copy from the Microsoft Store can.
    /// </summary>
    public bool UpdatesUnavailable { get; init; }

    /// <summary>
    /// A check found a newer release: the flyout reads Update available… with the status under it (the Mac's
    /// updateReminderVersion "0.2.0"). Windows shows a portable copy's words, which say where to get it.
    /// </summary>
    public bool UpdateAvailable { get; init; }

    /// <summary>The tutorial card's state, for <see cref="ReferenceSurface.Tutorial"/>: welcome, coach-&lt;step&gt;, coach-arrow-curved, coach-shape-done or finish.</summary>
    public string TutorialState { get; init; } = string.Empty;

    /// <summary>The vertical rail's compact controls rather than the full row, for <see cref="ReferenceSurface.ZoomControls"/>.</summary>
    public bool Compact { get; init; }

    /// <summary>The part of region sharing, for <see cref="ReferenceSurface.RegionSharing"/>.</summary>
    public RegionSharingPart SharingPart { get; init; }

    /// <summary>The person's choices in the Share a region window, for <see cref="RegionSharingPart.Chooser"/>.</summary>
    public Action<RegionSharing>? SharingSetup { get; init; }

    /// <summary>Region sharing is on, for <see cref="ReferenceSurface.TrayIcon"/>.</summary>
    public bool SharingOn { get; init; }

    /// <summary>The clipboard holds an image, as another app copied it, before <see cref="Setup"/> runs.</summary>
    public bool ClipboardHasImage { get; init; }

    /// <summary>
    /// Windows' screen access as a packaged build reads it (WIN-CAP-04), for Settings &gt; Permissions; null is a development
    /// build, which needs none.
    /// </summary>
    public Annotator.Workspace.Capture.ScreenAccess? ScreenAccess { get; init; }

    /// <summary>The explanation while Windows asks, for <see cref="ReferenceSurface.ScreenAccess"/>.</summary>
    public bool Asking { get; init; }

    /// <summary>
    /// What the break screen shows, for <see cref="ReferenceSurface.BreakTimer"/> at <see cref="Width"/> by
    /// <see cref="Height"/>. The host writes its end time.
    /// </summary>
    public Annotator.Workspace.Breaks.BreakFace? Break { get; init; }

    /// <summary>How long the surface's own animations run before the capture, as the Mac capture's settle time.</summary>
    public TimeSpan Settle { get; init; }
}

/// <summary>The profile fields the reference states change. The captures start with the properties shown, the default.</summary>
internal sealed class ReferenceProfile
{
    public WorkspacePreferences Preferences = new();
}

/// <summary>The named states of the toolbar and the tray flyout and icon (reference/ux-reference/macos/INDEX.md).</summary>
internal static partial class ReferenceStates
{
    /// <summary>The Mac capture's available size for vertical toolbars.</summary>
    private static readonly Size VerticalAvailable = new(880, 620);

    public static ReferenceScenario? Find(string surface, string state) => surface switch
    {
        "toolbar" => Toolbar(state),
        "menu" => Menu(state),
        "canvas" => Canvas(state),
        "properties" => Properties(state),
        "pickers" => Pickers(state),
        "tools-window" => Tools(state),
        "settings" => Settings(state),
        "tutorial" => Tutorial(state),
        "dialogs" => Dialogs(state),
        "region-sharing" => Sharing(state),
        "break-timer" => Break(state),
        _ => null,
    };

    /// <summary>
    /// UXReferenceCaptureTests.test05Settings: every tab at the default 840 by 700 points and stretched to 1500 high (2400
    /// for Toolbar &amp; Shortcuts, the longest tab), on the
    /// default profile with launch at sign-in off, and launch at sign-in alone for four startup states. The Mac's
    /// requires-approval is Windows' DisabledByUser and not-found an unpackaged build (WIN-SYS-01). The Toolbar &amp;
    /// Shortcuts and Pointer tabs show the pages the app sets through SettingsWindow.SetPage.
    /// </summary>
    /// <summary>The height of a tab's full state: the whole scrolling content shows.</summary>
    private static double FullHeight(SettingsTab tab) => tab == SettingsTab.Toolbar ? 2400 : 1500;

    private static ReferenceScenario? Settings(string state)
    {
        const string Launch = "launch-at-login-";
        if (state.StartsWith(Launch, StringComparison.Ordinal))
        {
            LaunchAtSignInState? launch = state[Launch.Length..] switch
            {
                "off" => LaunchAtSignInState.Off,
                "enabled" => LaunchAtSignInState.On,
                "requires-approval" => LaunchAtSignInState.DisabledByUser,
                "not-found" => LaunchAtSignInState.NotPackaged,
                _ => null,
            };
            return launch is { } shown ? new ReferenceScenario(ReferenceSurface.LaunchAtSignIn) { Launch = shown } : null;
        }
        bool full = state.EndsWith("-full", StringComparison.Ordinal);
        string name = full ? state[..^"-full".Length] : state;
        // Windows only (WIN-CAP-04): Permissions in a packaged build that Windows has not asked yet, or that the person refused.
        if (name is "tab-permissions-not-asked" or "tab-permissions-refused")
        {
            return new ReferenceScenario(ReferenceSurface.Settings)
            {
                Tab = SettingsTab.Permissions,
                Height = full ? 1500 : 700,
                ScreenAccess = name == "tab-permissions-refused" ? Annotator.Workspace.Capture.ScreenAccess.ScreenOff : Annotator.Workspace.Capture.ScreenAccess.NotAsked,
            };
        }
        if (!name.StartsWith("tab-", StringComparison.Ordinal) || SettingsTab.FromRawValue(name["tab-".Length..]) is not { } tab)
        {
            return null;
        }
        return new ReferenceScenario(ReferenceSurface.Settings) { Tab = tab, Height = full ? FullHeight(tab) : 700 };
    }

    /// <summary>
    /// UXReferenceCaptureTests.test06WindowsAndDialogs: the feedback windows, What's changed, the on-screen tip and the coffee
    /// reminder (<see cref="FirstUseDialogs"/>), the keycaps (<see cref="ShortcutReferenceStates"/>) and the error window with
    /// CanvasError.unsupportedSchema(9)'s message, which the Mac capture shows, reported the way the app reports errors.
    /// </summary>
    private static ReferenceScenario? Dialogs(string state) => state switch
    {
        "report-a-problem" => new ReferenceScenario(ReferenceSurface.Feedback) { Feedback = FeedbackKind.Issue },
        "share-feedback" => new ReferenceScenario(ReferenceSurface.Feedback) { Feedback = FeedbackKind.Feedback },
        "whats-changed" => new ReferenceScenario(ReferenceSurface.ReleaseNotes),
        "whats-changed-update" => new ReferenceScenario(ReferenceSurface.WhatsChanged),
        "coffee-reminder" or "canvas-hint" => FirstUseDialogs(state),
        "error-window" => new ReferenceScenario(ReferenceSurface.ErrorWindow)
        {
            Setup = model => model.Report(CanvasError.UnsupportedSchema(9).Message),
        },
        // Windows only (WIN-CAP-04): the explanation before Windows' questions, and while Windows asks.
        "screen-access" => new ReferenceScenario(ReferenceSurface.ScreenAccess),
        "screen-access-asking" => new ReferenceScenario(ReferenceSurface.ScreenAccess) { Asking = true },
        _ when state.StartsWith("keycaps-", StringComparison.Ordinal) => ShortcutReferenceStates.Keycaps(state),
        _ => null,
    };

    /// <summary>
    /// The Tools window states of UXReferenceCaptureTests.test06WindowsAndDialogs: a drawing workspace whose tool is each
    /// section's first tool (Pen for My shapes), the panel 368 points wide, then Shapes 780 points wide with a maximum height
    /// of 570 after the last section. Paste image is an action that never becomes the tool (the Mac capture sets the tool
    /// directly), so the Extras state keeps Pen, and no tile shows as the current one there.
    /// </summary>
    private static ReferenceScenario? Tools(string state)
    {
        static ReferenceScenario Section(ToolLibrarySection section, Tool tool, double width = ToolLibraryLayout.DefaultWidth,
                                         double maximumHeight = ToolLibraryLayout.DefaultMaximumHeight) => new(ReferenceSurface.ToolsWindow)
        {
            Section = section,
            Width = width,
            MaximumHeight = maximumHeight,
            Setup = model =>
            {
                model.ShowToolbar();
                model.BeginDrawing();
                model.Choose(tool);
            },
        };
        return state switch
        {
            "section-draw" => Section(ToolLibrarySection.Draw, Tool.Select),
            "section-shapes" => Section(ToolLibrarySection.Shapes, Tool.Rectangle),
            "section-focus" => Section(ToolLibrarySection.Focus, Tool.Cover),
            "section-extras" => Section(ToolLibrarySection.Extras, Tool.Pen),
            "section-my-shapes" => Section(ToolLibrarySection.MyShapes, Tool.Pen),
            "shapes-resized" => Section(ToolLibrarySection.Shapes, Tool.Pen, 780, 570),
            _ => null,
        };
    }

    /// <summary>
    /// The full properties of UXReferenceCaptureTests.test02PropertiesAndPickers: each tool with its default style in a
    /// drawing workspace, 648 points wide, and the variants with a fill, a curved arrow, Smart Draw and a 932-point width.
    /// </summary>
    private static ReferenceScenario? Properties(string state)
    {
        static ReferenceScenario Panel(Tool tool, Action<WorkspaceModel>? change = null, double width = 648) =>
            new(ReferenceSurface.Properties)
            {
                Width = width,
                Setup = model =>
                {
                    model.ShowToolbar();
                    model.Choose(tool);
                    change?.Invoke(model);
                },
            };
        return state switch
        {
            "rectangle-filled" => Panel(Tool.Rectangle, model => model.ApplyStyle((ref Style style) => style.SetFillEnabled(true))),
            "arrow-curved" => Panel(Tool.Arrow, model => model.ApplyStyle((ref Style style) => style.Curved = true)),
            "pen-smart-draw" => Panel(Tool.Pen, model => model.SetSmartDraw(true)),
            "diamond-wide" => Panel(Tool.Diamond, width: 932),
            _ when Tool.FromRawValue(state) is { } tool => Panel(tool) with { ClipboardHasImage = tool == Tool.Image },
            _ => null,
        };
    }

    /// <summary>
    /// The picker states of UXReferenceCaptureTests.test02PropertiesAndPickers. The color popover: the Rectangle tool with
    /// its default style, then Fill shape on, then the Spectrum gradient on the stroke, each step keeping the one before,
    /// then the Laser pointer's default style. The arrowhead choices: the Arrow tool's default style. The zoom controls:
    /// screen zoom at its default 2x.
    /// </summary>
    private static ReferenceScenario? Pickers(string state)
    {
        static void Rectangle(WorkspaceModel model)
        {
            model.ShowToolbar();
            model.Choose(Tool.Rectangle);
            model.ApplyStyle((ref Style style) => style = Style.Defaults(Tool.Rectangle));
        }
        // A rectangle drawn with its default style, then the Text tool's click inside it: the workspace calls a canvas makes
        // when its label editor opens (Mac startShapeLabel), a new label following the stroke, or one whose color the
        // person chose in the popover.
        static void EditLabel(WorkspaceModel model, bool follows)
        {
            Rectangle(model);
            DisplaySnapshot display = model.Snapshot.Displays[0];
            CanvasDocument document = display.Document;
            var shape = new Element(Tool.Rectangle, new Rect(200, 200, 320, 160), style: Style.Defaults(Tool.Rectangle));
            document.Begin();
            document.Add(shape);
            document.Commit();
            model.Submit(new EditSubmission(new DisplayEdit(display.Id, display.Version, document)));
            model.Choose(Tool.Text);
            display = model.Snapshot.Displays[0];
            model.BeginEdit(display.Id, display.Version, EditKind.Text);
            Style own = model.Snapshot.Preferences.ToolStyles.TryGetValue(Tool.Text.RawValue, out Style saved) ? saved : Style.Defaults(Tool.Text);
            own.Alignment = TextAlignment.Center;
            var label = new LabelEdit(true, shape.Style.Color, shape.Style.StrokeGradient);
            model.Perform(new CanvasCommand(CanvasCommandKind.TextStyle, display.Id) { Style = label.Following(own), Label = label });
            if (!follows)
            {
                model.SetPaintColor(Palette.Blue, fill: false);
            }
        }
        return state switch
        {
            "color-stroke" => new ReferenceScenario(ReferenceSurface.Picker) { Setup = Rectangle, Picker = ColorPickerRequest.Stroke },
            "color-fill" => new ReferenceScenario(ReferenceSurface.Picker)
            {
                Setup = model =>
                {
                    Rectangle(model);
                    model.SetFillEnabled(true);
                },
                Picker = ColorPickerRequest.FillColor,
            },
            "color-gradient" => new ReferenceScenario(ReferenceSurface.Picker)
            {
                Setup = model =>
                {
                    Rectangle(model);
                    model.SetFillEnabled(true);
                    model.SetGradient(GradientPreset.Spectrum.Gradient, fill: false);
                },
                Picker = ColorPickerRequest.Stroke,
            },
            // The Laser pointer's color: solid colors only, without the Gradient and Solid choice.
            "color-laser" => new ReferenceScenario(ReferenceSurface.Picker)
            {
                Setup = model =>
                {
                    model.ShowToolbar();
                    model.Choose(Tool.Laser);
                    model.ApplyStyle((ref Style style) => style = Style.Defaults(Tool.Laser));
                },
                Picker = ColorPickerRequest.Stroke,
            },
            // Windows only until the Mac reference has them: the stroke popover while a rectangle's label is edited, in its
            // own color (Match outline available) or following the rectangle's stroke (Match outline unavailable).
            "color-stroke-label" or "color-stroke-label-following" => new ReferenceScenario(ReferenceSurface.Picker)
            {
                Setup = model => EditLabel(model, follows: state == "color-stroke-label-following"),
                Picker = ColorPickerRequest.Stroke,
            },
            "arrowhead-start" or "arrowhead-end" => new ReferenceScenario(ReferenceSurface.ArrowHeads)
            {
                Start = state == "arrowhead-start",
                Setup = model =>
                {
                    model.ShowToolbar();
                    model.Choose(Tool.Arrow);
                },
            },
            "zoom-compact" or "zoom-full" => new ReferenceScenario(ReferenceSurface.ZoomControls)
            {
                Compact = state == "zoom-compact",
                Setup = model =>
                {
                    model.ShowToolbar();
                    model.SetMagnification(2);
                },
            },
            _ => null,
        };
    }

    /// <summary>
    /// The canvas states in the Mac capture's order. Its changes carry over: the oval stays rotated after select-rotated,
    /// and the board states show the text that text-editing committed.
    /// </summary>
    private static ReferenceScenario? Canvas(string state)
    {
        static ReferenceScenario Script(Tool tool, CanvasPreparation prepare, params Annotator.Core.Point[] gesture) =>
            new(ReferenceSurface.Canvas) { Canvas = new CanvasScript(tool, prepare) { Gesture = gesture } };
        static void Nothing(ref CanvasDocument document, CanvasSample sample)
        {
        }
        static void Rotated(ref CanvasDocument document, CanvasSample sample) =>
            document.Update(sample.Oval.Id, (ref Element element) => element.Rotation = 0.35);
        return state switch
        {
            "scene" => Script(Tool.Select, Nothing),
            "select-shape" => Script(Tool.Select, (ref CanvasDocument document, CanvasSample sample) => document.Select([sample.Box.Id])),
            "select-multiple" => Script(Tool.Select, (ref CanvasDocument document, CanvasSample sample) => document.Select([sample.Box.Id, sample.Oval.Id])),
            "select-arrow" => Script(Tool.Select, (ref CanvasDocument document, CanvasSample sample) => document.Select([sample.Arrow.Id])),
            "select-locked" => Script(Tool.Select, (ref CanvasDocument document, CanvasSample sample) => document.Select([sample.Locked.Id])),
            "attach-hint" => Script(Tool.Arrow, Nothing, new(560, 470), new(480, 300), new(424, 178)),
            "select-rotated" => Script(Tool.Select, (ref CanvasDocument document, CanvasSample sample) =>
            {
                Rotated(ref document, sample);
                document.Select([sample.Oval.Id]);
            }),
            "marquee" => Script(Tool.Select, Rotated, new(90, 270), new(260, 360), new(380, 450)),
            "eraser-preview" => Script(Tool.Eraser, Rotated, new(140, 395), new(260, 405), new(360, 400)),
            "text-editing" => new ReferenceScenario(ReferenceSurface.Canvas)
            {
                // Typing new text with the saved Text settings: a click at (480, 330) with the Text tool, then "New note",
                // still being edited when the canvas is captured.
                Canvas = new CanvasScript(Tool.Text, Rotated) { Gesture = [new(480, 330)], Typing = "New note" },
            },
            "board-whiteboard" or "board-blackboard" => Script(Tool.Select, (ref CanvasDocument document, CanvasSample sample) =>
            {
                Rotated(ref document, sample);
                sample.AddsNewNote = true;
                sample.Board = state == "board-whiteboard" ? Board.Whiteboard : Board.Blackboard;
                document.Select([sample.Box.Id]);
            }),
            _ => null,
        };
    }

    private static ReferenceScenario? Toolbar(string state)
    {
        if (state == "full-properties-rectangle-filled")
        {
            return Drawing(Tool.Rectangle) with
            {
                Setup = model =>
                {
                    model.ShowToolbar();
                    model.Choose(Tool.Rectangle);
                    model.ApplyStyle((ref Style style) => style.SetFillEnabled(true));
                },
            };
        }
        if (state == "full-properties-image")
        {
            // Paste image is an action, as on the Mac: the Mac capture sets the tool directly, which no call does, so Windows
            // pastes an image from the clipboard, and its properties show for the selected image.
            return Drawing(Tool.Image) with { ClipboardHasImage = true };
        }
        return state switch
        {
            "expanded-horizontal-properties" => Drawing(Tool.Pen),
            "expanded-horizontal-tools-only" or "draw-tools-only" => Drawing(Tool.Pen) with { Profile = ToolsOnly },
            "expanded-vertical-properties" => Drawing(Tool.Pen) with { Profile = Vertical, Available = VerticalAvailable },
            "expanded-vertical-tools-only" => Drawing(Tool.Pen) with
            {
                Profile = profile => { Vertical(profile); ToolsOnly(profile); },
                Available = VerticalAvailable,
            },
            "minimal-horizontal-properties" => Drawing(Tool.Pen) with { Profile = Minimal },
            "minimal-horizontal-tools-only" => Drawing(Tool.Pen) with { Profile = profile => { Minimal(profile); ToolsOnly(profile); } },
            "minimal-vertical-properties" => Drawing(Tool.Pen) with
            {
                Profile = profile => { Minimal(profile); Vertical(profile); },
                Available = VerticalAvailable,
            },
            "minimal-vertical-tools-only" => Drawing(Tool.Pen) with
            {
                Profile = profile => { Minimal(profile); Vertical(profile); ToolsOnly(profile); },
                Available = VerticalAvailable,
            },
            "fit-narrow" => Drawing(Tool.Arrow) with { Available = new Size(400, 600) },
            "fit-short" => Drawing(Tool.Arrow) with { Available = new Size(648, 230) },
            "off" => new ReferenceScenario(ReferenceSurface.Toolbar) { Setup = model => model.ShowToolbar() },
            "use-apps" => Drawing(Tool.Pen) with
            {
                Setup = model =>
                {
                    model.ShowToolbar();
                    model.Choose(Tool.Pen);
                    model.UseApps();
                },
            },
            "board-whiteboard" => Drawing(Tool.Pen) with { Setup = model => ShowBoard(model, Workspace.Board.Whiteboard) },
            "board-blackboard" => Drawing(Tool.Pen) with { Setup = model => ShowBoard(model, Workspace.Board.Blackboard) },
            "shared-style" => Drawing(Tool.Rectangle) with { Profile = profile => profile.Preferences.ShareToolStyles = true },
            "selection-single" => Selection(Tool.Rectangle),
            "selection-multiple" => Selection(Tool.Rectangle, Tool.Ellipse),
            "selection-locked" => Selection(Tool.Diamond),
            "added-buttons" => Drawing(Tool.Pen) with
            {
                Available = new Size(1600, 900),
                Profile = profile => profile.Preferences.ToolbarItems =
                [
                    .. WorkspacePreferences.DefaultToolbar.Select(tool => ShortcutCommand.ForTool(tool).Id),
                    "action.spotlight", "action.cursorHalo", "action.pointerLens", "action.smartDraw", "action.shareRegion",
                    "action.focusMenu", "action.zoomMenu", "tool.blur", "tool.pixelate",
                ],
            },
            // UXReferenceCaptureTests: the Blur tool, then reportCaptureFailure(.streamStopped("Synthetic reference fixture")).
            "capture-failure" => Drawing(Tool.Blur) with
            {
                Setup = model =>
                {
                    model.ShowToolbar();
                    model.Choose(Tool.Blur);
                    model.ReportCaptureFailure("Screen capture stopped: Synthetic reference fixture");
                },
            },
            "full-properties-vertical-arrow" => Drawing(Tool.Arrow) with
            {
                Profile = profile => { Vertical(profile); PropertiesShown(profile); },
                Available = VerticalAvailable,
            },
            "minimal-full-properties-arrow" => Drawing(Tool.Arrow) with { Profile = profile => { Minimal(profile); PropertiesShown(profile); } },
            // UXReferenceCaptureTests: full properties with every choice side by side (Settings > Your properties).
            "full-properties-all-choices-rectangle" => AllChoices(Tool.Rectangle, fill: true, "pattern", "roughness", "fillStyle", "edges"),
            "full-properties-all-choices-arrow" => AllChoices(Tool.Arrow, fill: false, "pattern", "roughness", "arrowType"),
            "full-properties-all-choices-text" => AllChoices(Tool.Text, fill: false, "font", "alignment"),
            "full-properties-all-choices-pen" => AllChoices(Tool.Pen, fill: false, "pressure"),
            // Buttons turned off in Settings: Undo, Redo, Settings and Hide, then every one of the toolbar's own buttons.
            "hidden-buttons" => Drawing(Tool.Pen) with
            {
                Profile = profile => profile.Preferences.HiddenToolbarControls = ["undo", "redo", "settings", "hide"],
            },
            "hidden-buttons-all" => Drawing(Tool.Pen) with
            {
                Profile = profile => profile.Preferences.HiddenToolbarControls = [.. ToolbarControl.AllCases.Select(control => control.RawValue)],
            },
            _ when state.StartsWith("collapsed-", StringComparison.Ordinal) => Collapsed(state["collapsed-".Length..]),
            _ when state.StartsWith("full-properties-", StringComparison.Ordinal) &&
                   Tool.FromRawValue(state["full-properties-".Length..]) is { } tool => Drawing(tool) with { Profile = PropertiesShown },
            _ => null,
        };
    }

    /// <summary>The Mac capture's plain model (tools hidden) or drawing model; release states hide the Local build badge.</summary>
    private static ReferenceScenario? Menu(string state) => state switch
    {
        "developer-preview" => new ReferenceScenario(ReferenceSurface.Flyout),
        "updates-unavailable" => new ReferenceScenario(ReferenceSurface.Flyout) { UpdatesUnavailable = true },
        "update-available" => new ReferenceScenario(ReferenceSurface.Flyout) { DeveloperPreview = false, UpdateAvailable = true },
        "tools-hidden" => new ReferenceScenario(ReferenceSurface.Flyout) { DeveloperPreview = false },
        "drawing" => new ReferenceScenario(ReferenceSurface.Flyout)
        {
            DeveloperPreview = false,
            Setup = model =>
            {
                model.ShowToolbar();
                model.Choose(Tool.Pen);
            },
        },
        "using-apps" => new ReferenceScenario(ReferenceSurface.Flyout)
        {
            DeveloperPreview = false,
            Setup = model =>
            {
                model.ShowToolbar();
                model.Choose(Tool.Pen);
                model.UseApps();
            },
        },
        "status-item" => new ReferenceScenario(ReferenceSurface.TrayIcon),
        "status-item-sharing" => new ReferenceScenario(ReferenceSurface.TrayIcon) { SharingOn = true },
        _ => null,
    };

    /// <summary>
    /// UXReferenceCaptureTests' region sharing states: the Share a region window with Left half (the default), Top right, or
    /// Top right after a custom region smaller than 32 points; the outline while choosing and while sharing; the output
    /// covered while it starts.
    /// </summary>
    private static ReferenceScenario? Sharing(string state) => state switch
    {
        "chooser-leftHalf" => new ReferenceScenario(ReferenceSurface.RegionSharing),
        "chooser-topRight" => new ReferenceScenario(ReferenceSurface.RegionSharing) { SharingSetup = sharing => sharing.ChoosePreset(ShareRegionPreset.TopRight) },
        "chooser-invalid" => new ReferenceScenario(ReferenceSurface.RegionSharing)
        {
            SharingSetup = sharing =>
            {
                sharing.ChoosePreset(ShareRegionPreset.TopRight);
                sharing.ApplyCustom(new Rect(2560, 0, 20, 20));
            },
        },
        "outline-choosing" => new ReferenceScenario(ReferenceSurface.RegionSharing) { SharingPart = RegionSharingPart.OutlineChoosing },
        "outline-live" => new ReferenceScenario(ReferenceSurface.RegionSharing) { SharingPart = RegionSharingPart.OutlineLive },
        "output-covered" => new ReferenceScenario(ReferenceSurface.RegionSharing) { SharingPart = RegionSharingPart.OutputCovered },
        _ => null,
    };

    private static ReferenceScenario Unavailable(string reason) => new(ReferenceSurface.Toolbar) { Unavailable = reason };

    /// <summary>UXReferenceCaptureTests' full-properties-all-choices states: 1600 points available, the choices shown side by side.</summary>
    private static ReferenceScenario AllChoices(Tool tool, bool fill, params string[] choices) => Drawing(tool) with
    {
        Available = new Size(1600, 900),
        Profile = profile =>
        {
            PropertiesShown(profile);
            profile.Preferences.ExpandedPropertyChoices = [.. choices];
        },
        Setup = model =>
        {
            model.ShowToolbar();
            model.Choose(tool);
            if (fill)
            {
                model.ApplyStyle((ref Style style) => style.SetFillEnabled(true));
            }
        },
    };

    /// <summary>
    /// UXReferenceCaptureTests' collapsed-&lt;size&gt;-&lt;orientation&gt;-&lt;draw|apps&gt; states: the toolbar shrunk to one button
    /// after the Shrink delay, with Draw or Apps.
    /// </summary>
    private static ReferenceScenario? Collapsed(string name)
    {
        string[] parts = name.Split('-');
        if (parts.Length != 3 || ToolbarSize.FromRawValue(parts[0]) is not { } size || parts[1] is not ("horizontal" or "vertical")
            || parts[2] is not ("draw" or "apps"))
        {
            return null;
        }
        bool vertical = parts[1] == "vertical";
        bool apps = parts[2] == "apps";
        return Drawing(Tool.Pen) with
        {
            Profile = profile =>
            {
                if (size == ToolbarSize.Minimal)
                {
                    Minimal(profile);
                }
                if (vertical)
                {
                    Vertical(profile);
                }
                profile.Preferences.ToolbarCollapseDelay = 5;
            },
            Setup = model =>
            {
                model.ShowToolbar();
                model.Choose(Tool.Pen);
                if (apps)
                {
                    model.UseApps();
                }
                model.SetToolbarCollapsed(true);
            },
        };
    }

    private static ReferenceScenario Drawing(Tool tool) => new(ReferenceSurface.Toolbar)
    {
        Setup = model =>
        {
            model.ShowToolbar();
            model.Choose(tool);
        },
    };

    private static void ToolsOnly(ReferenceProfile profile) => profile.Preferences.QuickPreferencesExpanded = false;

    /// <summary>The properties shown (Mac setPropertiesShown(true)).</summary>
    private static void PropertiesShown(ReferenceProfile profile)
    {
        profile.Preferences.InspectorPinned = true;
        profile.Preferences.QuickPreferencesExpanded = true;
    }

    private static void Vertical(ReferenceProfile profile) => profile.Preferences.ToolbarOrientation = ToolbarOrientation.Vertical;

    private static void Minimal(ReferenceProfile profile) => profile.Preferences.ToolbarSize = ToolbarSize.Minimal;

    private static void ShowBoard(WorkspaceModel model, Board board)
    {
        model.ShowToolbar();
        model.Choose(Tool.Pen);
        model.SetBoard(board);
    }

    /// <summary>
    /// The Mac capture's sample shapes (UXReferenceCaptureTests.addSampleScene) drawn and selected with Select, submitted
    /// the way a canvas submits an edit: an "API" box, a blue oval and a locked diamond.
    /// </summary>
    private static ReferenceScenario Selection(params Tool[] tools) => new(ReferenceSurface.Toolbar)
    {
        Setup = model =>
        {
            model.ShowToolbar();
            model.Choose(Tool.Select);
            WorkspaceSnapshot snapshot = model.Snapshot;
            DisplaySnapshot display = snapshot.Display(snapshot.ActiveDisplay ?? throw new InvalidOperationException("No display is drawing."))!;
            CanvasDocument document = display.Document;
            var box = new Element(Tool.Rectangle, new Rect(120, 110, 220, 130), style: Style.Defaults(Tool.Rectangle));
            var oval = new Element(Tool.Ellipse, new Rect(420, 120, 190, 110), style: Style.Defaults(Tool.Ellipse));
            oval.Style.Color = new RGBA(0.20, 0.55, 0.95);
            var diamond = new Element(Tool.Diamond, new Rect(650, 300, 120, 110), style: Style.Defaults(Tool.Diamond)) { Locked = true };
            foreach (Element element in new[] { box, oval, diamond })
            {
                document.Add(element);
            }
            document.Select(tools.Select(tool => tool switch
            {
                Tool.Rectangle => box.Id,
                Tool.Ellipse => oval.Id,
                _ => diamond.Id,
            }));
            if (model.Submit(new EditSubmission(new DisplayEdit(display.Id, display.Version, document))) is EditResult.Rejected rejected)
            {
                throw new InvalidOperationException($"The reference objects were refused: {rejected.Reason}.");
            }
        },
    };
}
