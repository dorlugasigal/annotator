using System.Collections.Immutable;
using Annotator.Core;
using Annotator.Core.Scene;

namespace Annotator.Workspace.Canvas;

/// <summary>
/// One display's canvas interaction: the platform-neutral port of the Mac DrawingCanvas gestures, keys and context
/// menu. It lives on one thread (the overlay thread): call every member there. It reads immutable
/// <see cref="WorkspaceSnapshot"/>s, edits copies of its display's document and changes the workspace only through
/// <see cref="IWorkspaceChannel"/>. Until the workspace confirms an edit, the canvas keeps showing its own result, so
/// nothing flickers; a refused or interrupted edit falls back to the workspace's document.
/// </summary>
public sealed partial class CanvasController : IDisposable
{
    private readonly IWorkspaceChannel _channel;
    private readonly CanvasOptions _options;
    private SceneBuilder? _boundsBuilder;

    private WorkspaceSnapshot? _workspace;
    private DisplaySnapshot? _snapshot;
    private bool _inputBlocked;
    private bool _disposed;

    // The document as this canvas shows it: the latest snapshot's, or the result of edits in flight. _version is the
    // version the workspace will publish once it took them; _confirmed is the newest version a snapshot showed.
    private CanvasDocument _document;
    private DocumentVersion _version;
    private DocumentVersion _confirmed;
    private ulong _refused;
    private bool _hasDocument;

    // The edit in progress: a copy of the document with its transaction open (Mac session.document during a gesture).
    private bool _working;
    private CanvasDocument _work;
    private DocumentVersion _editBase;
    private EditKind? _reported;

    private Gesture? _gesture;
    private bool _hasDraft;
    private Element _draft;
    /// <summary>Simulated pressure for the pen stroke being drawn without a pressure-sensitive pen.</summary>
    private InkPressure? _inkPressure;
    private ImmutableHashSet<Guid> _eraseIds = ImmutableHashSet<Guid>.Empty;
    private Rect? _marquee;
    private ImmutableArray<Anchor> _attachHints = [];
    private Guid? _linearId;
    private Point? _linearPreview;
    private Action<Rect>? _regionRequest;
    private ElementList _dragPreview;

    private long _lastClickTime;
    private Point _lastClickPoint;
    private int _clickCount;
    private bool _hasLastClick;
    private Point? _pointer;

    private CanvasCursor _cursor = CanvasCursor.Arrow;
    private CanvasLiveState _state;
    private ulong _revision;
    private ElementList _shown;
    private bool _shownStale = true;

