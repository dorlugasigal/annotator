using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Core.Scene;

namespace Annotator.Workspace.Canvas;

/// <summary>Which layers a <see cref="CanvasScene.Update"/> changed.</summary>
[Flags]
public enum CanvasLayers
{
    None = 0,
    Scene = 1,
    Live = 2,
}

/// <summary>
/// The two layers of one display's canvas for an overlay presenter, built from a <see cref="CanvasLiveState"/> (Mac
/// DrawingCanvas.draw with SelectionOverlayView above it). <see cref="Scene"/> holds the committed drawings, pixel effects
/// and covers with the selection. <see cref="Live"/>, drawn above it, holds the object being drawn, the Smart Draw
/// candidate, the morph and erase transitions, the region dimming, the marquee, attach hints and eraser outlines; while
/// it shows a drawing, the covers and pixel effects follow again with the selection, so nothing drawn live shows through
/// a cover. Unchanged parts keep their display lists: a growing stroke changes only its own list, and an unchanged state
/// changes nothing. Use it on the canvas thread.
/// </summary>
public sealed class CanvasScene
{
    private static readonly StrokeStyle MarqueeStroke = new(1, dashes: [4, 3]);
    private static readonly StrokeStyle EraserStroke = new(1, dashes: [3, 3]);
    private static readonly StrokeStyle HaloStroke = new(2);
    private static readonly StrokeStyle DotStroke = new(1.5);
    private static readonly Paint RegionDim = Paint.Solid(new RGBA(0, 0, 0, 0.4));
    private static readonly Paint HaloFill = Paint.System(SystemColor.Accent, 0.08);
    private static readonly Paint HaloLine = Paint.System(SystemColor.Accent, 0.9);
    private static readonly Paint EraserLine = Paint.System(SystemColor.Accent, 0.75);
    private static readonly Paint DotFill = Paint.Solid(new RGBA(1, 1, 1, 0.9));

    private readonly SceneBuilder _sceneBuilder;
    private readonly SceneBuilder _liveBuilder;
    private readonly DisplayListBuilder _lists = new();
    private readonly List<DisplayList> _parts = [];
    private readonly HashSet<Guid> _protective = [];
    private readonly List<Guid> _hintOrder = [];

    private bool _hasScene;
    private ElementList _source;
    private Guid? _draftId;
    private ElementList _sceneElements;
    private ImmutableHashSet<Guid> _selection = ImmutableHashSet<Guid>.Empty;
    private ImmutableHashSet<Guid> _faded = ImmutableHashSet<Guid>.Empty;
    private ImmutableHashSet<Guid> _hiddenLabels = ImmutableHashSet<Guid>.Empty;
    private double _selectionScale = 1;
    private bool _pixelsAvailable;
    private DisplayList[] _covers = [];

    private Rect? _marquee;
    private DisplayList? _marqueeList;
    private (Rect Region, Rect Bounds)? _region;
    private DisplayList? _regionList;
    private ImmutableArray<Anchor> _hints = [];
    private ElementList _hintElements;
    private DisplayList? _hintList;
    private ImmutableHashSet<Guid> _eraser = ImmutableHashSet<Guid>.Empty;
    private ElementList _eraserElements;
    private DisplayList? _eraserList;
    private SnapMorph? _morph;
    private double _morphProgress;
    private DisplayList[] _morphLists = [];
    private EraseFade? _fade;
    private double _fadeProgress;
    private DisplayList[] _fadeLists = [];
    private CanvasTextSession? _edited;
    private DisplayList? _editedList;
    private DisplayList? _draftList;
    private SharedCanvasContent? _shared;
    private SceneSnapshot? _sharedScene;
    private SceneSnapshot? _sharedLive;

    public CanvasScene(ITextMeasurer textMeasurer)
    {
        ArgumentNullException.ThrowIfNull(textMeasurer);
        _sceneBuilder = new SceneBuilder(textMeasurer);
        _liveBuilder = new SceneBuilder(textMeasurer);
    }

    /// <summary>The committed drawings and the selection.</summary>
    public SceneSnapshot Scene { get; private set; } = SceneSnapshot.Empty;

    /// <summary>The gesture in progress, transitions and adornments; <see cref="SceneSnapshot.Empty"/> while there are none.</summary>
    public SceneSnapshot Live { get; private set; } = SceneSnapshot.Empty;

