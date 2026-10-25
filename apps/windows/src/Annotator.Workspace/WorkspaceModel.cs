using System.Collections.Immutable;
using Annotator.Core;
using Annotator.Workspace.Shortcuts;

namespace Annotator.Workspace;

/// <summary>Inputs of a <see cref="WorkspaceModel"/>.</summary>
public sealed record WorkspaceOptions
{
    /// <summary>Reads the connected displays now (the Mac's desktopDisplays). It may throw; the model reports it.</summary>
    public required Func<IReadOnlyList<DisplayDescriptor>> Displays { get; init; }

    /// <summary>The pointer position in desktop units, or null when unknown.</summary>
    public Func<Point?> Pointer { get; init; } = static () => null;

    public IWorkspacePreferencesStore PreferencesStore { get; init; } = new MemoryPreferencesStore();

    /// <summary>The clipboard for Copy, Cut and Paste (WIN-CORE-07); null leaves those keys to other handlers.</summary>
    public IWorkspaceClipboard? Clipboard { get; init; }

    /// <summary>
    /// False while the host has no text editor (the Windows Text Services Framework editor of spike S-7 is not built
    /// yet): the Text tool cannot be chosen and says why (<see cref="WorkspaceModel.TextUnavailableMessage"/>).
    /// </summary>
    public bool TextEditingAvailable { get; init; } = true;

    /// <summary>
    /// Measures text on this platform (D-4). With it, a change of text size, font or weight resizes the selected text
    /// objects to their new text, as the Mac's applyStyle does; without it their frames stay unchanged.
    /// </summary>
    public Annotator.Core.Geometry.ITextMeasurer? TextMeasurer { get; init; }

    /// <summary>
    /// True when the host animates showing and hiding drawings and tools (spec 009, WIN-MOT-02): hiding and showing then
    /// publish a <see cref="VisibilityTransition"/>, and the host calls
    /// <see cref="WorkspaceModel.CompleteVisibilityTransition"/> when its motion ends. False shows and hides at once.
    /// </summary>
    public bool AnimatesVisibility { get; init; }

    /// <summary>Reads Reduce Motion now (Windows: Animation effects off, WIN-MOT-03); motion then becomes a short fade.</summary>
    public Func<bool> ReducesMotion { get; init; } = static () => false;

    /// <summary>
    /// Runs a callback on the model's thread after a delay (the end of an Alt+scroll gesture, the zoom start deadline). Without
    /// it nothing is scheduled: a scroll gesture ends with the next other change, and a zoom start waits for its first frame.
    /// </summary>
    public Zoom.WorkspaceTimer? Timer { get; init; }

    /// <summary>What each display's overlay shows of zoom (the displayed factor and a zoom-out ramp), or null without overlays.</summary>
    public Zoom.DisplayedZoomBoard? DisplayedZoom { get; init; }

    /// <summary>
    /// Windows' screen access for pixel features (WIN-CAP-04): the packaged app's answers and its explanation, which asks
    /// Windows. Null needs none, as a development build, tests and reference states.
    /// </summary>
    public Capture.IScreenAccess? ScreenAccess { get; init; }
}

/// <summary>The outcome of <see cref="WorkspaceModel.Choose"/>.</summary>
public enum ToolChoice
{
    Chosen,

    /// <summary>
    /// The tool's feature does not exist in this build, or the host cannot edit text
    /// (<see cref="WorkspaceOptions.TextEditingAvailable"/>).
    /// </summary>
    Unavailable,

    /// <summary>
    /// An effect waits for screen access (WIN-CAP-04): it is chosen once Windows allows it, or not at all after a refusal,
    /// whose words the toolbar's warning and Settings show.
    /// </summary>
    NeedsScreenAccess,
}

/// <summary>
/// Port of the Mac WorkspaceModel's workspace state: one document per display, the Off, Use apps and Draw
/// modes with Show / hide, Resume drawing and Hide only, the tool and its saved styles, ink colors, and
/// coordinated history across displays. It lives on one thread (the app thread): call every member there,
/// except <see cref="Snapshot"/>, which any thread may read. Each change publishes an immutable
/// <see cref="WorkspaceSnapshot"/>; canvases on other threads edit copies of snapshot documents and submit
/// the results (<see cref="Submit"/>), which the model accepts only at the version they started from.
/// </summary>
public sealed partial class WorkspaceModel
{
    private const string DrawStatus = "Draw on any display. Escape returns to Interact.";
    private const string UseAppsStatus = "Interact: your apps receive clicks; drawings remain visible.";
    private const string HistoryChangedMessage =
        "Another display's drawing or history changed. No display was changed by this Undo or Redo.";
    private const string HistoryBudgetMessage =
        "This change would exceed the Undo history limit. Your drawings have not changed. Save your boards first.";
    private static readonly string OtherDisplayEditingMessage =
        CanvasError.InvalidDocument("Finish the edit on the other display before moving these objects.").Message;