    public CanvasController(DisplayId display, IWorkspaceChannel channel, CanvasOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(display.Value, nameof(display));
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.TextMeasurer);
        ArgumentNullException.ThrowIfNull(options.Scheduler);
        Display = display;
        _channel = channel;
        _options = options;
        options.Coordinator?.Register(this);
    }

    public DisplayId Display { get; }

    /// <summary>The live state to draw. Read it after <see cref="Changed"/>; it is an immutable value.</summary>
    public CanvasLiveState State => _state;

    /// <summary>Raised on the canvas thread after every change of <see cref="State"/>, with what changed and where.</summary>
    public event Action<CanvasChange>? Changed;

    /// <summary>Raised when the canvas needs the app (files, the tool library, Settings, properties).</summary>
    public event Action<CanvasRequest>? Requested;

    /// <summary>Raised for a right-click or pen barrel click: show the menu and call <see cref="PerformMenuItem"/> with the choice.</summary>
    public event Action<CanvasMenu>? ContextMenuRequested;

    /// <summary>The canvas takes drawing input: Draw mode, shown, and its display is part of the workspace.</summary>
    public bool IsDrawing => _workspace is { Mode: WorkspaceMode.Draw, WorkspaceHidden: false } && _snapshot is { TakesInput: true };

    /// <summary>Mac hasActiveEdit: a gesture, text edit, clicked arrow or region choice is in progress.</summary>
    public bool HasActiveEdit => _gesture is not null || _text is not null || _linearId is not null || _regionRequest is not null;

    /// <summary>A text object or shape label is being edited.</summary>
    public bool HasTextEditor => _text is not null;

    /// <summary>Mac constructingArrow: a clicked arrow is being built.</summary>
    public bool ConstructingArrow => _linearId is not null;

    /// <summary>
    /// The document as this canvas shows it (Mac session.document): the latest snapshot's, with the edit in progress or
    /// in flight applied; null before the workspace prepared the display. An object being drawn is in
    /// <see cref="CanvasLiveState.Draft"/>, not here. While text is edited it holds the object whose text is being typed,
    /// selected, as the Mac synchronizes its document when its editor opens, though the canvas hides that object.
    /// </summary>
    public CanvasDocument? Document => _hasDocument ? Current : null;

    /// <summary>
    /// Takes the latest workspace state, as published (<see cref="WorkspaceModel.Published"/>). <paramref name="display"/>
    /// is this canvas's display in it, or null when the workspace has not prepared it. <paramref name="inputBlocked"/> is
    /// true while a flyout, menu or palette is open: the click that dismisses it must not draw.
    /// </summary>
    public void Update(WorkspaceSnapshot workspace, DisplaySnapshot? display, bool inputBlocked = false)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool wasDrawing = IsDrawing;
        Tool previousTool = _workspace?.Tool ?? Tool.Select;
        _workspace = workspace;
        _snapshot = display;
        _inputBlocked = inputBlocked;
        var kinds = CanvasChangeKinds.None;
        if (display is null)
        {
            if (_hasDocument)
            {
                DropLocal(cancelReportedEdit: false);
                _hasDocument = false;
                _document = default;
                kinds |= CanvasChangeKinds.Content | CanvasChangeKinds.Selection;
            }
        }
        else
        {
            bool refused = _hasDocument && display.RefusedEdits != _refused;
            _refused = display.RefusedEdits;
            DocumentVersion version = display.Version;
            if (!_hasDocument || refused || !InChain(version))
            {
                if (_hasDocument)
                {
                    DropLocal(cancelReportedEdit: !refused && display.ActiveEdit is not null);
                }
                _document = display.Document;
                _version = version;
                _confirmed = version;
                _hasDocument = true;
                kinds |= CanvasChangeKinds.Content | CanvasChangeKinds.Selection;
            }
            else if (version == _version)
            {
                // Everything this canvas sent is confirmed; the snapshot holds the same document.
                _confirmed = version;
                if (!_working)
                {
                    _document = display.Document;
                }
            }
            else
            {
                _confirmed = version;
            }
            if (_text is not null)
            {
                FollowWorkspaceTextStyle();
            }
        }
        if (!IsDrawing && HasActiveEdit && display is not null && _workspace.Mode != WorkspaceMode.Draw)
        {
            CancelGesture();
        }
        FollowLaser();
        if (_workspace.WorkspaceHidden || !IsDrawing)
        {
            FinishTransitions();
        }
        if (previousTool != workspace.Tool && _morph is not null && !MorphValid(_morph))
        {
            FinishSnapMorph();
        }
        if (wasDrawing != IsDrawing)
        {
            kinds |= CanvasChangeKinds.Input | CanvasChangeKinds.Selection | CanvasChangeKinds.Cursor;
        }
        if (!IsDrawing || (!workspace.Tool.IsConnector && _gesture is null && _linearId is null))
        {
            SetAttachHints([]);
        }
        _shownStale = true;
        RefreshCursor();
        Publish(kinds | CanvasChangeKinds.Content | CanvasChangeKinds.Adornments, null);
    }

    /// <summary>
    /// Mac cancelGesture: drops the gesture, text edit, clicked arrow or region choice without changing the document, and
    /// lets a held laser pointer stroke go, so it fades.
    /// </summary>
    public void CancelGesture()
    {
        ReleaseLaser(null);
        bool changed = HasActiveEdit || _marquee is not null || !_eraseIds.IsEmpty || _smartPreview is not null || _working;
        DropLocal(cancelReportedEdit: true);
        if (changed)
        {
            _shownStale = true;
            Publish(CanvasChangeKinds.Content | CanvasChangeKinds.Selection | CanvasChangeKinds.Adornments | CanvasChangeKinds.Edit
                    | CanvasChangeKinds.Text, null);
        }
    }

    /// <summary>
    /// Mac selectRegion: the next drag chooses a region and calls <paramref name="completion"/> with it in canvas points.
    /// Escape, Undo or any interruption cancels the choice without calling it.
    /// </summary>
    public void SelectRegion(Action<Rect> completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (!_hasDocument)
        {
            return;
        }
        CommitText();
        CancelGesture();
        BeginWork(EditKind.Gesture);
        _regionRequest = completion;
        Status("Drag a region. Escape cancels.");
        Publish(CanvasChangeKinds.Edit, null);
    }

    /// <summary>The canvas's accessibility action Focus drawing tools (Mac focusDrawingTools).</summary>
    public void FocusTools() => Request(CanvasRequest.FocusTools);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        CancelSmartPreview();
        CancelRecognition();
        _text = null;
        _options.Coordinator?.Unregister(this);
        _disposed = true;
    }

    private WorkspaceSnapshot Workspace => _workspace ?? throw new InvalidOperationException("The canvas has no workspace snapshot yet.");

    private DisplayDescriptor DisplayInfo => _snapshot?.Display ?? throw new InvalidOperationException("The canvas's display is not prepared.");

    /// <summary>The document gestures read and change (Mac session.document).</summary>
    private ref CanvasDocument Current => ref _working ? ref _work : ref _document;

    private bool CanStartEdit => IsDrawing && _hasDocument && !_inputBlocked;

    private bool InChain(DocumentVersion version) =>
        version.Value >> 32 == _version.Value >> 32 && version.Value >= _confirmed.Value && version.Value <= _version.Value;

    private Style StyleFor(Tool tool) =>
        Workspace.Preferences.ToolStyles.TryGetValue(tool.RawValue, out Style style) ? style : Style.Defaults(tool);

    /// <summary>The style new objects get (Mac model.style once the canvas is the editing target).</summary>
    private Style DrawingStyle => Workspace.SelectionCount == 0 ? Workspace.Style : StyleFor(Workspace.Tool);

    private double ScreenZoom => Current.Presentation.Mode == PresentationMode.Screen ? Current.Presentation.Zoom : 1;

    /// <summary>
    /// Starts an edit on a copy of the document. Like the Mac's activateDrawingSession, a canvas that becomes the editing
    /// target with a tool other than Select drops its selection first.
    /// </summary>
    private void BeginWork(EditKind? report)
    {
        _work = _document;
        _editBase = _version;
        _working = true;
        if (Workspace.ActiveDisplay != Display && Workspace.Tool != Tool.Select && !_work.Selection.IsEmpty)
        {
            _work.Select([]);
        }
        if (report is { } kind)
        {
            ReportEdit(kind);
        }
    }

    private void ReportEdit(EditKind kind)
    {
        _reported = kind;
        _channel.BeginEdit(Display, _editBase, kind);
    }

    /// <summary>Commits the edit and hands it to the workspace (Mac commit and synchronize); an unchanged document only ends the edit.</summary>
    private void SubmitWork(Tool? toolAfter = null, bool localSelection = true)
    {
        if (_work.IsEditing)
        {
            _work.Commit();
        }
        if (toolAfter is not null || !SameDocument(_work, _document))
        {
            _channel.Submit(new EditSubmission(new DisplayEdit(Display, _editBase, _work))
            {
                ToolAfter = toolAfter,
                UsesLocalSelection = localSelection,
                Origin = Display,
            });
            _document = _work;
            _version = _editBase.Next;
        }
        else if (_reported is not null)
        {
            _channel.CancelEdit(Display, _editBase);
        }
        _working = false;
        _work = default;
        _reported = null;
        _shownStale = true;
    }

    /// <summary>Ends the edit without a change (Mac document.cancel).</summary>
    private void CancelWork()
    {
        if (_reported is not null)
        {
            _channel.CancelEdit(Display, _editBase);
        }
        _working = false;
        _work = default;
        _reported = null;
        _shownStale = true;
    }

    private static bool SameDocument(in CanvasDocument left, in CanvasDocument right) =>
        left.Elements == right.Elements && left.Selection.SetEquals(right.Selection) && left.Presentation == right.Presentation;

    /// <summary>Ends every local edit without sending its result: after a refusal, an interruption or <see cref="CancelGesture"/>.</summary>
    private void DropLocal(bool cancelReportedEdit)
    {
        if (_gesture is { Kind: GestureKind.Move })
        {
            _options.Coordinator?.ClearDragPreviews(Display);
        }
        CancelSmartPreview();
        CancelRecognition();
        FinishTransitions();
        if (_working && _reported is not null && cancelReportedEdit)
        {
            _channel.CancelEdit(Display, _editBase);
        }
        _working = false;
        _work = default;
        _reported = null;
        _gesture = null;
        _hasDraft = false;
        _draft = default;
        _marquee = null;
        _eraseIds = ImmutableHashSet<Guid>.Empty;
        EndTextSession();
        _linearId = null;
        _linearPreview = null;
        _regionRequest = null;
        SetAttachHints([]);
        _shownStale = true;
    }

    /// <summary>Mac fail(error): cancels the gesture and shows the error.</summary>
    private void Fail(CanvasError error)
    {
        CancelGesture();
        Report(error.Message);
    }

    private void Report(string message) => _channel.Send(new CanvasCommand(CanvasCommandKind.Report, Display) { Message = message });

    private void Status(string message) => _channel.Send(new CanvasCommand(CanvasCommandKind.Status, Display) { Message = message });

    private void Send(CanvasCommandKind kind) => _channel.Send(new CanvasCommand(kind, Display));

    private void Request(CanvasRequest request) => Requested?.Invoke(request);

    private SceneBuilder Bounds => _boundsBuilder ??= new SceneBuilder(_options.TextMeasurer);

    internal void SetDragPreview(ElementList elements)
    {
        if (elements.IsEmpty && _dragPreview.IsEmpty)
        {
            return;
        }
        _dragPreview = elements;
        _shownStale = true;
        Publish(CanvasChangeKinds.Content | CanvasChangeKinds.Selection, null);
    }

    private void SetAttachHints(ImmutableArray<Anchor> hints)
    {
        if (hints.IsDefault)
        {
            hints = [];
        }
        if (_attachHints.SequenceEqual(hints))
        {
            return;
        }
        _attachHints = hints;
    }

    private void RefreshCursor()
    {
        _cursor = _pointer is { } point ? HoverCursor(point) : IsDrawing ? ToolCursor() : CanvasCursor.Arrow;
    }

    private CanvasCursor ToolCursor() =>
        IsDrawing ? CanvasCursor.ForTool(Workspace.Tool, Workspace.Style.Width * ScreenZoom, Workspace.Style.Color) : CanvasCursor.Arrow;

    /// <summary>The objects to draw (Mac visibleElements without the text editor), cached until something but a stroke changes.</summary>
    private ElementList ShownElements()
    {
        if (!_shownStale)
        {
            return _shown;
        }
        ElementList elements = _hasDocument ? Current.Elements : ElementList.Empty;
        if (_text is { IsLabel: false } text)
        {
            Guid hidden = text.ElementId;
            elements = elements.Filter(element => element.Id != hidden);
        }
        else if (_text is { IsLabel: true } label && elements.IndexOf(label.ElementId) is >= 0 and var index
                 && elements[index].Label is { } current && current.Text != label.Text)
        {
            // Mac visibleElements: the shape keeps a gap in its outline for the label as it is being typed.
            Element owner = elements[index];
            owner.Label = new ShapeLabel(label.Text, current.Style, current.FollowsStroke);
            elements = elements.SetItem(index, owner);
        }
        if (_morph is { } morph)
        {
            Guid morphing = morph.Shape.Id;
            elements = elements.Filter(element => element.Id != morphing);
        }
        if (!_dragPreview.IsEmpty)
        {
            elements = elements.AddRange(_dragPreview);
        }
        _shown = elements;
        _shownStale = false;
        return elements;
    }

    /// <summary>
    /// Mac SelectionOverlayView: the selection shows only in Draw, not while the text editor is open, and not on the
    /// objects the eraser is about to delete.
    /// </summary>
    private ImmutableHashSet<Guid> ShownSelection()
    {
        if (!IsDrawing || !_hasDocument || _text is not null)
        {
            return ImmutableHashSet<Guid>.Empty;
        }
        if (!_dragPreview.IsEmpty)
        {
            return [.. _dragPreview.Select(element => element.Id)];
        }
        ImmutableHashSet<Guid> selection = Current.Selection;
        return _eraseIds.IsEmpty || selection.IsEmpty ? selection : selection.Except(_eraseIds);
    }

    private ImmutableHashSet<Guid> FadedIds()
    {
        if (!Workspace.SmartDraw || (_smartPreview is null && _releasePreview is null))
        {
            return _eraseIds;
        }
        ImmutableHashSet<Guid> faded = _eraseIds;
        if (_smartPreview is { } preview)
        {
            faded = faded.Add(preview.Id);
        }
        if (_releasePreview is { } held)
        {
            faded = faded.Add(held.Id);
        }
        return faded;
    }

    private Element? CandidateForDrawing()
    {
        if (!Workspace.SmartDraw || (_smartPreview ?? _releasePreview) is not { } candidate)
        {
            return null;
        }
        candidate.Style.Pattern = StrokePattern.Dashed;
        candidate.Style.Opacity = 0.75;
        return candidate;
    }

    private Element? DraftForDrawing()
    {
        if (_hasDraft)
        {
            return _draft;
        }
        return ConstructionPreview;
    }

    /// <summary>Builds the new state and tells listeners. Allocation-free while only a stroke grows.</summary>
    private void Publish(CanvasChangeKinds kinds, Rect? dirty)
    {
        if (_disposed)
        {
            return;
        }
        _revision++;
        bool drawing = IsDrawing;
        _state = new CanvasLiveState
        {
            Revision = _revision,
            IsDrawing = drawing,
            Elements = _workspace is null ? ElementList.Empty : ShownElements(),
            Draft = _workspace is null ? null : DraftForDrawing(),
            Selection = _workspace is null ? ImmutableHashSet<Guid>.Empty : ShownSelection(),
            FadedIds = _workspace is null ? ImmutableHashSet<Guid>.Empty : FadedIds(),
            EraserTargets = _eraseIds,
            HiddenLabelIds = _text is { IsLabel: true } label ? ImmutableHashSet.Create(label.ElementId) : ImmutableHashSet<Guid>.Empty,
            Marquee = _regionRequest is null ? _marquee : null,
            RegionSelection = _regionRequest is not null ? _marquee : null,
            AttachHints = _attachHints,
            SmartPreview = _workspace is null ? null : CandidateForDrawing(),
            Cursor = _cursor,
            Text = _text?.Session,
            Morph = _morph,
            MorphProgress = _morphProgress,
            EraseFade = _fade,
            EraseFadeProgress = _fadeProgress,
            Laser = _laser,
            LaserTime = _laserTime,
            HasActiveEdit = HasActiveEdit,
            ConstructingArrow = _linearId is not null,
        };
        Changed?.Invoke(new CanvasChange(kinds, dirty, _revision));
    }
}