    /// <summary>
    /// Builds both layers for <paramref name="state"/>. <paramref name="bounds"/> is the canvas in points (the region
    /// dimming covers it); <paramref name="selectionScale"/> is the zoom that keeps selection handles the same size on
    /// screen (1 without zoom). With <paramref name="pixelsAvailable"/>, an effect layer above shows the pixel effects, so
    /// the scene leaves them out instead of drawing them opaque (Mac <c>pixelsAvailableForBlur</c>). <paramref name="editor"/>
    /// is what the text editor draws with the text being edited (<see cref="CanvasTextEditor.Decorations"/>). Returns the
    /// layers whose snapshot changed.
    /// </summary>
    public CanvasLayers Update(in CanvasLiveState state, Rect bounds, double selectionScale = 1, bool pixelsAvailable = false,
                               TextEditorDecorations? editor = null)
    {
        var changed = CanvasLayers.None;
        if (UpdateScene(state, selectionScale, pixelsAvailable))
        {
            changed |= CanvasLayers.Scene;
        }
        if (UpdateLive(state, bounds, editor))
        {
            changed |= CanvasLayers.Live;
        }
        return changed;
    }

    /// <summary>
    /// What the shared region shows of this canvas (Mac elementsForSharing and SharedRegionDrawingLayer), from the lists the
    /// canvas draws: the document's objects, with a draft in place of the object it rebuilds and the text being edited in
    /// place of its object, and the drawings among them without covers and pixel effects. An object drawn anew is shared once
    /// it is committed; the selection, guides and the text editor's marks are never shared. Call it after <see cref="Update"/>
    /// with the same state.
    /// </summary>
    public SharedCanvasContent Shared(in CanvasLiveState state)
    {
        if (_shared is { } cached && ReferenceEquals(_sharedScene, Scene) && ReferenceEquals(_sharedLive, Live) && cached.Source == state.Elements)
        {
            return cached;
        }
        var lists = new Dictionary<Guid, DisplayList>();
        foreach (DisplayList list in Scene.Elements)
        {
            lists[list.ElementId] = list;
        }
        Element? draft = state.Draft is { } drawn && state.Elements.Contains(drawn.Id) ? drawn : null;
        CanvasTextSession? text = state.Text;
        var elements = new List<Element>(state.Elements.Count + 1);
        ImmutableArray<DisplayList>.Builder drawings = ImmutableArray.CreateBuilder<DisplayList>(state.Elements.Count + 1);
        bool textShared = false;
        foreach (ref readonly Element element in state.Elements)
        {
            Element shared = element;
            DisplayList? list;
            if (draft is { } replacement && element.Id == replacement.Id)
            {
                shared = replacement;
                list = _draftList;
            }
            else if (text is { IsLabel: false } edited && element.Id == edited.ElementId)
            {
                shared = new Element(Tool.Text, edited.Frame, text: edited.Text, style: edited.Style, id: edited.ElementId) { Rotation = edited.Rotation };
                list = _editedList;
                textShared = true;
            }
            else
            {
                list = lists.GetValueOrDefault(element.Id);
            }
            elements.Add(shared);
            if (!shared.Tool.IsProtection && list is not null)
            {
                drawings.Add(list);
                if (text is { IsLabel: true } label && element.Id == label.ElementId && _editedList is { } labelList)
                {
                    drawings.Add(labelList);
                    textShared = true;
                }
            }
        }
        if (!textShared && text is { IsLabel: false } added && added.Text.Length > 0 && _editedList is { } addedList)
        {
            // Windows keeps new text out of the document while it is edited; the Mac's document holds it already.
            elements.Add(new Element(Tool.Text, added.Frame, text: added.Text, style: added.Style, id: added.ElementId) { Rotation = added.Rotation });
            drawings.Add(addedList);
        }
        _shared = new SharedCanvasContent(state.Elements, ElementList.Create(CollectionsMarshal.AsSpan(elements)), drawings.DrainToImmutable());
        _sharedScene = Scene;
        _sharedLive = Live;
        return _shared;
    }
    /// <summary>Forgets every cached list, for example after the canvas's display changed; the next update rebuilds both layers.</summary>
    public void Reset()
    {
        _sceneBuilder.Reset();
        _liveBuilder.Reset();
        _hasScene = false;
        _source = default;
        _sceneElements = default;
        _covers = [];
        _marquee = null;
        _marqueeList = null;
        _region = null;
        _regionList = null;
        _hints = [];
        _hintList = null;
        _eraser = ImmutableHashSet<Guid>.Empty;
        _eraserList = null;
        _morph = null;
        _morphLists = [];
        _fade = null;
        _fadeLists = [];
        _edited = null;
        _editedList = null;
        _draftList = null;
        _shared = null;
        _sharedScene = null;
        _sharedLive = null;
        Scene = SceneSnapshot.Empty;
        Live = SceneSnapshot.Empty;
    }

