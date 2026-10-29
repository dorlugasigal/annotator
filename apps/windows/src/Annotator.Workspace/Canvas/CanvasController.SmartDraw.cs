using Annotator.Core;

namespace Annotator.Workspace.Canvas;

public sealed partial class CanvasController
{
    private const int MorphSamples = 96;

    private Element? _smartPreview;
    private Element? _releasePreview;
    private Element? _previewPending;
    private bool _previewRunning;
    private ulong _previewRevision;
    private ulong _previewToken;
    private ulong _recognitionToken;

    private SnapMorph? _morph;
    private double? _morphStart;
    private double _morphProgress;
    private EraseFade? _fade;
    private double? _fadeStart;
    private double _fadeProgress;

    /// <summary>
    /// Moves the Smart Draw morph, the eraser fade and the laser pointer's trail to <paramref name="now"/> (seconds on
    /// <see cref="CanvasOptions.Clock"/>), as the Mac's display-linked tickers do; the first call after a transition starts
    /// sets its start time. Returns true while a transition or the trail still needs frames. Call it only while
    /// <see cref="CanvasLiveState.HasTransitions"/> is true.
    /// </summary>
    public bool AdvanceTransitions(double now)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool laser = LaserMoves;
        if (_morph is null && _fade is null && !laser)
        {
            return false;
        }
        Rect? dirty = null;
        if (_morph is { } morph)
        {
            if (!MorphValid(morph))
            {
                FinishSnapMorph();
            }
            else
            {
                double start = _morphStart ??= now;
                _morphProgress = Math.Min(1, Math.Max(0, (now - start) / SnapMorph.Duration));
                dirty = morph.Dirty;
                if (_morphProgress >= 1)
                {
                    FinishSnapMorph();
                }
            }
        }
        if (_fade is { } fade)
        {
            if (_gesture is not null || Current.Generation != fade.Generation || Workspace.WorkspaceHidden || !IsDrawing)
            {
                FinishEraseFade();
            }
            else
            {
                double start = _fadeStart ??= now;
                _fadeProgress = Math.Min(1, Math.Max(0, (now - start) / EraseFade.Duration));
                dirty = dirty is { } existing ? existing.Union(fade.Dirty) : fade.Dirty;
                if (_fadeProgress >= 1)
                {
                    FinishEraseFade();
                }
            }
        }
        if (laser && AdvanceLaser(now, out Rect? trail) && trail is { } moved)
        {
            dirty = dirty is { } existing ? existing.Union(moved) : moved;
        }
        Publish(CanvasChangeKinds.Transitions, dirty);
        return _morph is not null || _fade is not null || LaserMoves;
    }

    /// <summary>Mac finishTransitions: jumps every drawing transition to its end state. Document content never changes here.</summary>
    private void FinishTransitions()
    {
        bool had = _releasePreview is not null || _morph is not null || _fade is not null;
        _releasePreview = null;
        FinishSnapMorph();
        FinishEraseFade();
        if (had)
        {
            _shownStale = true;
        }
    }

    /// <summary>
    /// Mac requestSmartPreview: recognition of the stroke so far runs away from the canvas thread, one at a time, always on
    /// the newest stroke. A result shows only when no sample arrived while it ran, so the candidate appears when the pen
    /// pauses and never lags behind it.
    /// </summary>
    private void RequestSmartPreview()
    {
        if (!Workspace.SmartDraw || !_hasDraft || _draft.Tool != Tool.Pen)
        {
            return;
        }
        _previewRevision++;
        _previewPending = _draft;
        if (!_previewRunning)
        {
            RunSmartPreview();
        }
    }

    private void RunSmartPreview()
    {
        if (_previewPending is not { } input)
        {
            return;
        }
        _previewPending = null;
        _previewRunning = true;
        ulong token = _previewToken;
        ulong revision = _previewRevision;
        _options.Scheduler.Run(
            () => ShapeRecognition.Recognize(ShapeRecognition.PreviewInput(input)),
            candidate => SmartPreviewFinished(token, revision, input.Id, candidate),
            error => SmartPreviewFailed(token, error));
    }

    private void SmartPreviewFinished(ulong token, ulong revision, Guid id, Element? candidate)
    {
        if (_disposed || token != _previewToken)
        {
            return;
        }
        _previewRunning = false;
        if (!Workspace.SmartDraw)
        {
            _smartPreview = null;
            Publish(CanvasChangeKinds.Adornments, null);
            return;
        }
        if (_gesture is not { Kind: GestureKind.Create } gesture || gesture.Id != id)
        {
            return;
        }
        if (revision == _previewRevision)
        {
            _smartPreview = candidate;
            Publish(CanvasChangeKinds.Adornments, null);
        }
        RunSmartPreview();
    }

    private void SmartPreviewFailed(ulong token, Exception error)
    {
        if (_disposed || token != _previewToken)
        {
            return;
        }
        _previewRunning = false;
        Report(error.Message);
    }

    /// <summary>Mac cancelSmartPreview.</summary>
    private void CancelSmartPreview()
    {
        _previewToken++;
        _previewRevision++;
        _previewRunning = false;
        _previewPending = null;
        _smartPreview = null;
    }

    private void CancelRecognition() => _recognitionToken++;

    /// <summary>
    /// Mac scheduleRecognition: after a Smart Draw pen stroke, recognition runs away from the canvas thread while the
    /// release candidate stays visible. A recognized shape replaces the stroke without a new Undo step, then morphs in.
    /// </summary>
    private void ScheduleRecognition(Element source, Element? preview)
    {
        if (!Workspace.SmartDraw || source.Tool != Tool.Pen)
        {
            return;
        }
        ulong generation = _document.Generation;
        _releasePreview = preview;
        ulong token = ++_recognitionToken;
        _options.Scheduler.Run(
            () => ShapeRecognition.Recognize(source),
            recognized => RecognitionFinished(token, source, generation, recognized),
            error =>
            {
                if (!_disposed && token == _recognitionToken)
                {
                    ClearReleasePreview();
                    Report(error.Message);
                }
            });
    }

    private void RecognitionFinished(ulong token, Element source, ulong generation, Element? recognized)
    {
        if (_disposed || token != _recognitionToken)
        {
            return;
        }
        if (recognized is not { } shape || _working || !_hasDocument)
        {
            ClearReleasePreview();
            return;
        }
        Element? preview = _releasePreview;
        CanvasDocument document = _document;
        try
        {
            if (!document.ApplyRecognition(shape, generation, source.GeometryToken))
            {
                ClearReleasePreview();
                return;
            }
        }
        catch (CanvasError error)
        {
            ClearReleasePreview();
            Report(error.Message);
            return;
        }
        _channel.Submit(new EditSubmission(new DisplayEdit(Display, _version, document)) { Origin = Display });
        _document = document;
        _version = _version.Next;
        _shownStale = true;
        BeginSnapMorph(source, shape.Id, preview);
        Publish(CanvasChangeKinds.Content | CanvasChangeKinds.Adornments | CanvasChangeKinds.Transitions, null);
    }

    private void ClearReleasePreview()
    {
        if (_releasePreview is null)
        {
            return;
        }
        _releasePreview = null;
        Publish(CanvasChangeKinds.Adornments, null);
    }

    /// <summary>Mac beginSnapMorph: the ink morphs into the recognized outline unless Animation effects are off.</summary>
    private void BeginSnapMorph(Element ink, Guid id, Element? preview)
    {
        FinishSnapMorph();
        int index = _document.Elements.IndexOf(id);
        if (_options.ReducesMotion() || Workspace.WorkspaceHidden || !IsDrawing || index < 0)
        {
            _releasePreview = null;
            return;
        }
        Element shape = _document.Elements[index];
        ValueList<Point> from = SnapMorph.Resample(ink.Points.AsSpan(), MorphSamples);
        if (from.Count != MorphSamples)
        {
            _releasePreview = null;
            return;
        }
        ValueList<Point>? to = SnapMorph.SnapOutline(shape, MorphSamples) is { } outline
            ? SnapMorph.Align(outline, from, closed: shape.Tool != Tool.Arrow)
            : null;
        Rect dirty = Bounds.DrawingBounds(shape).Union(Bounds.DrawingBounds(ink));
        if (preview is { } candidate)
        {
            dirty = dirty.Union(Bounds.DrawingBounds(candidate));
        }
        if (to is { } target)
        {
            double pad = shape.Style.Width * 2 + 4;
            Rect around = Rect.Enclosing(target.AsSpan());
            dirty = dirty.Union(new Rect(around.X - pad, around.Y - pad, around.Width + pad * 2, around.Height + pad * 2));
        }
        _releasePreview = null;
        _morph = new SnapMorph(shape, ink, from, to, preview, _document.Generation, _document.Selection, Workspace.Tool, dirty);
        _morphStart = null;
        _morphProgress = 0;
        _shownStale = true;
    }

    /// <summary>Mac snapMorphValid: new input, a document, selection or tool change, or hiding ends the morph on the final shape.</summary>
    private bool MorphValid(SnapMorph morph) =>
        _gesture is null && Workspace.SmartDraw && !Workspace.WorkspaceHidden && IsDrawing && !_working
        && _document.Generation == morph.Generation && _document.Selection.SetEquals(morph.Selection) && Workspace.Tool == morph.Tool;

    private void FinishSnapMorph()
    {
        if (_morph is null)
        {
            return;
        }
        _morph = null;
        _morphStart = null;
        _morphProgress = 0;
        _shownStale = true;
    }

    /// <summary>Mac beginEraseFade: erased drawings fade from their preview strength; Animation effects off keeps opacity only.</summary>
    private void BeginEraseFade(ElementList erased)
    {
        FinishEraseFade();
        if (erased.IsEmpty || Workspace.WorkspaceHidden || !IsDrawing)
        {
            return;
        }
        Rect dirty = Bounds.DrawingBounds(erased[0]);
        for (int index = 1; index < erased.Count; index++)
        {
            dirty = dirty.Union(Bounds.DrawingBounds(erased[index]));
        }
        _fade = new EraseFade(erased, _document.Generation, scales: !_options.ReducesMotion(), dirty);
        _fadeStart = null;
        _fadeProgress = 0;
    }

    private void FinishEraseFade()
    {
        _fade = null;
        _fadeStart = null;
        _fadeProgress = 0;
    }
}