    /// <summary>What the Text tool and double-click text editing say while the host has no text editor.</summary>
    public const string TextUnavailableMessage = "Text is not ready on Windows yet. Use the pen, shapes and arrows for now.";

    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly Func<IReadOnlyList<DisplayDescriptor>> _displaySource;
    private readonly Func<Point?> _pointer;
    private readonly IWorkspacePreferencesStore _store;
    private readonly IWorkspaceClipboard? _clipboard;
    private readonly bool _textEditing;
    private readonly Annotator.Core.Geometry.ITextMeasurer? _textMeasurer;
    private readonly bool _animatesVisibility;
    private readonly Func<bool> _reducesMotion;
    private readonly Zoom.WorkspaceTimer? _timer;
    private readonly Zoom.DisplayedZoomBoard? _displayedZoom;
    private readonly Dictionary<DisplayId, Canvas> _canvases = [];
    private readonly Dictionary<Guid, HistoryEntry> _workspaceHistory = [];
    private List<Canvas> _styleSessions = [];
    private Canvas? _active;
    private PasteCascade? _pasteCascade;
    private WorkspaceMode _mode;
    private bool _hidden;
    private bool _toolbarVisible;
    private bool _resumeAfterShowing;
    private VisibilityTransition? _transition;
    private ulong _transitionSerial;
    private bool _revealDrawingRequested;
    private Tool _tool = Tool.Pen;
    private Style _style;
    private WorkspacePreferences _preferences = new();
    private bool _workspaceWide;
    private string _status;
    private string? _errorMessage;
    private FocusRequest? _focus;
    private ulong _focusSerial;
    /// <summary>How many times Undo took something back (Mac undoSerial), for the tutorial's Undo step.</summary>
    private ulong _undoSerial;
    private ImmutableArray<GlobalShortcutState> _shortcuts;
    private uint _epoch;
    private ulong _revision;
    private int _depth;
    private WorkspaceSnapshot _snapshot;