    private bool UpdateScene(in CanvasLiveState state, double selectionScale, bool pixelsAvailable)
    {
        ElementList elements = state.Elements;
        Guid? draftId = state.Draft?.Id;
        bool sameElements = _hasScene && elements == _source && draftId == _draftId;
        if (sameElements && SameSet(state.Selection, _selection) && SameSet(state.FadedIds, _faded)
            && SameSet(state.HiddenLabelIds, _hiddenLabels) && selectionScale == _selectionScale && pixelsAvailable == _pixelsAvailable)
        {
            return false;
        }
        if (!sameElements)
        {
            // A draft with the identity of a document object (a clicked arrow being built) replaces it: it is drawn live.
            _sceneElements = draftId is { } id && elements.Contains(id) ? elements.Filter(element => element.Id != id) : elements;
            _source = elements;
            _draftId = draftId;
        }
        _hasScene = true;
        _selection = state.Selection;
        _faded = state.FadedIds;
        _hiddenLabels = state.HiddenLabelIds;
        _selectionScale = selectionScale;
        _pixelsAvailable = pixelsAvailable;
        SceneSnapshot previous = Scene;
        Scene = _sceneBuilder.Build(_sceneElements, _selection, pixelsAvailable, selectionScale, _faded, _hiddenLabels);
        if (ReferenceEquals(previous, Scene))
        {
            return false;
        }
        CollectCovers(elements);
        return true;
    }

    /// <summary>The scene's covers and pixel effects, which the scene builder draws last, in their order.</summary>
    private void CollectCovers(ElementList elements)
    {
        _protective.Clear();
        foreach (ref readonly Element element in elements)
        {
            if (element.Tool.IsProtection)
            {
                _protective.Add(element.Id);
            }
        }
        ReadOnlySpan<DisplayList> lists = Scene.Elements;
        int first = lists.Length;
        while (first > 0 && _protective.Contains(lists[first - 1].ElementId))
        {
            first--;
        }
        _covers = first == lists.Length ? [] : lists[first..].ToArray();
    }

    private bool UpdateLive(in CanvasLiveState state, Rect bounds, TextEditorDecorations? editor)
    {
        _parts.Clear();
        _draftList = null;
        if (state.Draft is { } draft)
        {
            _draftList = _liveBuilder.List(draft);
            _parts.Add(_draftList);
        }
        if (state.SmartPreview is { } candidate)
        {
            _parts.Add(_liveBuilder.List(candidate));
        }
        if (state.Morph is { } morph)
        {
            _parts.AddRange(MorphLists(morph, state.MorphProgress));
        }
        else
        {
            _morph = null;
            _morphLists = [];
        }
        if (state.EraseFade is { } fade)
        {
            _parts.AddRange(FadeLists(fade, state.EraseFadeProgress));
        }
        else
        {
            _fade = null;
            _fadeLists = [];
        }
        if (state.Text is { } session)
        {
            // The Mac's text view sits above the canvas's drawings while it edits; covers still hide it here.
            if (editor is { Under.IsEmpty: false })
            {
                _parts.Add(editor.Under);
            }
            _parts.Add(EditedTextList(session));
            if (editor is { Over.IsEmpty: false })
            {
                _parts.Add(editor.Over);
            }
        }
        else
        {
            _edited = null;
            _editedList = null;
        }
        int drawings = _parts.Count;
        if (state.RegionSelection is { } region)
        {
            _parts.Add(RegionList(region, bounds));
        }
        if (state.Marquee is { } marquee)
        {
            _parts.Add(MarqueeList(marquee));
        }
        if (!state.AttachHints.IsDefaultOrEmpty)
        {
            _parts.Add(HintList(state.AttachHints, state.Elements));
        }
        if (!state.EraserTargets.IsEmpty)
        {
            _parts.Add(EraserList(state.EraserTargets, state.Elements));
        }
        if (drawings > 0 && _covers.Length > 0)
        {
            _parts.AddRange(_covers);
            if (!Scene.Selection.IsEmpty)
            {
                _parts.Add(Scene.Selection);
            }
        }
        ReadOnlySpan<DisplayList> parts = CollectionsMarshal.AsSpan(_parts);
        if (SameLists(parts, Live.Elements))
        {
            return false;
        }
        Live = SceneSnapshot.Create(parts);
        if (Live.Elements.IsEmpty)
        {
            // Nothing is drawn live any more: drop the cached strokes and candidates of the finished gesture.
            _liveBuilder.Reset();
        }
        return true;
    }

