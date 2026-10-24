using System.Collections.Immutable;
using Annotator.Core;

namespace Annotator.Workspace;

/// <summary>The workspace modes of specification 002. Stable raw values match the Mac.</summary>
public enum WorkspaceMode
{
    /// <summary>No drawing session exists yet; overlays are hidden.</summary>
    Off,

    /// <summary>Use apps (the Mac's Interact): drawings stay visible and apps receive every click.</summary>
    UseApps,

    /// <summary>Every display's canvas takes pointer input.</summary>
    Draw,
}

public static class WorkspaceModeExtensions
{
    extension(WorkspaceMode mode)
    {
        /// <summary>The Mac's raw value: Off, Interact or Draw.</summary>
        public string RawValue => mode switch
        {
            WorkspaceMode.Off => "Off",
            WorkspaceMode.UseApps => "Interact",
            WorkspaceMode.Draw => "Draw",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }
}

/// <summary>What the desktop overlays should do, derived from the mode and visibility.</summary>
public enum SurfaceMode
{
    /// <summary>Overlays are hidden (Off, or drawings and tools are hidden).</summary>
    Hidden,

    /// <summary>Overlays are visible and click-through on every display at once (WIN-OVR-02).</summary>
    UseApps,

    /// <summary>Overlays are visible and take input on every display.</summary>
    Draw,
}

/// <summary>An edit in progress on a canvas, as the canvas reported it.</summary>
public enum EditKind
{
    /// <summary>A pointer gesture or path; interruptions cancel it.</summary>
    Gesture,

    /// <summary>Text editing; interruptions commit the last text the canvas reported (Mac commitTextIfNeeded).</summary>
    Text,

    /// <summary>
    /// A clicked arrow or line being built. Choosing a tool finishes it with the last document the canvas
    /// reported (Mac finishLinearPath); other interruptions cancel it.
    /// </summary>
    Path,
}

/// <summary>A request to give keyboard focus to a display's canvas: entering Draw, or choosing a tool while drawing (WIN-OVR-03).</summary>
/// <param name="Serial">Increases with every request; act on each serial once.</param>
public sealed record FocusRequest(ulong Serial, DisplayId Display);

/// <summary>
/// Drawings and tools are being shown or hidden with motion (spec 009, Mac <c>visibilityAnimating</c>). The host animates
/// snapshots of the drawings and the toolbar in <see cref="Style"/> and calls
/// <see cref="WorkspaceModel.CompleteVisibilityTransition"/> with <see cref="Serial"/> when the motion ends; a newer
/// transition (a reversal) replaces it. While it runs, the workspace stays hidden (<see cref="WorkspaceSnapshot.WorkspaceHidden"/>)
/// and no canvas takes input.
/// </summary>
/// <param name="Serial">Increases with every transition.</param>
/// <param name="Showing">True when showing (the Mac's targetVisible).</param>
/// <param name="Style">The resolved style; Reduce Motion has already made it Fade.</param>
/// <param name="RevealDrawing">A show that ends in Draw: drawing state was prepared before snapshots, so selection adornments show in them.</param>
public sealed record VisibilityTransition(ulong Serial, bool Showing, Annotator.Core.VisibilityAnimationStyle Style, bool RevealDrawing);

/// <summary>The global shortcuts (WIN-INP-05).</summary>
public enum GlobalShortcutAction
{
    ShowHide,
    ResumeDrawing,
    HideOnly,
    LiveZoom,
    BreakTimer,
}

public enum GlobalShortcutStatus
{
    /// <summary>Registration was not attempted, for example because the feature does not exist yet.</summary>
    NotRegistered,

    Registered,

    /// <summary>Another app, or Windows, already uses the chord; the action has no shortcut.</summary>
    AlreadyUsed,