    public WorkspaceModel(WorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Displays);
        ArgumentNullException.ThrowIfNull(options.Pointer);
        ArgumentNullException.ThrowIfNull(options.PreferencesStore);
        ArgumentNullException.ThrowIfNull(options.ReducesMotion);
        _displaySource = options.Displays;
        _pointer = options.Pointer;
        _store = options.PreferencesStore;
        _clipboard = options.Clipboard;
        _textEditing = options.TextEditingAvailable;
        _textMeasurer = options.TextMeasurer;
        _animatesVisibility = options.AnimatesVisibility;
        _reducesMotion = options.ReducesMotion;
        _timer = options.Timer;
        _displayedZoom = options.DisplayedZoom;
        _screenAccessHost = options.ScreenAccess;
        _screenAccess = _screenAccessHost?.Check() ?? Capture.ScreenAccess.NotNeeded;
        _style = Style.Defaults(_tool);
        try
        {
            if (_store.Load() is { } data)
            {
                _preferences = WorkspacePreferences.Read(data);
                _style = StyleFor(_tool);
            }
            else
            {
                // Mac: a brand-new profile starts drawing tools with a gradient. It is saved with the first change, so a
                // first install still reads as one.
                _preferences = WorkspacePreferences.FirstUse;
                _style = StyleFor(_tool);
            }
        }
        catch (Exception error) when (IsReportable(error))
        {
            _errorMessage = $"Saved settings were not loaded: {Describe(error)}";
        }
        _status = _errorMessage ?? $"Ready. {WindowsShortcuts.Label(GlobalShortcutAction.ShowHide, _preferences)} opens drawing.";
        _shortcuts = [.. Enum.GetValues<GlobalShortcutAction>().Select(action =>
            new GlobalShortcutState(action, WindowsShortcuts.Label(action, _preferences), GlobalShortcutStatus.NotRegistered, null))];
        _snapshot = BuildSnapshot(0);
    }

    /// <summary>Raised on the model's thread after every change, with the new snapshot.</summary>
    public event Action<WorkspaceSnapshot>? Published;

    /// <summary>The latest snapshot. Safe to read from any thread.</summary>
    public WorkspaceSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// False for tools whose feature is not built yet; every tool is built now. Cover stands for Effects, which chooses the
    /// last effect (<see cref="PreferredEffect(in WorkspacePreferences)"/>), as the toolbar no longer makes solid covers, and
    /// Paste image pastes the clipboard's image (<see cref="Choose"/>).
    /// </summary>
    public static bool IsAvailable(Tool tool) => Enum.IsDefined(tool);

    /// <summary>Shows a message to the person, as the Mac's report(error) does.</summary>
    public void Report(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        using ChangeScope scope = Change();
        _errorMessage = message;
        _status = message;
    }

    /// <summary>Dismisses the current error message; the status line stays.</summary>
    public void DismissError()
    {
        using ChangeScope scope = Change();
        _errorMessage = null;
    }

    /// <summary>Records the outcome of registering a global shortcut (WIN-INP-05). Failures are reported to the person.</summary>
    public void SetGlobalShortcutState(GlobalShortcutState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using ChangeScope scope = Change();
        _shortcuts = [.. _shortcuts.Where(existing => existing.Action != state.Action), state];
        _shortcuts = [.. _shortcuts.OrderBy(existing => existing.Action)];
        if (state.Action == GlobalShortcutAction.ShowHide && state.Status == GlobalShortcutStatus.Registered)
        {
            _status = $"Ready. {state.Label} opens drawing or hides the tools.";
        }
        if (state.Status is GlobalShortcutStatus.AlreadyUsed or GlobalShortcutStatus.Refused && state.Message is { } message)
        {
            _errorMessage = message;
            _status = message;
        }
    }

    internal static bool IsReportable(Exception error) =>
        error is CanvasError or DecodingError or WorkspaceFailure or IOException or UnauthorizedAccessException
            or System.Security.SecurityException or Annotator.Core.Geometry.RenderFailure;

    internal static string Describe(Exception error) => error is CanvasError canvas ? canvas.ErrorDescription : error.Message;

    private void Report(Exception error)
    {
        _errorMessage = Describe(error);
        _status = _errorMessage;
    }

    private Style StyleFor(Tool tool) =>
        _preferences.ToolStyles.TryGetValue(tool.RawValue, out Style style) ? style : Style.Defaults(tool);

    private DocumentVersion FreshVersion() => DocumentVersion.Fresh(++_epoch);

    private void PersistPreferences()
    {
        try
        {
            _store.Save(_preferences.Data());
        }
        catch (Exception error) when (IsReportable(error))
        {
            Report(error);
        }
    }

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
        {
            throw new InvalidOperationException("WorkspaceModel must be used on the thread that created it (the app thread).");
        }
    }

    private ChangeScope Change()
    {
        VerifyAccess();
        _depth++;
        return new ChangeScope(this);
    }

    private void EndChange()
    {
        if (--_depth > 0)
        {
            return;
        }
        WorkspaceSnapshot snapshot = BuildSnapshot(++_revision);
        Volatile.Write(ref _snapshot, snapshot);
        Published?.Invoke(snapshot);
    }

    internal readonly struct ChangeScope(WorkspaceModel model) : IDisposable
    {
        public void Dispose() => model.EndChange();
    }

    private delegate void DocumentChange(ref CanvasDocument document);

    /// <summary>Changes a display's document on a copy; a failure leaves it unchanged. Gives the display a fresh version.</summary>
    private void Mutate(Canvas canvas, DocumentChange change)
    {
        CanvasDocument document = canvas.Document;
        change(ref document);
        canvas.Document = document;
        canvas.Version = FreshVersion();
    }

    private sealed class Canvas(DisplayDescriptor display, CanvasDocument document, DocumentVersion version)
    {
        public DisplayDescriptor Display = display;
        public CanvasDocument Document = document;
        public DocumentVersion Version = version;
        public bool TakesInput;
        public PendingEdit? Edit;
        public Board Board;
        public ulong Refused;

        /// <summary>The host shows effect regions on this display (the document's, or one being drawn).</summary>
        public bool EffectRegions;

        /// <summary>Mac effectsReady: those regions show live pixels.</summary>
        public bool EffectsReady;

        /// <summary>Increases whenever zoom here stops without motion (another display took over, a suspension or a failure).</summary>
        public ulong PresentationCut;

        /// <summary>The presentation mode the last synchronization saw, so a lens or zoom that starts can end a capture pause.</summary>
        public PresentationMode SynchronizedPresentation;
    }

    /// <param name="TextStyle">The style a text edit reported for the toolbar (<see cref="CanvasCommandKind.TextStyle"/>).</param>
    /// <param name="Label">
    /// The shape label of a text edit, as the canvas reported it or as the toolbar changed it since: a paint change stops it
    /// following its shape's stroke, and Match outline makes it follow again. Null for a text object.
    /// </param>
    private sealed record PendingEdit(DocumentVersion Base, EditKind Kind, CanvasDocument? Interim, Style? TextStyle = null, LabelEdit? Label = null);

    private sealed record HistoryEntry(ImmutableHashSet<Guid> Documents, bool GlobalSelection);
}

/// <summary>A workspace operation failed with a message for the person (Mac WorkspaceHistoryFailure).</summary>
public sealed class WorkspaceFailure(string message) : Exception(message);