    /// <summary>
    /// The text being edited, drawn by the same display-list path as committed text and on its baseline, so it does not
    /// move at commit (WIN-INP-09); rebuilt only when the session changes.
    /// </summary>
    private DisplayList EditedTextList(CanvasTextSession session)
    {
        if (_editedList is { } cached && session.Equals(_edited))
        {
            return cached;
        }
        var text = new Element(Tool.Text, session.Frame, text: session.Text, style: session.Style, id: session.ElementId)
        {
            Rotation = session.Rotation,
        };
        _edited = session;
        _editedList = _liveBuilder.EditedText(text, session.PaintFrame, session.FirstBaseline(session.Text));
        return _editedList;
    }

    /// <summary>Mac drawSnapMorph: the fading candidate, the line moving onto the outline (or the fading ink), then the shape fading in.</summary>
    private DisplayList[] MorphLists(SnapMorph morph, double progress)
    {
        if (ReferenceEquals(morph, _morph) && progress == _morphProgress)
        {
            return _morphLists;
        }
        SnapMorphFrame frame = morph.FrameAt(progress);
        var lists = new List<DisplayList>(4);
        if (frame.Dashed is { } dashed)
        {
            lists.Add(_liveBuilder.List(dashed));
        }
        if (frame.Line is { } line)
        {
            lists.Add(_liveBuilder.List(line));
        }
        if (frame.Ink is { } ink && Transition(ink, frame.InkAlpha, 1) is { } inkList)
        {
            lists.Add(inkList);
        }
        if (Transition(frame.Shape, frame.ShapeAlpha, 1) is { } shapeList)
        {
            lists.Add(shapeList);
        }
        _morph = morph;
        _morphProgress = progress;
        _morphLists = [.. lists];
        return _morphLists;
    }

    /// <summary>Mac drawEraseFade: every erased drawing at the fade's opacity, shrinking around its center unless motion is reduced.</summary>
    private DisplayList[] FadeLists(EraseFade fade, double progress)
    {
        if (ReferenceEquals(fade, _fade) && progress == _fadeProgress)
        {
            return _fadeLists;
        }
        (double alpha, double scale) = fade.FrameAt(progress);
        var lists = new List<DisplayList>(fade.Elements.Count);
        foreach (ref readonly Element element in fade.Elements)
        {
            if (Transition(element, alpha, scale) is { } list)
            {
                lists.Add(list);
            }
        }
        _fade = fade;
        _fadeProgress = progress;
        _fadeLists = [.. lists];
        return _fadeLists;
    }

    /// <summary>Mac drawTransition: the element in a transparency layer of <paramref name="alpha"/> over its drawing bounds, scaled about its center.</summary>
    private DisplayList? Transition(in Element element, double alpha, double scale)
    {
        if (!(alpha > 0.001))
        {
            return null;
        }
        DisplayList list = _liveBuilder.List(element);
        if (list.IsEmpty)
        {
            return null;
        }
        Point center = element.Frame.Center;
        bool scaled = scale != 1;
        if (scaled)
        {
            _lists.Add(DisplayItem.PushTransform(AffineTransform.Translation(center.X, center.Y).Scaled(scale, scale).Translated(-center.X, -center.Y)));
        }
        _lists.Add(DisplayItem.PushLayer(Math.Min(1, alpha), _liveBuilder.DrawingBounds(element)));
        _lists.AddRange(list);
        _lists.Add(DisplayItem.PopLayer());
        if (scaled)
        {
            _lists.Add(DisplayItem.PopTransform());
        }
        return _lists.ToList(element.Id);
    }

    /// <summary>Mac outputRegion dimming: 40% black over the canvas except the region being chosen.</summary>
    private DisplayList RegionList(Rect region, Rect bounds)
    {
        if (_region is { } known && known.Region == region && known.Bounds == bounds && _regionList is { } cached)
        {
            return cached;
        }
        PathData path = new PathBuilder(10, 10).AddRect(bounds).AddRect(region).ToPathData();
        _region = (region, bounds);
        _regionList = _lists.Add(DisplayItem.FillPath(path, FillRule.EvenOdd, RegionDim)).ToList();
        return _regionList;
    }

    /// <summary>Mac marquee: the accent color, 1 point wide, dashed 4 on and 3 off.</summary>
    private DisplayList MarqueeList(Rect marquee)
    {
        if (_marquee == marquee && _marqueeList is { } cached)
        {
            return cached;
        }
        PathData path = new PathBuilder(5, 5).AddRect(marquee).ToPathData();
        _marquee = marquee;
        _marqueeList = _lists.Add(DisplayItem.StrokePath(path, MarqueeStroke, Paint.Accent)).ToList();
        return _marqueeList;
    }

