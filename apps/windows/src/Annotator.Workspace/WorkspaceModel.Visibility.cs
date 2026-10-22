using Annotator.Core;

namespace Annotator.Workspace;

public sealed partial class WorkspaceModel
{
    /// <summary>The Mac's status when a board is too large to animate (WIN-MOT-02).</summary>
    public const string LargeBoardMessage = Motion.VisibilityChoreographer.LargeBoardMessage;

    /// <summary>The Mac's drawingToolsArePresented: what Show / hide and the tray's first row act on.</summary>
    private bool ToolsArePresented => _transition is { } transition ? transition.Showing : _toolbarVisible && !_hidden;

    /// <summary>Show / hide (Mac activateTools): hides drawings and tools when they are shown, otherwise opens drawing.</summary>
    public void ToggleTools()
    {
        using ChangeScope scope = Change();
        if (ToolsArePresented)
        {
            HideTools();
        }
        else
        {
            OpenDrawing();
        }
    }

    /// <summary>Show drawings and tools (Mac openDrawing).</summary>
    public void OpenDrawing()
    {
        using ChangeScope scope = Change();
        if (_hidden || _transition is not null)
        {
            ResumeDrawing();
            return;
        }
        BeginDrawing();
        ShowToolbar();
    }

    /// <summary>
    /// Resume drawing (Ctrl+Shift+2, spec 002 "Return to drawing"): shows hidden tools and drawings and enters
    /// Draw, never hides anything, and keeps objects, selection and history. During a show it takes effect when the
    /// show ends; during a hide it turns the hide around.
    /// </summary>
    public void ResumeDrawing()
    {
        using ChangeScope scope = Change();
        if (_hidden || _transition is not null)
        {
            _resumeAfterShowing = true;
            if (_transition is not { Showing: true })
            {
                ShowToolbar();
            }
            return;
        }
        if (_mode != WorkspaceMode.Draw)
        {
            BeginDrawing();
        }
        ShowToolbar();
    }

    /// <summary>
    /// Hide only (Mac hideTools): releases input, then hides drawings and the toolbar, with motion when the host animates
    /// (the toolbar stays shown until <see cref="CompleteVisibilityTransition"/>). Hiding what is already hidden does nothing.
    /// </summary>
    public void HideTools()
    {
        using ChangeScope scope = Change();
        _resumeAfterShowing = false;
        bool shown = _transition is not null || !_hidden;
        UseApps();
        Conceal();
        _revealDrawingRequested = false;
        if (shown && AnimationStyle() is { } style)
        {
            _transition = new VisibilityTransition(++_transitionSerial, Showing: false, style, RevealDrawing: false);
            return;
        }
        _transition = null;
        _toolbarVisible = false;
    }

    /// <summary>
    /// Shows the toolbar, revealing hidden drawings (Mac showToolbar). With motion, the workspace stays hidden until
    /// <see cref="CompleteVisibilityTransition"/>; a show that already runs is left alone.
    /// </summary>
    public void ShowToolbar()
    {
        using ChangeScope scope = Change();
        if (_transition is { Showing: true })
        {
            return;
        }
        if (!_hidden && _transition is null)
        {
            _toolbarVisible = true;
            return;
        }
        bool resume = _resumeAfterShowing;
        bool revealDrawing = resume && PrepareDrawingForReveal();
        _toolbarVisible = true;
        if (AnimationStyle() is { } style)
        {
            _revealDrawingRequested = revealDrawing;
            _transition = new VisibilityTransition(++_transitionSerial, Showing: true, style, revealDrawing);
            return;
        }
        _transition = null;
        Reveal(revealDrawing);
        FinishShowing();
    }

    /// <summary>
    /// The host's motion for <paramref name="serial"/> ended (Mac finishByDeadline): a show reveals the workspace, in Draw
    /// when it was asked for and not withdrawn meanwhile, then applies a pending Resume drawing; a hide hides the toolbar.
    /// A stale serial (a transition that was turned around or cancelled) does nothing.
    /// </summary>
    public void CompleteVisibilityTransition(ulong serial)
    {
        using ChangeScope scope = Change();
        if (_transition is not { } transition || transition.Serial != serial)
        {
            return;
        }
        _transition = null;
        if (transition.Showing)
        {
            bool drawing = transition.RevealDrawing && _revealDrawingRequested;
            _revealDrawingRequested = false;
            Reveal(drawing);
            FinishShowing();
        }
        else
        {
            _toolbarVisible = false;
        }
    }