    /// <summary>The chord cannot be a global shortcut; the action has no shortcut.</summary>
    Refused,
}

/// <summary>The user-visible state of one global shortcut. <paramref name="Message"/> explains a failure.</summary>
public sealed record GlobalShortcutState(GlobalShortcutAction Action, string Label, GlobalShortcutStatus Status, string? Message);

/// <summary>The board behind a display's drawings (spec 009): the live desktop, a whiteboard or a blackboard.</summary>
public enum Board
{
    /// <summary>The Mac's transparent board: drawings float over the desktop.</summary>
    Desktop,

    Whiteboard,

    Blackboard,
}

public static class BoardExtensions
{
    extension(Board board)
    {
        /// <summary>The Mac's raw value, also the name people see: Desktop, Whiteboard or Blackboard.</summary>
        public string RawValue => board switch
        {
            Board.Desktop => "Desktop",
            Board.Whiteboard => "Whiteboard",
            Board.Blackboard => "Blackboard",
            _ => throw new ArgumentOutOfRangeException(nameof(board)),
        };

        /// <summary>The next board of the toolbar's one-click cycle: Desktop, Whiteboard, Blackboard, then Desktop.</summary>
        public Board Next => board switch
        {
            Board.Desktop => Board.Whiteboard,
            Board.Whiteboard => Board.Blackboard,
            _ => Board.Desktop,
        };
    }
}

/// <summary>One display in a <see cref="WorkspaceSnapshot"/>. Immutable; safe to read on any thread.</summary>
public sealed class DisplaySnapshot
{
    private readonly CanvasDocument _document;

    internal DisplaySnapshot(DisplayDescriptor display, CanvasDocument document, DocumentVersion version,
                             bool takesInput, bool isActive, EditKind? activeEdit, Board board = Board.Desktop,
                             ulong refusedEdits = 0, Style? textStyle = null, ulong presentationCut = 0, LabelEdit? textLabel = null)
    {
        PresentationCut = presentationCut;
        Display = display;
        _document = document;
        Version = version;
        TakesInput = takesInput;
        IsActive = isActive;
        ActiveEdit = activeEdit;
        Board = board;
        RefusedEdits = refusedEdits;
        TextStyle = textStyle;
        TextLabel = textLabel;
    }

    public DisplayDescriptor Display { get; }

    public DisplayId Id => Display.Id;

    /// <summary>
    /// A copy of the display's document. Its contents are immutable; mutating the copy changes only the
    /// caller's copy, which is how a canvas builds an edit to submit.
    /// </summary>
    public CanvasDocument Document => _document;

    /// <summary>The version a canvas edit on this display must start from.</summary>
    public DocumentVersion Version { get; }

    /// <summary>True in Draw: the display's canvas takes pointer input.</summary>
    public bool TakesInput { get; }

    /// <summary>The editing target: ordinary selection, Undo and Redo follow it (spec 002).</summary>
    public bool IsActive { get; }

    /// <summary>The edit a canvas reported as in progress here, if any.</summary>
    public EditKind? ActiveEdit { get; }

    /// <summary>The board drawn behind this display's drawings.</summary>
    public Board Board { get; }

    /// <summary>
    /// How many canvas edits sent from this display the workspace refused. A canvas with edits in flight that sees
    /// it grow drops its unconfirmed result and shows <see cref="Document"/> again.
    /// </summary>
    public ulong RefusedEdits { get; }

    /// <summary>
    /// The style of the text edit on this display (<see cref="CanvasCommandKind.TextStyle"/>), including changes made on the
    /// toolbar while it lasts; null without a text edit or before the canvas reported one.
    /// </summary>
    public Style? TextStyle { get; }

    /// <summary>
    /// The shape label of the text edit on this display (<see cref="CanvasCommand.Label"/>), including the toolbar's changes
    /// while it lasts (a paint change stops it following the stroke, Match outline makes it follow again); null for a text
    /// object, without a text edit or before the canvas reported one.
    /// </summary>
    public LabelEdit? TextLabel { get; }

