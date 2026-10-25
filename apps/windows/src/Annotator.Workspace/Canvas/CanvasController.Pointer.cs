using System.Collections.Immutable;
using System.Globalization;
using Annotator.Core;

namespace Annotator.Workspace.Canvas;

public sealed partial class CanvasController
{
    private const double CornerHitRadius = 9;
    private const double VertexHitRadius = 8;
    private const double RotationHandleOffset = 24;

    private enum GestureKind
    {
        Create,
        Move,
        Resize,
        ResizeSelection,
        Rotate,
        RotateSelection,
        Vertex,
        Marquee,
        Erase,
    }

    /// <summary>The Mac's Gesture cases with their values. One instance per gesture; moves reuse it.</summary>
    private sealed class Gesture(GestureKind kind, uint pointerId)
    {
        public readonly GestureKind Kind = kind;
        public readonly uint PointerId = pointerId;
        public Guid Id;
        public Point Start;
        public Point Anchor;
        public Point Corner;
        public double InitialAngle;
        public double InitialRotation;
        public Element Original;
        public Dictionary<Guid, Element>? Originals;
        public int Index;
        public bool IncrementalInk;
        public Rect Bounds;
    }

    /// <summary>
    /// Takes a pointer event. Positions are points from the display's top-left corner. A gesture follows the pointer that
    /// started it; other pointers wait until it ends.
    /// </summary>
    public void OnPointer(in PointerEvent e)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (e.Samples.IsEmpty || _workspace is null)
        {
            return;
        }
        try
        {
            switch (e.Phase)
            {
                case PointerPhase.Down when e.ChangedButton == PointerButtons.Secondary:
                    if (_gesture is null)
                    {
                        OpenContextMenu(e.Position);
                    }
                    break;
                case PointerPhase.Down when e.ChangedButton is PointerButtons.Primary or PointerButtons.None:
                    Down(e);
                    break;
                case PointerPhase.Move:
                    if (_gesture is { } gesture && gesture.PointerId == e.PointerId)
                    {
                        Drag(e, e.Samples);
                    }
                    else if (_gesture is null)
                    {
                        Hover(e.Position);
                    }
                    break;
                case PointerPhase.Up:
                    if (_gesture is { } active && active.PointerId == e.PointerId)
                    {
                        Up(e);
                    }
                    break;
                case PointerPhase.Cancel:
                    if (_gesture is { } cancelled && cancelled.PointerId == e.PointerId)
                    {
                        CancelGesture();
                    }
                    break;
                case PointerPhase.Leave:
                    _pointer = null;
                    if (_gesture is null && _linearId is null)
                    {
                        SetAttachHints([]);
                    }
                    RefreshCursor();
                    Publish(CanvasChangeKinds.Adornments | CanvasChangeKinds.Cursor, null);
                    break;
            }
        }
        catch (CanvasError error)
        {
            Fail(error);
        }
    }

    /// <summary>Mac isOverRotationHandle: the shared handle of a multi-selection, or the handle of one selected object.</summary>
    public bool IsOverRotationHandle(Point point)
    {
        if (!IsDrawing || !_hasDocument || Workspace.Tool != Tool.Select)
        {
            return false;
        }
        ref CanvasDocument document = ref Current;
        double radius = RotationHitRadius;
        if (SelectionGeometry.Create(document.Elements, document.Selection) is { } group)
        {
            return group.CanRotate && point.Distance(new Point(group.Bounds.Center.X, group.Bounds.Y - RotationHandleOffset)) <= radius;
        }
        if (document.Selection.Count != 1 || FirstSelected(document) is not { } element || element.Locked || element.Tool.IsProtection
            || element.Tool.IsConnector)
        {
            return false;
        }
        Point local = point.Rotated(element.Frame.Center, -element.Rotation);
        return local.Distance(new Point(element.Frame.Center.X, element.Frame.Y - RotationHandleOffset)) <= radius;
    }

    /// <summary>Mac hoverCursor: the cursor for the pointer at <paramref name="point"/>.</summary>
    public CanvasCursor HoverCursor(Point point)
    {
        if (!IsDrawing)
        {
            return CanvasCursor.Arrow;
        }
        if (_gesture is { Kind: GestureKind.Rotate or GestureKind.RotateSelection })
        {
            return CanvasCursor.Rotation;
        }
        if (_text is { } text && TextContains(text.Session, point))
        {
            return CanvasCursor.IBeam;
        }
        if (IsOverRotationHandle(point))
        {
            return CanvasCursor.Rotation;
        }
        return ToolCursor();
    }

    private double RotationHitRadius => 18 / Math.Max(1, ScreenZoom);

    private static Element? FirstSelected(in CanvasDocument document)
    {
        foreach (ref readonly Element element in document.Elements)
        {
            if (document.Selection.Contains(element.Id))
            {
                return element;
            }
        }
        return null;
    }

    private int CountClick(in PointerEvent e)
    {
        PointerSample sample = e.Current;
        bool again = _hasLastClick && sample.Timestamp >= _lastClickTime && sample.Timestamp - _lastClickTime <= _options.DoubleClickTime
            && Math.Abs(sample.Position.X - _lastClickPoint.X) <= _options.DoubleClickSize.Width / 2
            && Math.Abs(sample.Position.Y - _lastClickPoint.Y) <= _options.DoubleClickSize.Height / 2;
        _clickCount = again ? _clickCount + 1 : 1;
        _lastClickTime = sample.Timestamp;
        _lastClickPoint = sample.Position;
        _hasLastClick = true;
        return _clickCount;
    }

    /// <summary>Mac mouseDown.</summary>
    private void Down(in PointerEvent e)
    {
        if (!CanStartEdit || _gesture is not null)
        {
            return;
        }
        Point point = e.Position;
        _pointer = point;
        int clicks = CountClick(e);
        FinishTransitions();
        CancelRecognition();
        if (_text is { } text && TextContains(text.Session, point))
        {
            return;
        }
        CommitText();
        if (_regionRequest is not null)
        {
            _gesture = new Gesture(GestureKind.Marquee, e.PointerId) { Start = point };
            _marquee = new Rect(point, point);
            Publish(CanvasChangeKinds.Adornments, null);
            return;
        }
        Tool tool = Workspace.Tool;
        Element? hit = ElementStacking.Frontmost(point, Current.Elements);
        if (tool == Tool.Counter)
        {
            PlaceCounter(point);
            return;
        }
        if (_linearId is { } linear)
        {
            if (clicks >= 2)
            {
                FinishLinearPath();
                return;
            }
            _work.Update(linear, (ref Element element) =>
            {
                if (element.Points[^1] != point)
                {
                    element.Points = element.Points.Add(point);
                }
                element.Frame = Rect.Enclosing(element.Points);
            });
            ReportLinearPath();
            _shownStale = true;
            Publish(CanvasChangeKinds.Content, null);
            return;
        }
        if (clicks >= 2 && tool == Tool.Select && hit is { Tool: Tool.Arrow, Locked: false } connector
            && Current.Selection.Contains(connector.Id))
        {
            EditArrowPoints(connector, point);
            return;
        }
        bool editsText = clicks == 2 && tool == Tool.Select && (hit is null || hit.Value.Tool == Tool.Text || hit.Value.Tool.IsShape);
        if (tool == Tool.Text || editsText)
        {
            if (_options.TextEditor is null)
            {
                // Without an editor nothing could be typed; say so instead of opening an empty text box.
                Status(WorkspaceModel.TextUnavailableMessage);
                return;
            }
            StartText(point);
            return;
        }
        if (tool == Tool.Select)
        {
            StartSelectGesture(e, point, hit);
        }
        else if (tool == Tool.Eraser)
        {
            BeginWork(EditKind.Gesture);
            _work.Begin();
            _gesture = new Gesture(GestureKind.Erase, e.PointerId);
            CollectErase(point);
        }
        else
        {
            StartCreateGesture(e, point, tool);
        }
        RefreshCursor();
        Publish(CanvasChangeKinds.Content | CanvasChangeKinds.Selection | CanvasChangeKinds.Adornments | CanvasChangeKinds.Edit
                | CanvasChangeKinds.Cursor, null);
    }

    private void PlaceCounter(Point point)
    {
        BeginWork(report: null);
        bool any = false;
        long maximum = long.MinValue;
        foreach (ref readonly Element element in _work.Elements)
        {
            if (element.Tool == Tool.Counter && TryParseSwiftInt(element.Text, out long value))
            {
                any = true;
                maximum = Math.Max(maximum, value);
            }
        }
        if (!any)
        {
            maximum = 0;
        }
        if (maximum == long.MaxValue)
        {
            CancelWork();
            throw CanvasError.InvalidDocument("The counter value is too large.");
        }
        _work.Add(new Element(Tool.Counter, new Rect(point.X - 18, point.Y - 18, 36, 36),
                              text: (maximum + 1).ToString(CultureInfo.InvariantCulture), style: DrawingStyle));
        SubmitWork();
        Publish(CanvasChangeKinds.Content | CanvasChangeKinds.Selection, null);
    }

    /// <summary>Swift Int(String): optional sign, ASCII digits only, 64-bit.</summary>
    private static bool TryParseSwiftInt(string text, out long value) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    /// <summary>A double-click on a selected arrow removes the bend point under it, or adds one on the nearest segment.</summary>
    private void EditArrowPoints(Element connector, Point point)
    {
        BeginWork(report: null);
        int remove = -1;
        for (int index = 1; index < connector.Points.Count - 1; index++)
        {
            if (connector.Points[index].Distance(point) <= VertexHitRadius)
            {
                remove = index;
                break;
            }
        }
        if (remove >= 0)
        {
            _work.Update(connector.Id, (ref Element element) =>
            {
                element.Points = element.Points.RemoveAt(remove);
                element.Frame = Rect.Enclosing(element.Points);
            });
        }
        else if (connector.Points.Count >= 2)
        {
            int nearest = 0;
            for (int index = 1; index < connector.Points.Count - 1; index++)
            {
                if (HitTesting.Distance(point, connector.Points[index], connector.Points[index + 1])
                    < HitTesting.Distance(point, connector.Points[nearest], connector.Points[nearest + 1]))
                {
                    nearest = index;
                }
            }
            _work.Update(connector.Id, (ref Element element) =>
            {
                var points = new List<Point>(element.Points.Count + 1);
                points.AddRange(element.Points);
                points.Insert(nearest + 1, point);
                element.Points = ValueList.CreateRange(points);
                element.Frame = Rect.Enclosing(element.Points);
            });
        }
        SubmitWork();
        Publish(CanvasChangeKinds.Content, null);
    }

    private void StartSelectGesture(in PointerEvent e, Point point, Element? hit)
    {
        BeginWork(EditKind.Gesture);
        SelectionGeometry? group = SelectionGeometry.Create(_work.Elements, _work.Selection);
        if (group is { } shared)
        {
            ValueList<Point> corners = shared.Corners;
            for (int index = 0; index < corners.Count; index++)
            {
                if (corners[index].Distance(point) <= CornerHitRadius)
                {
                    _work.Begin();
                    _gesture = new Gesture(GestureKind.ResizeSelection, e.PointerId)
                    {
                        Anchor = corners[(index + 2) % 4],
                        Corner = corners[index],
                        Originals = SelectedOriginals(includeLocked: false),
                    };
                    return;
                }
            }
            if (IsOverRotationHandle(point))
            {
                _work.Begin();
                Point center = shared.Bounds.Center;
                _gesture = new Gesture(GestureKind.RotateSelection, e.PointerId)
                {
                    Anchor = center,
                    InitialAngle = Math.Atan2(point.Y - center.Y, point.X - center.X),
                    Originals = SelectedOriginals(includeLocked: false),
                };
                return;
            }
        }
        if (_work.Selection.Count == 1 && FirstSelected(_work) is { Locked: false } selected)
        {
            if (selected.Tool.IsConnector)
            {
                for (int index = 0; index < selected.Points.Count; index++)
                {
                    if (selected.Points[index].Distance(point) <= VertexHitRadius)
                    {
                        _work.Begin();
                        _gesture = new Gesture(GestureKind.Vertex, e.PointerId) { Id = selected.Id, Index = index, Original = selected };
                        return;
                    }
                }
            }
            Point unrotated = point.Rotated(selected.Frame.Center, -selected.Rotation);
            Rect box = selected.Frame;
            Point[] corners = [box.Origin, new Point(box.MaxX, box.Y), new Point(box.MaxX, box.MaxY), new Point(box.X, box.MaxY)];
            for (int index = 0; index < corners.Length; index++)
            {
                if (unrotated.Distance(corners[index]) <= CornerHitRadius)
                {
                    _work.Begin();
                    _gesture = new Gesture(GestureKind.Resize, e.PointerId)
                    {
                        Id = selected.Id,
                        Original = selected,
                        Anchor = corners[(index + 2) % 4],
                    };
                    return;
                }
            }
            var handle = new Point(selected.Frame.Center.X, selected.Frame.Y - RotationHandleOffset);
            if (!selected.Tool.IsProtection && !selected.Tool.IsConnector && unrotated.Distance(handle) <= RotationHitRadius)
            {
                _work.Begin();
                Point center = selected.Frame.Center;
                _gesture = new Gesture(GestureKind.Rotate, e.PointerId)
                {
                    Id = selected.Id,
                    Anchor = center,
                    InitialAngle = Math.Atan2(point.Y - center.Y, point.X - center.X),
                    InitialRotation = selected.Rotation,
                };
                return;
            }
        }
        bool shift = (e.Modifiers & KeyModifiers.Shift) != 0;
        bool preservesSelection = !shift && (hit is { } target ? _work.Selection.Contains(target.Id) : group?.Bounds.Contains(point) == true);
        if (!preservesSelection)
        {
            _work.Select(point, extending: shift);
        }
        _work.Begin();
        _gesture = _work.Selection.IsEmpty
            ? new Gesture(GestureKind.Marquee, e.PointerId) { Start = point }
            : new Gesture(GestureKind.Move, e.PointerId) { Start = point, Originals = SelectedOriginals(includeLocked: true) };
    }

    private Dictionary<Guid, Element> SelectedOriginals(bool includeLocked)
    {
        var originals = new Dictionary<Guid, Element>();
        foreach (ref readonly Element element in _work.Elements)
        {
            if (_work.Selection.Contains(element.Id) && (includeLocked || !element.Locked))
            {
                originals[element.Id] = element;
            }
        }
        return originals;
    }

    /// <summary>
    /// Mac mouseDown for a drawing tool. The new object stays outside the document as a draft until release, so a
    /// stroke grows with amortized constant work and no allocation per sample.
    /// </summary>
    private void StartCreateGesture(in PointerEvent e, Point point, Tool tool)
    {
        BeginWork(EditKind.Gesture);
        _work.Begin();
        var element = new Element(tool, new Rect(point, point), style: DrawingStyle);
        if (tool.IsConnector || tool == Tool.Ruler)
        {
            element.Points = [point, point];
        }
        element.Validate();
        _draft = element;
        _hasDraft = true;
        var gesture = new Gesture(GestureKind.Create, e.PointerId) { Id = element.Id, Start = point, Bounds = DisplayInfo.Bounds };
        _gesture = gesture;
        if (tool.IsFreehand)
        {
            gesture.IncrementalInk = IncrementalInkAllowed();
            _inkPressure = tool == Tool.Pen && !e.HasPressure ? new InkPressure(_draft.Style.Width) : null;
            AppendSample(point, SamplePressure(point, e.Current, e.HasPressure));
            _draft.GeometryToken = Guid.NewGuid();
        }
    }

    /// <summary>Mac incrementalInkRect conditions that hold for the whole stroke.</summary>
    private bool IncrementalInkAllowed()
    {
        if (Workspace.SmartDraw || !_work.Selection.IsEmpty || _work.Presentation.Mode != PresentationMode.Off
            || _draft.Rotation != 0 || !(_draft.Style.Roughness == 0 || (_draft.Tool == Tool.Pen && _draft.Style.Pressure)))
        {
            return false;
        }
        foreach (ref readonly Element element in _work.Elements)
        {
            if (element.Tool.IsPixelEffect)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Mac samplePressure: a pen's own pressure, else simulated from the stroke's speed (Ink).</summary>
    private double SamplePressure(Point point, in PointerSample sample, bool hasPressure)
    {
        if (_inkPressure is { } ink)
        {
            double pressure = ink.Next(point, sample.Timestamp / 1000.0);
            _inkPressure = ink;
            return pressure;
        }
        return hasPressure ? sample.Pressure : 1;
    }

    /// <summary>
    /// CanvasDocument.appendSample on the draft: every sample is kept with its pressure (Mac: event pressure when
    /// above zero, else 1), and the frame grows by the sample. Amortized constant time, no allocation in steady state.
    /// </summary>
    private void AppendSample(Point point, double pressure)
    {
        if (!point.IsFinite)
        {
            throw CanvasError.InvalidGeometry;
        }
        double value = pressure > 0 && double.IsFinite(pressure) ? Math.Min(1, pressure) : 1;
        bool hadPoints = !_draft.Points.IsEmpty;
        if (hadPoints && _draft.Pressures.IsEmpty)
        {
            _draft.Pressures = ValueList.Repeat(1.0, _draft.Points.Count);
        }
        _draft.Points = _draft.Points.Add(point);
        _draft.Pressures = _draft.Pressures.Add(value);
        _draft.Frame = hadPoints ? _draft.Frame.Union(new Rect(point, point)) : new Rect(point, point);
        _draft.Revision = unchecked(_draft.Revision + 1);
    }

    /// <summary>Mac updateStraightHighlight: Shift makes the highlighter stroke one straight line, snapped to 45 degrees.</summary>
    private void StraightHighlight(Point start, Point point)
    {
        Point finish = DragConstraint.SnappedLineEnd(start, point);
        _draft.Points = finish == start ? [start] : [start, finish];
        _draft.Pressures = default;
        _draft.Frame = new Rect(start, finish);
        _draft.Revision = unchecked(_draft.Revision + 1);
        _draft.InvalidateGeometry();
    }

    /// <summary>Mac incrementalInkRect: the area a new stroke segment can paint, or null to draw everything.</summary>
    private Rect? IncrementalInkRect(Point previous, Point point, Rect bounds)
    {
        double padding = _draft.Style.Width * (_draft.Tool == Tool.Highlighter ? 3 : 1) + _draft.Style.Roughness + 3;
        Rect segment = new(previous, point);
        double minX = Math.Floor(segment.X - padding);
        double minY = Math.Floor(segment.Y - padding);
        double maxX = Math.Ceiling(segment.MaxX + padding);
        double maxY = Math.Ceiling(segment.MaxY + padding);
        double x = Math.Max(minX, bounds.X);
        double y = Math.Max(minY, bounds.Y);
        double right = Math.Min(maxX, bounds.MaxX);
        double bottom = Math.Min(maxY, bounds.MaxY);
        return right > x && bottom > y ? new Rect(x, y, right - x, bottom - y) : null;
    }

    private void Hover(Point point)
    {
        _pointer = point;
        bool content = false;
        if (_linearId is not null)
        {
            _linearPreview = point;
            content = true;
        }
        ImmutableArray<Anchor> hints = _attachHints;
        RefreshAttachHints(point);
        CanvasCursor cursor = _cursor;
        RefreshCursor();
        if (content || !hints.SequenceEqual(_attachHints) || cursor != _cursor)
        {
            Publish((content ? CanvasChangeKinds.Content : CanvasChangeKinds.None) | CanvasChangeKinds.Adornments | CanvasChangeKinds.Cursor,
                    null);
        }
    }

    /// <summary>Mac mouseDragged, for every sample of the event.</summary>
    private void Drag(in PointerEvent e, ReadOnlySpan<PointerSample> samples)
    {
        if (_gesture is not { } gesture || samples.IsEmpty)
        {
            return;
        }
        Point point = samples[^1].Position;
        _pointer = point;
        bool shift = (e.Modifiers & KeyModifiers.Shift) != 0;
        Rect? dirty = null;
        var kinds = CanvasChangeKinds.Content;
        switch (gesture.Kind)
        {
            case GestureKind.Create when _draft.Tool.IsFreehand:
                if (_draft.Tool == Tool.Highlighter && shift)
                {
                    StraightHighlight(gesture.Start, point);
                }
                else
                {
                    bool incremental = gesture.IncrementalInk && _draft.Rotation == 0;
                    foreach (PointerSample sample in samples)
                    {
                        if (incremental && _draft.Points.Count > 0)
                        {
                            dirty = Union(dirty, IncrementalInkRect(_draft.Points[^1], sample.Position, gesture.Bounds), ref incremental);
                        }
                        AppendSample(sample.Position, SamplePressure(sample.Position, sample, e.HasPressure));
                    }
                    _draft.GeometryToken = Guid.NewGuid();
                    if (!incremental)
                    {
                        dirty = null;
                    }
                    RequestSmartPreview();
                }
                break;
            case GestureKind.Create:
                UpdateShapeDraft(gesture.Start, point, shift);
                break;
            case GestureKind.Move:
                MoveSelection(gesture, point);
                break;
            case GestureKind.Resize:
                ResizeOne(gesture, point, shift);
                break;
            case GestureKind.Rotate:
                RotateOne(gesture, point);
                break;
            case GestureKind.ResizeSelection:
                ResizeSelection(gesture, point);
                break;
            case GestureKind.RotateSelection:
                RotateSelection(gesture, point);
                break;
            case GestureKind.Vertex:
                MoveVertex(gesture, point, shift);
                break;
            case GestureKind.Marquee:
                _marquee = new Rect(gesture.Start, point);
                kinds = CanvasChangeKinds.Adornments;
                break;
            case GestureKind.Erase:
                foreach (PointerSample sample in samples)
                {
                    CollectErase(sample.Position);
                }
                kinds = CanvasChangeKinds.Adornments;
                break;
        }
        if (_gesture is null)
        {
            return;
        }
        if (gesture.Kind is not GestureKind.Create)
        {
            _shownStale = true;
        }
        if (gesture.Kind is GestureKind.Create && _draft.Tool.IsConnector || gesture.Kind is GestureKind.Vertex)
        {
            kinds |= CanvasChangeKinds.Adornments;
        }
        RefreshAttachHints(point);
        if (gesture.Kind is GestureKind.Rotate or GestureKind.RotateSelection)
        {
            _cursor = CanvasCursor.Rotation;
        }
        Publish(kinds, dirty);
    }

    private static Rect? Union(Rect? total, Rect? next, ref bool incremental)
    {
        if (next is not { } rect)
        {
            incremental = false;
            return null;
        }
        return total is { } existing ? existing.Union(rect) : rect;
    }

    /// <summary>Mac mouseDragged for shapes, lines, arrows, rulers and covers: Shift squares shapes and snaps lines to 45 degrees.</summary>
    private void UpdateShapeDraft(Point start, Point point, bool shift)
    {
        Tool tool = _draft.Tool;
        Point finish = point;
        if (shift)
        {
            finish = tool.IsConnector || tool == Tool.Ruler ? DragConstraint.SnappedLineEnd(start, point) : DragConstraint.SquareEnd(start, point);
        }
        if (tool.SystemShapeAspectRatio is { } ratio)
        {
            finish = DragConstraint.AspectEnd(start, finish, ratio);
        }
        _draft.Frame = new Rect(start, finish);
        if (tool.IsConnector || tool == Tool.Ruler)
        {
            _draft.Points = [start, finish];
        }
        if (tool == Tool.Ruler)
        {
            _draft.Text = RulerText(start, finish);
        }
        _draft.Revision = unchecked(_draft.Revision + 1);
        _draft.InvalidateGeometry();
    }

    /// <summary>The ruler's label: the distance in physical pixels of this display.</summary>
    private string RulerText(Point start, Point finish) =>
        ((long)(start.Distance(finish) * DisplayInfo.Scale)).ToString(CultureInfo.InvariantCulture) + " px";

    private void MoveSelection(Gesture gesture, Point point)
    {
        Dictionary<Guid, Element> originals = gesture.Originals!;
        Point offset = point - gesture.Start;
        _work.EditSelection((ref Element element) =>
        {
            if (originals.TryGetValue(element.Id, out Element original))
            {
                element = original;
                element.Translate(offset);
            }
        });
        PreviewDisplayDrag(gesture, point);
    }

    private void ResizeOne(Gesture gesture, Point point, bool shift)
    {
        Element original = gesture.Original;
        Point anchor = gesture.Anchor;
        Point local = point.Rotated(original.Frame.Center, -original.Rotation);
        if ((shift || original.Tool.SystemShapeAspectRatio is not null) && original.Frame.Width > 0 && original.Frame.Height > 0)
        {
            Point delta = local - anchor;
            double scale = Math.Max(Math.Abs(delta.X) / original.Frame.Width, Math.Abs(delta.Y) / original.Frame.Height);
            double xSign = delta.X == 0 ? original.Frame.Center.X - anchor.X : delta.X;
            double ySign = delta.Y == 0 ? original.Frame.Center.Y - anchor.Y : delta.Y;
            local = anchor + new Point((xSign < 0 ? -1 : 1) * original.Frame.Width * scale, (ySign < 0 ? -1 : 1) * original.Frame.Height * scale);
        }
        var frame = new Rect(anchor, local);
        Point fixedWorld = anchor.Rotated(original.Frame.Center, original.Rotation);
        Point movedWorld = anchor.Rotated(frame.Center, original.Rotation);
        double scaleFactor = DisplayInfo.Scale;
        _work.Update(gesture.Id, (ref Element element) =>
        {
            element = original;
            element.Resize(frame);
            element.Translate(fixedWorld - movedWorld);
            if (element.Tool == Tool.Ruler && !element.Points.IsEmpty)
            {
                element.Text = ((long)(element.Points[0].Distance(element.Points[^1]) * scaleFactor)).ToString(CultureInfo.InvariantCulture) + " px";
            }
        });
    }

    private void RotateOne(Gesture gesture, Point point)
    {
        double rotation = gesture.InitialRotation + Math.Atan2(point.Y - gesture.Anchor.Y, point.X - gesture.Anchor.X) - gesture.InitialAngle;
        _work.Update(gesture.Id, (ref Element element) => element.Rotation = rotation);
    }

    private void ResizeSelection(Gesture gesture, Point point)
    {
        Point diagonal = gesture.Corner - gesture.Anchor;
        double squared = diagonal.X * diagonal.X + diagonal.Y * diagonal.Y;
        if (!(squared > 0))
        {
            return;
        }
        Point delta = point - gesture.Anchor;
        double scale = Math.Max(0.01, (delta.X * diagonal.X + delta.Y * diagonal.Y) / squared);
        Dictionary<Guid, Element> originals = gesture.Originals!;
        Point anchor = gesture.Anchor;
        double scaleFactor = DisplayInfo.Scale;
        _work.EditSelection((ref Element element) =>
        {
            if (!originals.TryGetValue(element.Id, out Element original))
            {
                return;
            }
            element = original;
            Point origin = anchor + (original.Frame.Origin - anchor) * scale;
            element.Resize(new Rect(origin.X, origin.Y, original.Frame.Width * scale, original.Frame.Height * scale));
            if (element.Tool == Tool.Text)
            {
                element.Style.FontSize = Math.Min(1024, Math.Max(4, original.Style.FontSize * scale));
            }
            if (element.Tool == Tool.Ruler && !element.Points.IsEmpty)
            {
                element.Text = ((long)(element.Points[0].Distance(element.Points[^1]) * scaleFactor)).ToString(CultureInfo.InvariantCulture) + " px";
            }
        });
    }

    private void RotateSelection(Gesture gesture, Point point)
    {
        Point center = gesture.Anchor;
        double angle = Math.Atan2(point.Y - center.Y, point.X - center.X) - gesture.InitialAngle;
        Dictionary<Guid, Element> originals = gesture.Originals!;
        _work.EditSelection((ref Element element) =>
        {
            if (!originals.TryGetValue(element.Id, out Element original))
            {
                return;
            }
            element = original;
            if (original.Tool.IsConnector)
            {
                var points = new Point[original.Points.Count];
                for (int index = 0; index < points.Length; index++)
                {
                    points[index] = original.Points[index].Rotated(original.Frame.Center, original.Rotation).Rotated(center, angle);
                }
                element.Points = ValueList.Create<Point>(points);
                element.Rotation = 0;
                element.Frame = Rect.Enclosing(points);
            }
            else
            {
                element.Translate(original.Frame.Center.Rotated(center, angle) - original.Frame.Center);
                element.Rotation = original.Rotation + angle;
            }
        });
    }

    private void MoveVertex(Gesture gesture, Point point, bool shift)
    {
        Element original = gesture.Original;
        int index = gesture.Index;
        if (shift && original.Points.Count >= 2 && (index == 0 || index == original.Points.Count - 1))
        {
            Point anchor = original.Points[index == 0 ? original.Points.Count - 1 : 0];
            Point direction = original.Points[index] - anchor;
            double lengthSquared = direction.X * direction.X + direction.Y * direction.Y;
            if (lengthSquared > 0)
            {
                Point local = point.Rotated(original.Frame.Center, -original.Rotation) - anchor;
                double scale = (local.X * direction.X + local.Y * direction.Y) / lengthSquared;
                var points = new Point[original.Points.Count];
                for (int position = 0; position < points.Length; position++)
                {
                    points[position] = anchor + (original.Points[position] - anchor) * scale;
                }
                Rect frame = Rect.Enclosing(points);
                Point translation = anchor.Rotated(original.Frame.Center, original.Rotation) - anchor.Rotated(frame.Center, original.Rotation);
                _work.Update(gesture.Id, (ref Element element) =>
                {
                    element = original;
                    element.Points = ValueList.Create<Point>(points);
                    element.Frame = frame;
                    element.Translate(translation);
                    if (index == 0)
                    {
                        element.StartAnchor = null;
                    }
                    else
                    {
                        element.EndAnchor = null;
                    }
                });
                return;
            }
        }
        _work.Update(gesture.Id, (ref Element element) =>
        {
            element.Points = element.Points.SetItem(index, point);
            element.Frame = Rect.Enclosing(element.Points);
            if (index == 0)
            {
                element.StartAnchor = null;
            }
            if (index == element.Points.Count - 1)
            {
                element.EndAnchor = null;
            }
        });
    }

    /// <summary>Mac collectErase: a frontmost cover alone, or every unlocked drawing the eraser touches.</summary>
    private void CollectErase(Point point)
    {
        ElementList elements = _work.Elements;
        if (ElementStacking.Frontmost(point, elements) is { } front && front.Tool.IsProtection)
        {
            if (!front.Locked && !_eraseIds.Contains(front.Id))
            {
                _eraseIds = _eraseIds.Add(front.Id);
            }
            return;
        }
        double tolerance = Workspace.Style.Width;
        ImmutableHashSet<Guid>.Builder? added = null;
        foreach (ref readonly Element element in elements)
        {
            if (!element.Tool.IsProtection && !element.Locked && !_eraseIds.Contains(element.Id) && HitTesting.Contains(point, element, tolerance))
            {
                added ??= _eraseIds.ToBuilder();
                added.Add(element.Id);
            }
        }
        if (added is not null)
        {
            _eraseIds = added.ToImmutable();
        }
    }

    /// <summary>Mac refreshAttachHints: the hints mirror the attach rules used on release.</summary>
    private void RefreshAttachHints(Point point)
    {
        if (!IsDrawing || !_hasDocument)
        {
            SetAttachHints([]);
            return;
        }
        ref CanvasDocument document = ref Current;
        Anchor? first = null;
        Anchor? second = null;
        if (_gesture is { Kind: GestureKind.Create } create)
        {
            if (_hasDraft && _draft.Tool.IsConnector)
            {
                first = document.NearestAnchor(create.Start, create.Id);
                second = document.NearestAnchor(_draft.Points.IsEmpty ? point : _draft.Points[^1], create.Id);
            }
        }
        else if (_gesture is { Kind: GestureKind.Vertex } vertex)
        {
            if (vertex.Original.Tool.IsConnector && (vertex.Index == 0 || vertex.Index == vertex.Original.Points.Count - 1)
                && document.Elements.IndexOf(vertex.Id) is var position and >= 0
                && vertex.Index < document.Elements[position].Points.Count)
            {
                first = document.NearestAnchor(document.Elements[position].Points[vertex.Index], vertex.Id);
            }
        }
        else if (_gesture is null)
        {
            if (_linearId is { } linear && document.Elements.IndexOf(linear) is var index and >= 0)
            {
                ValueList<Point> points = document.Elements[index].Points;
                if (!points.IsEmpty)
                {
                    first = document.NearestAnchor(points[0], linear);
                }
                second = document.NearestAnchor(point, linear);
            }
            else if (Workspace.Tool.IsConnector)
            {
                first = document.NearestAnchor(point, Guid.NewGuid());
            }
        }
        if (first is null && second is null)
        {
            SetAttachHints([]);
            return;
        }
        ImmutableArray<Anchor> hints = first is { } a && second is { } b ? [a, b] : [(first ?? second)!.Value];
        SetAttachHints(hints);
    }

    /// <summary>Mac mouseUp.</summary>
    private void Up(in PointerEvent e)
    {
        if (_gesture is not { } gesture)
        {
            return;
        }
        if (e.Samples.Length > 1)
        {
            Drag(e, e.Samples[..^1]);
            if (_gesture is null)
            {
                return;
            }
        }
        Point point = e.Position;
        _pointer = point;
        bool shift = (e.Modifiers & KeyModifiers.Shift) != 0;
        Element? heldPreview = Workspace.SmartDraw ? _smartPreview : null;
        CancelSmartPreview();
        if (_regionRequest is { } request)
        {
            Rect selected = _marquee ?? Rect.Zero;
            _regionRequest = null;
            _gesture = null;
            _marquee = null;
            CancelWork();
            Publish(CanvasChangeKinds.Adornments | CanvasChangeKinds.Edit, null);
            request(selected);
            return;
        }
        Tool? toolAfter = null;
        ElementList erased = ElementList.Empty;
        Element? stroke = null;
        switch (gesture.Kind)
        {
            case GestureKind.Move:
                Drag(e, e.Samples[^1..]);
                if (_gesture is null)
                {
                    return;
                }
                if (FinishDisplayDrag(gesture, point))
                {
                    return;
                }
                break;
            case GestureKind.Create:
                if (_draft.Tool == Tool.Arrow && gesture.Start.Distance(point) < 4)
                {
                    StartClickedArrow(gesture, point);
                    return;
                }
                if (_draft.Tool == Tool.Highlighter && shift)
                {
                    StraightHighlight(gesture.Start, point);
                }
                else if (_draft.Tool.IsFreehand && _draft.Points[^1] != point)
                {
                    AppendSample(point, SamplePressure(point, e.Current, e.HasPressure));
                    _draft.GeometryToken = Guid.NewGuid();
                }
                if (_draft.Tool == Tool.Pen && _inkPressure is not null)
                {
                    _draft.Pressures = InkPressure.TaperEnd(_draft.Points, _draft.Pressures, _draft.Style.Width);
                }
                _inkPressure = null;
                Element created = _draft;
                _hasDraft = false;
                _draft = default;
                _work.Add(created);
                if (created.Tool.IsConnector && !created.Points.IsEmpty)
                {
                    Anchor? start = _work.NearestAnchor(created.Points[0], created.Id);
                    Anchor? end = _work.NearestAnchor(created.Points[^1], created.Id);
                    _work.Update(created.Id, (ref Element element) =>
                    {
                        element.StartAnchor = start;
                        element.EndAnchor = end;
                    });
                }
                if (!created.Tool.IsFreehand)
                {
                    _work.Select([created.Id]);
                    toolAfter = Tool.Select;
                }
                else
                {
                    stroke = created;
                }
                break;
            case GestureKind.Marquee:
                if (_marquee is { } marquee)
                {
                    var inside = new List<Guid>();
                    foreach (ref readonly Element element in _work.Elements)
                    {
                        if (marquee.Contains(element.Frame))
                        {
                            inside.Add(element.Id);
                        }
                    }
                    _work.Select(inside);
                }
                break;
            case GestureKind.Erase:
                ImmutableHashSet<Guid> ids = _eraseIds;
                erased = _work.Elements.Filter(element => ids.Contains(element.Id) && !element.Tool.IsProtection);
                _work.Select(ids);
                _work.DeleteSelection(includingProtection: true);
                break;
            case GestureKind.Vertex:
                FinishVertex(gesture);
                break;
        }
        SetAttachHints([]);
        _gesture = null;
        _marquee = null;
        _eraseIds = ImmutableHashSet<Guid>.Empty;
        SubmitWork(toolAfter);
        if (stroke is { } ink)
        {
            ScheduleRecognition(ink, heldPreview);
        }
        BeginEraseFade(erased);
        RefreshCursor();
        Publish(CanvasChangeKinds.Content | CanvasChangeKinds.Selection | CanvasChangeKinds.Adornments | CanvasChangeKinds.Edit
                | CanvasChangeKinds.Cursor, null);
    }

    private void FinishVertex(Gesture gesture)
    {
        int position = _work.Elements.IndexOf(gesture.Id);
        if (position < 0)
        {
            return;
        }
        Element element = _work.Elements[position];
        int index = gesture.Index;
        if (!element.Tool.IsConnector || !(index == 0 || index == element.Points.Count - 1) || index >= gesture.Original.Points.Count
            || element.Points[index] == gesture.Original.Points[index])
        {
            return;
        }
        Anchor? anchor = _work.NearestAnchor(element.Points[index], gesture.Id);
        if (anchor != (index == 0 ? element.StartAnchor : element.EndAnchor))
        {
            _work.Update(gesture.Id, (ref Element changed) =>
            {
                if (index == 0)
                {
                    changed.StartAnchor = anchor;
                }
                else
                {
                    changed.EndAnchor = anchor;
                }
            });
        }
    }

    /// <summary>A click without a drag starts a clicked arrow: each click adds a point (Mac mouseUp with the arrow tool).</summary>
    private void StartClickedArrow(Gesture gesture, Point point)
    {
        Element arrow = _draft;
        arrow.Points = [gesture.Start];
        arrow.Frame = new Rect(gesture.Start, gesture.Start);
        arrow.Revision = unchecked(arrow.Revision + 1);
        arrow.InvalidateGeometry();
        _hasDraft = false;
        _draft = default;
        _work.Add(arrow);
        _linearId = arrow.Id;
        _linearPreview = point;
        _gesture = null;
        ReportEdit(EditKind.Path);
        Status("Click to add arrow points. Enter or double-click finishes; Escape cancels.");
        _shownStale = true;
        Publish(CanvasChangeKinds.Content | CanvasChangeKinds.Edit, null);
    }

    /// <summary>Mac constructionPreview: the clicked arrow with the pointer as its next point.</summary>
    private Element? ConstructionPreview
    {
        get
        {
            if (_linearId is not { } id || _linearPreview is not { } point || !_working || _work.Elements.IndexOf(id) is not (var index and >= 0))
            {
                return null;
            }
            Element element = _work.Elements[index];
            if (element.Points[^1] != point)
            {
                element.Points = element.Points.Add(point);
            }
            if (element.Points.Count < 2)
            {
                return null;
            }
            element.Frame = Rect.Enclosing(element.Points);
            element.InvalidateGeometry();
            return element;
        }
    }

    /// <summary>The interruption result of a clicked arrow: what finishing it now would leave (Mac finishLinearPath).</summary>
    private void ReportLinearPath()
    {
        if (_linearId is not { } id || _work.Elements.IndexOf(id) is not (var index and >= 0))
        {
            return;
        }
        Element element = _work.Elements[index];
        if (element.Points.Count < 2)
        {
            return;
        }
        CanvasDocument finished = _work;
        Anchor? start = finished.NearestAnchor(element.Points[0], id);
        Anchor? end = finished.NearestAnchor(element.Points[^1], id);
        finished.Update(id, (ref Element changed) =>
        {
            changed.StartAnchor = start;
            changed.EndAnchor = end;
        });
        finished.Commit();
        finished.Select([id]);
        _channel.UpdateEdit(Display, _editBase, finished);
    }

    /// <summary>Mac finishLinearPath: Enter, a double-click or Finish arrow ends a clicked arrow with its attachments and selects it.</summary>
    public void FinishLinearPath()
    {
        if (_linearId is not { } id || !_working)
        {
            return;
        }
        try
        {
            if (_work.Elements.IndexOf(id) is not (var index and >= 0) || _work.Elements[index].Points.Count < 2)
            {
                CancelGesture();
                return;
            }
            Element element = _work.Elements[index];
            Anchor? start = _work.NearestAnchor(element.Points[0], id);
            Anchor? end = _work.NearestAnchor(element.Points[^1], id);
            _work.Update(id, (ref Element changed) =>
            {
                changed.StartAnchor = start;
                changed.EndAnchor = end;
            });
            _work.Commit();
            _work.Select([id]);
            _linearId = null;
            _linearPreview = null;
            SetAttachHints([]);
            SubmitWork(Tool.Select);
            Publish(CanvasChangeKinds.Content | CanvasChangeKinds.Selection | CanvasChangeKinds.Adornments | CanvasChangeKinds.Edit, null);
        }
        catch (CanvasError error)
        {
            Fail(error);
        }
    }

    /// <summary>Mac previewDisplayDrag: shows the moved objects on every other display they reach.</summary>
    private void PreviewDisplayDrag(Gesture gesture, Point point)
    {
        if (_options.Coordinator is not { } coordinator)
        {
            return;
        }
        coordinator.ClearDragPreviews(Display);
        DisplayDescriptor source = DisplayInfo;
        HashSet<Guid> ids = UnlockedIds(gesture);
        Point desktop = source.ToDesktop(point);
        foreach (DisplaySnapshot destination in Workspace.Displays)
        {
            if (destination.Id == Display)
            {
                continue;
            }
            Point offset = DisplayTransfer.Offset(source, destination.Display, desktop);
            ElementList moved = DisplayTransfer.MovedElements(_work, ids, offset);
            Rect bounds = destination.Display.Bounds;
            bool reaches = false;
            foreach (ref readonly Element element in moved)
            {
                if (Intersects(Bounds.DrawingBounds(element), bounds))
                {
                    reaches = true;
                    break;
                }
            }
            if (!reaches)
            {
                continue;
            }
            if (coordinator.Find(destination.Id) is { HasActiveEdit: true } || destination.ActiveEdit is not null)
            {
                throw CanvasError.InvalidDocument("Finish the edit on the other display before moving these objects.");
            }
            coordinator.ShowDragPreview(destination.Id, moved);
        }
    }

    private static HashSet<Guid> UnlockedIds(Gesture gesture)
    {
        var ids = new HashSet<Guid>();
        foreach ((Guid id, Element element) in gesture.Originals!)
        {
            if (!element.Locked)
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    private static bool Intersects(Rect a, Rect b) => a.X < b.MaxX && b.X < a.MaxX && a.Y < b.MaxY && b.Y < a.MaxY;

    /// <summary>
    /// Mac finishDisplayDrag: a drop on another display moves the unlocked objects there as one coordinated Undo step,
    /// keeping the drop point on screen; a drop outside every display restores both documents. Returns true when the
    /// drop was handled here.
    /// </summary>
    private bool FinishDisplayDrag(Gesture gesture, Point point)
    {
        _options.Coordinator?.ClearDragPreviews(Display);
        DisplayDescriptor source = DisplayInfo;
        Point desktop = source.ToDesktop(point);
        DisplaySnapshot? destination = null;
        foreach (DisplaySnapshot candidate in Workspace.Displays)
        {
            if (candidate.Display.ContainsDesktopPoint(desktop))
            {
                destination = candidate;
                break;
            }
        }
        if (destination is null)
        {
            _work.Cancel();
            _gesture = null;
            SubmitWork();
            Publish(CanvasChangeKinds.Content | CanvasChangeKinds.Selection | CanvasChangeKinds.Edit, null);
            return true;
        }
        if (destination.Id == Display)
        {
            return false;
        }
        HashSet<Guid> ids = UnlockedIds(gesture);
        ElementList moved = DisplayTransfer.MovedElements(_work, ids, DisplayTransfer.Offset(source, destination.Display, desktop));
        if (moved.IsEmpty)
        {
            return false;
        }
        _work.Cancel();
        _gesture = null;
        Guid operation = Guid.NewGuid();
        CanvasDocument departure = _work;
        CanvasDocument arrival = destination.Document;
        var movedIds = moved.Select(element => element.Id).ToArray();
        departure.Begin();
        departure.Select(movedIds);
        departure.DeleteSelection(includingProtection: true);
        arrival.Begin();
        foreach (ref readonly Element element in moved)
        {
            arrival.Add(element);
        }
        arrival.Select(movedIds);
        departure.Commit(operation);
        arrival.Commit(operation);
        _channel.Submit(new EditSubmission(
            [new DisplayEdit(destination.Id, destination.Version, arrival), new DisplayEdit(Display, _editBase, departure)], operation)
        {
            Origin = Display,
        });
        _document = departure;
        _version = _editBase.Next;
        _working = false;
        _work = default;
        _reported = null;
        _shownStale = true;
        RefreshCursor();
        Publish(CanvasChangeKinds.Content | CanvasChangeKinds.Selection | CanvasChangeKinds.Edit | CanvasChangeKinds.Cursor, null);
        return true;
    }
}