    /// <summary>
    /// Stops the motion without finishing it (Mac visibility.cancel when displays change or the system suspends): a hidden
    /// workspace stays hidden with its toolbar, and a pending Resume drawing is dropped.
    /// </summary>
    public void CancelVisibilityTransition()
    {
        using ChangeScope scope = Change();
        if (_transition is null)
        {
            return;
        }
        _transition = null;
        _resumeAfterShowing = false;
        _revealDrawingRequested = false;
        if (_hidden)
        {
            _toolbarVisible = false;
        }
    }

    /// <summary>Sets the status line, as the Mac's motion does when an effect cannot start (Mac <c>model.status</c>).</summary>
    public void ShowStatus(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        using ChangeScope scope = Change();
        _status = message;
    }

    /// <summary>Mac finishShowingToolbar: a Resume drawing that waited for the show enters Draw.</summary>
    private void FinishShowing()
    {
        bool resume = _resumeAfterShowing;
        _resumeAfterShowing = false;
        if (resume && _mode != WorkspaceMode.Draw)
        {
            BeginDrawing();
        }
    }

    /// <summary>Reveals hidden drawings and the toolbar at once, ending any motion (Mac revealWorkspaceSurfaces then showToolbar).</summary>
    private void RevealImmediately()
    {
        _transition = null;
        _revealDrawingRequested = false;
        _toolbarVisible = true;
        Reveal(drawing: false);
        FinishShowing();
    }

    /// <summary>
    /// Mac shouldAnimate and animationStyle: null shows and hides at once (the host does not animate, the preference is
    /// off or Off, or the board has more than 64 objects, which says so); Reduce Motion turns every style into Fade.
    /// </summary>
    private VisibilityAnimationStyle? AnimationStyle()
    {
        if (!_animatesVisibility || _preferences.AnimateVisibility == false
            || _preferences.VisibilityAnimationStyle == VisibilityAnimationStyle.None)
        {
            return null;
        }
        int count = _canvases.Values.Sum(canvas => canvas.Document.Elements.Count);
        if (count > Motion.VisibilityChoreographer.ObjectLimit)
        {
            _status = LargeBoardMessage;
            return null;
        }
        if (_reducesMotion())
        {
            return VisibilityAnimationStyle.Fade;
        }
        return (_preferences.VisibilityAnimationStyle ?? VisibilityAnimationStyle.Slide).Resolved;
    }

    /// <summary>Enters Draw on every display, targeting the last used display, else the one under the pointer, else the first.</summary>
    public void BeginDrawing() => BeginDrawing(recordsUsage: true);

    /// <summary>Mac beginDrawing(recordsUsage:): an action that opens drawing on its way to something else counts only that.</summary>
    private void BeginDrawing(bool recordsUsage)
    {
        using ChangeScope scope = Change();
        if (recordsUsage)
        {
            RecordUsage(Metrics.UsageMetric.Drawing);
        }
        if (_hidden)
        {
            ResumeDrawing();
            return;
        }
        FinishZoomGestureCore();
        FinishStyleEditCore();
        if (PrepareDesktopDrawingSession() is not { } target)
        {
            return;
        }
        _mode = WorkspaceMode.Draw;
        foreach (Canvas canvas in _canvases.Values)
        {
            SetDrawing(canvas, true, focus: canvas == target);
        }
        _status = DrawStatus;
        Synchronize();
    }

    /// <summary>Use apps (Mac interact): every display becomes click-through; drawings stay visible.</summary>
    public void UseApps()
    {
        using ChangeScope scope = Change();
        if (_transition is { Showing: true })
        {
            // Mac performMenuAction(.useApps): a show that was going to end in Draw ends in Use apps instead.
            _resumeAfterShowing = false;
            _revealDrawingRequested = false;
        }
        FinishZoomGestureCore();
        FinishStyleEditCore();
        if (_active is { } active)
        {
            CommitTextIfNeeded(active);
        }
        if (!_hidden)
        {
            foreach (Canvas canvas in _canvases.Values)
            {
                SetDrawing(canvas, false);
            }
        }
        _mode = _canvases.Count == 0 ? WorkspaceMode.Off : WorkspaceMode.UseApps;
        _status = UseAppsStatus;
        Synchronize();
    }