    /// <summary>
    /// Increases whenever zoom on this display stopped without motion (Mac stopPresentationWithoutHistory with the
    /// presentation set off at once): another display became the editing target, the system suspended, or zoom failed.
    /// </summary>
    public ulong PresentationCut { get; }
}

/// <summary>
/// Everything the workspace publishes after a change. Immutable, so any thread may hold and read it; the
/// overlay thread renders from it and starts canvas edits from <see cref="DisplaySnapshot.Version"/>.
/// </summary>
public sealed class WorkspaceSnapshot
{
    internal WorkspaceSnapshot()
    {
    }

    /// <summary>Increases with every published change.</summary>
    public ulong Revision { get; internal init; }

    public WorkspaceMode Mode { get; internal init; }

    /// <summary>Drawings and tools are hidden (Hide).</summary>
    public bool WorkspaceHidden { get; internal init; }

    public bool ToolbarVisible { get; internal init; }

    /// <summary>The show or hide that is animating, or null (Mac <c>visibilityAnimating</c>).</summary>
    public VisibilityTransition? Visibility { get; internal init; }

    /// <summary>
    /// The Mac's drawingToolsArePresented: while drawings and tools animate, whether they are being shown; otherwise
    /// whether the toolbar is shown and the workspace is not hidden.
    /// </summary>
    public bool ToolsPresented => Visibility is { } transition ? transition.Showing : ToolbarVisible && !WorkspaceHidden;

    public SurfaceMode Surfaces => WorkspaceHidden || Mode == WorkspaceMode.Off ? SurfaceMode.Hidden
        : Mode == WorkspaceMode.Draw ? SurfaceMode.Draw : SurfaceMode.UseApps;

    public Tool Tool { get; internal init; }

    /// <summary>The tool whose style the toolbar edits: a uniform selection's tool, Select for a mixed one, else <see cref="Tool"/>.</summary>
    public Tool EffectiveTool { get; internal init; }

    /// <summary>The style the toolbar shows: the selection's style, or the tool's saved style.</summary>
    public Style Style { get; internal init; }

    /// <summary>
    /// Mac <c>editingShapeLabelID != nil</c>: the editing target's canvas edits a shape label, so the stroke color popover
    /// offers Match outline.
    /// </summary>
    public bool EditingShapeLabel { get; internal init; }

    /// <summary>Mac editingLabelFollowsStroke: that label follows its shape's stroke already, so Match outline is unavailable.</summary>
    public bool EditingLabelFollowsStroke { get; internal init; }

    public ImmutableArray<DisplaySnapshot> Displays { get; internal init; } = [];

    public DisplayId? ActiveDisplay { get; internal init; }

    public bool SelectionIsWorkspaceWide { get; internal init; }

    public int SelectionCount { get; internal init; }

    public bool SelectionIsLocked { get; internal init; }

    public bool SelectionSupportsFill { get; internal init; }

    public bool CanUndo { get; internal init; }

    public bool CanRedo { get; internal init; }

    /// <summary>
    /// How many times Undo took something back since launch (Mac undoSerial). Cancelling an edit in progress does not count.
    /// The tutorial's Undo step waits for it to grow.
    /// </summary>
    public ulong UndoSerial { get; internal init; }

    public bool CanClearCanvas { get; internal init; }

    public bool CanDeleteCurrent { get; internal init; }

    /// <summary>The Mac's delete button label, such as "Delete selection" or "Cancel current drawing".</summary>
    public string DeleteActionLabel { get; internal init; } = "Delete";

    /// <summary>The SF Symbol name the Mac shows for the delete action ("trash" or "xmark.circle").</summary>
    public string DeleteActionSymbol { get; internal init; } = "trash";

    /// <summary>Toolbar items in order, from the preferences (Mac toolbarItems).</summary>
    public ValueList<ShortcutCommand> ToolbarItems { get; internal init; }