    /// <summary>
    /// Mac drawAttachHints: a halo around each shape a line end can attach to, then its anchor dots; the chosen anchor
    /// is larger and filled with the accent color.
    /// </summary>
    private DisplayList HintList(ImmutableArray<Anchor> hints, ElementList elements)
    {
        if (_hintList is { } cached && hints.SequenceEqual(_hints) && elements == _hintElements)
        {
            return cached;
        }
        _hintOrder.Clear();
        foreach (Anchor hint in hints)
        {
            if (!_hintOrder.Contains(hint.ElementId))
            {
                _hintOrder.Add(hint.ElementId);
            }
        }
        foreach (Guid id in _hintOrder)
        {
            int index = elements.IndexOf(id);
            if (index < 0)
            {
                continue;
            }
            ref readonly Element shape = ref elements[index];
            PathData halo = new PathBuilder(10, 21).AddRoundedRect(RectMath.Outset(shape.SymbolFrame, 6, 6), 6, 6).ToPathData();
            bool rotated = shape.Rotation != 0;
            if (rotated)
            {
                _lists.Add(DisplayItem.PushTransform(AffineTransform.RotationAbout(shape.Frame.Center, shape.Rotation)));
            }
            _lists.Add(DisplayItem.FillPath(halo, FillRule.NonZero, HaloFill));
            _lists.Add(DisplayItem.StrokePath(halo, HaloStroke, HaloLine));
            if (rotated)
            {
                _lists.Add(DisplayItem.PopTransform());
            }
            foreach (Point location in shape.AnchorLocations)
            {
                Point point = shape.AnchorPoint(location);
                bool chosen = hints.Contains(new Anchor(id, location));
                double radius = chosen ? 5.5 : 3.5;
                PathData dot = new PathBuilder(6, 13).AddEllipse(new Rect(point.X - radius, point.Y - radius, radius * 2, radius * 2)).ToPathData();
                _lists.Add(DisplayItem.FillPath(dot, FillRule.NonZero, chosen ? Paint.Accent : DotFill));
                _lists.Add(DisplayItem.StrokePath(dot, DotStroke, chosen ? Paint.White : Paint.Accent));
            }
        }
        _hints = hints;
        _hintElements = elements;
        _hintList = _lists.ToList();
        return _hintList;
    }

    /// <summary>Mac eraser preview: a dashed accent outline 4 points outside each object the eraser will delete.</summary>
    private DisplayList EraserList(ImmutableHashSet<Guid> targets, ElementList elements)
    {
        if (_eraserList is { } cached && SameSet(targets, _eraser) && elements == _eraserElements)
        {
            return cached;
        }
        foreach (ref readonly Element element in elements)
        {
            if (!targets.Contains(element.Id))
            {
                continue;
            }
            bool rotated = element.Rotation != 0;
            if (rotated)
            {
                _lists.Add(DisplayItem.PushTransform(AffineTransform.RotationAbout(element.Frame.Center, element.Rotation)));
            }
            PathData outline = new PathBuilder(5, 5).AddRect(RectMath.Outset(element.Frame, 4, 4)).ToPathData();
            _lists.Add(DisplayItem.StrokePath(outline, EraserStroke, EraserLine));
            if (rotated)
            {
                _lists.Add(DisplayItem.PopTransform());
            }
        }
        _eraser = targets;
        _eraserElements = elements;
        _eraserList = _lists.ToList();
        return _eraserList;
    }

    private static bool SameSet(ImmutableHashSet<Guid> left, ImmutableHashSet<Guid> right) =>
        ReferenceEquals(left, right) || (left.Count == right.Count && left.SetEquals(right));

    private static bool SameLists(ReadOnlySpan<DisplayList> parts, ReadOnlySpan<DisplayList> shown)
    {
        int count = 0;
        foreach (DisplayList list in parts)
        {
            if (list.IsEmpty)
            {
                continue;
            }
            if (count >= shown.Length || !ReferenceEquals(list, shown[count]))
            {
                return false;
            }
            count++;
        }
        return count == shown.Length;
    }
}

/// <summary>
/// What the shared region shows of one canvas (<see cref="CanvasScene.Shared"/>): <see cref="Elements"/> for its covers
/// and pixel effects, and <see cref="Drawings"/>, the display lists of its other objects in drawing order.
/// </summary>
public sealed record SharedCanvasContent(ElementList Source, ElementList Elements, ImmutableArray<DisplayList> Drawings);