    /// <summary>
    /// The displays changed (Mac displaysChanged): region sharing stops for safety, drawing input is released, pending
    /// edits are cancelled, removed displays are dropped and protected displays whose geometry changed are covered
    /// (WIN-OVR-05); then region sharing reads the new displays.
    /// </summary>
    public void DisplaysChanged()
    {
        using ChangeScope scope = Change();
        _sharing?.StopForSafety(Sharing.RegionSharingText.DisplaysChanged);
        CancelVisibilityTransition();
        UseApps();
        if (ReadDisplays() is not { } screens)
        {
            return;
        }
        var byId = new Dictionary<DisplayId, DisplayDescriptor>();
        foreach (DisplayDescriptor screen in screens)
        {
            byId.TryAdd(screen.Id, screen);
        }
        foreach ((DisplayId id, Canvas canvas) in _canvases.ToArray())
        {
            if (!byId.TryGetValue(id, out DisplayDescriptor? screen))
            {
                if (canvas.Document.HasProtection)
                {
                    _status = "A protected display disconnected. Stop sharing and review all covers.";
                }
                _canvases.Remove(id);
                if (_active == canvas)
                {
                    _active = null;
                }
                continue;
            }
            bool geometryChanged = canvas.Display.DesktopFrame != screen.DesktopFrame || canvas.Display.Scale != screen.Scale;
            canvas.Display = screen;
            CancelGesture(canvas, always: true);
            if (geometryChanged && canvas.Document.HasProtection)
            {
                try
                {
                    var cover = new Element(Tool.Cover, screen.Bounds);
                    Mutate(canvas, (ref CanvasDocument document) => document.Add(cover));
                    _status = "Display geometry changed: a full-display opaque cover was added. Review before removing.";
                }
                catch (CanvasError error)
                {
                    Report(error);
                }
            }
        }
        _sharing?.RefreshDisplays();
        Synchronize();
    }

    private void Conceal()
    {
        FinishStyleEditCore();
        _hidden = true;
        foreach (Canvas canvas in _canvases.Values)
        {
            canvas.TakesInput = false;
        }
    }

    private void Reveal(bool drawing)
    {
        _hidden = false;
        // Mac revealWorkspaceSurfaces: presentation?.retry() on every display, so capture resumes with the drawings.
        _effectsRetry++;
        foreach (Canvas canvas in _canvases.Values)
        {
            SetDrawing(canvas, false);
        }
        if (drawing && _active is { } active)
        {
            _mode = WorkspaceMode.Draw;
            foreach (Canvas canvas in _canvases.Values)
            {
                SetDrawing(canvas, true, focus: canvas == active);
            }
            _status = DrawStatus;
        }
        else
        {
            _mode = _canvases.Count == 0 ? WorkspaceMode.Off : WorkspaceMode.UseApps;
        }
        Synchronize();
    }

    private bool PrepareDrawingForReveal()
    {
        if (PrepareDesktopDrawingSession() is null)
        {
            return false;
        }
        _mode = WorkspaceMode.Draw;
        Synchronize();
        return true;
    }

    private IReadOnlyList<DisplayDescriptor>? ReadDisplays()
    {
        try
        {
            return _displaySource();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Report(error);
            return null;
        }
    }