    /// <summary>The latest status line (Mac status).</summary>
    public string Status { get; internal init; } = string.Empty;

    /// <summary>The latest failure message, or null (Mac errorMessage).</summary>
    public string? ErrorMessage { get; internal init; }

    public FocusRequest? FocusRequest { get; internal init; }

    public ImmutableArray<GlobalShortcutState> GlobalShortcuts { get; internal init; } = [];

    /// <summary>A copy of the preferences; immutable contents.</summary>
    public WorkspacePreferences Preferences { get; internal init; }

    /// <summary>The editing target's board (Mac board), or the board the first display gets.</summary>
    public Board Board { get; internal init; }

    /// <summary>Smart Draw turns finished pen strokes into shapes (the saved preference).</summary>
    public bool SmartDraw => Preferences.SmartDraw;

    /// <summary>
    /// False while the host has no text editor: the Text tool is shown disabled with
    /// <see cref="WorkspaceModel.TextUnavailableMessage"/> (<see cref="WorkspaceOptions.TextEditingAvailable"/>).
    /// </summary>
    public bool TextEditingAvailable { get; internal init; } = true;

    /// <summary>
    /// Why capture failed, for Settings (Mac captureFailureMessage), or null. Effect regions stay opaque meanwhile; an
    /// effect tool tries again.
    /// </summary>
    public string? CaptureFailureMessage { get; internal init; }

    /// <summary>
    /// Mac captureVerified: capture delivered a frame since it last failed, so Settings > Permissions says it works. Allowed
    /// and working stay separate states (spec 005, WIN-CAP-04).
    /// </summary>
    public bool CaptureVerified { get; internal init; }

    /// <summary>
    /// Mac DisplayCapture.hasPermission: whether Windows lets Annotator read screen pixels, as last read. A development build
    /// needs no access (<see cref="Capture.ScreenAccess.NotNeeded"/>); a packaged one needs the person's answers (WIN-CAP-04).
    /// </summary>
    public Capture.ScreenAccess ScreenAccess { get; internal init; }

    /// <summary>
    /// Mac censorFallbackActive: the editing target shows effect regions that capture cannot fill yet, so they are opaque.
    /// </summary>
    public bool CensorFallbackActive { get; internal init; }

    /// <summary>
    /// Increases whenever the workspace asks the host to resume live effects: an effect tool was chosen, the workspace
    /// showed again, or Settings retried (Mac <c>presentation?.retry()</c>). The host ends a capture pause and retries
    /// failed effects once per new value.
    /// </summary>
    public ulong EffectsRetry { get; internal init; }

    /// <summary>
    /// The toolbar's capture warning (Mac ToolbarView needsCaptureRecovery), or null: capture failed, or effect regions wait
    /// for capture.
    /// </summary>
    public string? CaptureWarning => ZoomStart.Phase == Zoom.ZoomStartPhase.Failed ? ZoomStart.Message
        : CaptureFailureMessage is not null ? WorkspaceModel.CaptureUnavailableWarning
        : CensorFallbackActive ? WorkspaceModel.CaptureCoveredWarning : null;

    /// <summary>Mac <c>spotlight</c>: the overlays dim everything but a circle around the pointer while the workspace shows.</summary>
    public bool Spotlight { get; internal init; }

    /// <summary>Mac <c>cursorHalo</c>: the overlays draw the highlight around the real pointer while the workspace shows.</summary>
    public bool CursorHalo { get; internal init; }

    /// <summary>
    /// Increases whenever Spotlight or Cursor halo is set (Mac updatePointerEffects): the overlays restart their pointer
    /// watching, which forgets the halo's last movement, click and click animation, as the Mac's stopPointerMonitors does.
    /// </summary>
    public ulong PointerEffectsSerial { get; internal init; }

    /// <summary>Mac cursorHighlightSettings: the saved Pointer settings, or the defaults.</summary>
    public CursorHighlightSettings CursorHighlight => Preferences.CursorHighlight ?? new CursorHighlightSettings();

    /// <summary>Mac zoomStartState: where a live zoom request is.</summary>
    public Zoom.ZoomStartState ZoomStart { get; internal init; }

    /// <summary>Mac screenZoomEnabled: the editing target's presentation is screen zoom.</summary>
    public bool ScreenZoomEnabled { get; internal init; }

    /// <summary>Mac magnifierEnabled: the editing target's presentation is the pointer lens.</summary>
    public bool MagnifierEnabled { get; internal init; }

    /// <summary>Mac magnification: the editing target's zoom, or the zoom the next lens or zoom starts with.</summary>
    public double Magnification { get; internal init; } = 2;

    /// <summary>Mac zoomGesturesAvailable: the Alt+scroll and Escape hooks run.</summary>
    public bool ZoomGesturesAvailable { get; internal init; }

    /// <summary>Mac zoomScrollStatus: whether Alt+scroll reaches zoom now, in the person's words.</summary>
    public string ZoomScrollStatus { get; internal init; } = WorkspaceModel.ZoomScrollNotConnected;

    /// <summary>The latest animated start of screen zoom (Mac screenZoomEntrance), or null.</summary>
    public ZoomEntrance? ZoomEntrance { get; internal init; }

    /// <summary>Increases with every Alt+scroll that changed zoom, so an entrance ramp eases on toward the new zoom.</summary>
    public ulong ZoomScrollSerial { get; internal init; }

    /// <summary>Mac animatesScreenZoom: zoom ramps in and out unless Reduce Motion (Animation effects off) is on.</summary>
    public bool AnimatesScreenZoom { get; internal init; } = true;

    /// <summary>
    /// Screen zoom or the pointer lens is on, or a zoom request is not idle (Mac screenZoomEnabled, magnifierEnabled and
    /// zoomStartState): the tutorial and the coffee reminder wait until it ends.
    /// </summary>
    public bool ZoomActive => ScreenZoomEnabled || MagnifierEnabled || ZoomStart.Phase != Zoom.ZoomStartPhase.Idle;

    /// <summary>The zoom facts of the shortcut hint (Mac WorkspaceHint reads screenZoomEnabled, magnification and zoomScrollStatus).</summary>
    public Canvas.ZoomHint ZoomHint => new(ScreenZoomEnabled, Magnification, ZoomScrollStatus);

    /// <summary>The Live zoom shortcut's keycaps (the flyout's Stop zoom row and the toolbar).</summary>
    public string ZoomShortcutLabel { get; internal init; } = string.Empty;

    /// <summary>
    /// Region sharing (specification 012): its state for the tray, the toolbar and Tools, and what the output window shows.
    /// </summary>
    public Sharing.RegionSharingSnapshot RegionSharing { get; internal init; } = Sharing.RegionSharingSnapshot.Off;

    /// <summary>Mac isOn(_:): whether a toolbar button's feature is on.</summary>
    public bool IsOn(ShortcutAction action) => action switch
    {
        ShortcutAction.Spotlight => Spotlight,
        ShortcutAction.CursorHalo => CursorHalo,
        ShortcutAction.PointerLens => MagnifierEnabled,
        ShortcutAction.SmartDraw => SmartDraw,
        ShortcutAction.ShareRegion => RegionSharing.IsCapturing,
        _ => false,
    };

    public DisplaySnapshot? Display(DisplayId id)
    {
        foreach (DisplaySnapshot display in Displays)
        {
            if (display.Id == id)
            {
                return display;
            }
        }
        return null;
    }

    public GlobalShortcutState? Shortcut(GlobalShortcutAction action)
    {
        foreach (GlobalShortcutState state in GlobalShortcuts)
        {
            if (state.Action == action)
            {
                return state;
            }
        }
        return null;
    }
}