    private Canvas? PrepareDesktopDrawingSession()
    {
        if (ReadDisplays() is not { } displays)
        {
            return null;
        }
        try
        {
            Point? pointer = _pointer();
            DisplayDescriptor? display =
                displays.FirstOrDefault(candidate => _active is not null && _canvases.TryGetValue(candidate.Id, out Canvas? canvas) && canvas == _active)
                ?? (pointer is { } position ? displays.FirstOrDefault(candidate => candidate.ContainsDesktopPoint(position)) : null)
                ?? displays.FirstOrDefault();
            if (display is null)
            {
                throw CanvasError.InvalidDocument("No attached display is available.");
            }
            if (displays.Select(candidate => candidate.Id).Distinct().Count() != displays.Count)
            {
                throw CanvasError.InvalidDocument("The display list contains duplicate identities.");
            }
            foreach (DisplayDescriptor candidate in displays)
            {
                if (!_canvases.ContainsKey(candidate.Id))
                {
                    _canvases[candidate.Id] = new Canvas(candidate, CanvasDocument.Empty(), FreshVersion());
                }
            }
            if (!_canvases.TryGetValue(display.Id, out Canvas? session))
            {
                throw CanvasError.InvalidDocument("The drawing display is unavailable.");
            }
            SwitchActiveSession(session, clearsSelection: true);
            return session;
        }
        catch (CanvasError error)
        {
            Report(error);
            return null;
        }
    }

    /// <summary>
    /// Makes <paramref name="session"/> the editing target. The previous target commits text and drops its
    /// gesture. With a tool other than Select, the new target's selection is cleared; a canvas edit passes
    /// <paramref name="clearsSelection"/> false because the canvas applies that rule to its own document.
    /// </summary>
    private void SwitchActiveSession(Canvas session, bool clearsSelection)
    {
        if (_active is null)
        {
            session.Board = Board.Desktop;
        }
        if (_active is { } previous && previous != session)
        {
            FinishZoomGestureCore();
            FinishStyleEditCore();
            CommitTextIfNeeded(previous);
            CancelGesture(previous);
            if (previous.Document.Presentation.Mode != PresentationMode.Off)
            {
                Mutate(previous, (ref CanvasDocument document) => document.StopPresentationWithoutHistory());
                previous.PresentationCut++;
            }
        }
        if (clearsSelection && _active != session && _tool != Tool.Select && !session.Document.Selection.IsEmpty)
        {
            Mutate(session, (ref CanvasDocument document) => document.Select([]));
        }
        _active = session;
    }

    /// <summary>Mac activateDrawingSession: a canvas in Draw becomes the editing target.</summary>
    private bool ActivateDrawingSession(Canvas session, bool clearsSelection)
    {
        if (_mode != WorkspaceMode.Draw || _hidden || !_canvases.ContainsValue(session))
        {
            return false;
        }
        if (_active != session)
        {
            SwitchActiveSession(session, clearsSelection);
            Synchronize();
        }
        return true;
    }

    /// <summary>Mac OverlaySession.setDrawing. Leaving Draw always gives a fresh version, cancelling edits still in flight.</summary>
    private void SetDrawing(Canvas canvas, bool drawing, bool focus = true)
    {
        if (!drawing)
        {
            CancelGesture(canvas, always: true);
        }
        canvas.TakesInput = drawing;
        if (drawing && focus)
        {
            RequestFocus(canvas);
        }
    }

    private void RequestFocus(Canvas canvas) => _focus = new FocusRequest(++_focusSerial, canvas.Display.Id);

    /// <summary>Mac cancelGesture: drops a reported edit; the canvas sees the fresh version and discards its work.</summary>
    private void CancelGesture(Canvas canvas, bool always = false)
    {
        if (canvas.Edit is null && !always)
        {
            return;
        }
        canvas.Edit = null;
        canvas.Version = FreshVersion();
    }

    /// <summary>Mac commitTextIfNeeded: keeps the last text a canvas reported for its text edit.</summary>
    private void CommitTextIfNeeded(Canvas canvas) => FinishInterim(canvas, EditKind.Text);

    /// <summary>Mac finishLinearPath: keeps the last state a canvas reported for its clicked arrow or line.</summary>
    private void FinishLinearPath(Canvas canvas) => FinishInterim(canvas, EditKind.Path);

    private void FinishInterim(Canvas canvas, EditKind kind)
    {
        if (canvas.Edit is not { } edit || edit.Kind != kind)
        {
            return;
        }
        canvas.Edit = null;
        if (edit.Interim is { } interim)
        {
            try
            {
                if (interim.IsEditing)
                {
                    interim.Commit();
                }
                canvas.Document = interim;
            }
            catch (CanvasError error)
            {
                Report(error);
            }
        }
        canvas.Version = FreshVersion();
    }
}
