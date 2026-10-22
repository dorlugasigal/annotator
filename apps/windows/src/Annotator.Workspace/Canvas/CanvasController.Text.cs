using Annotator.Core;
using Annotator.Core.Geometry;

namespace Annotator.Workspace.Canvas;

public sealed partial class CanvasController
{
    private TextState? _text;

    /// <summary>The text or label being edited (Mac textElement, initialTextElement, editingShapeLabelID and initialShapeLabel).</summary>
    private sealed class TextState(Guid elementId, bool isLabel, Element? initial, ShapeLabel? initialLabel, string text)
    {
        public readonly Guid ElementId = elementId;
        public readonly bool IsLabel = isLabel;
        public readonly Element? Initial = initial;
        public readonly ShapeLabel? InitialLabel = initialLabel;
        public string Text = text;
        public Style? ReportedStyle;
        public LabelEdit? ReportedLabel;
        public CanvasTextSession Session = null!;
    }

    /// <summary>The text editor host's typing: the editor now holds <paramref name="text"/> (Mac textDidChange).</summary>
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_text is not { } state || state.Text == text)
        {
            return;
        }
        try
        {
            state.Text = text;
            RefreshTextSession(state);
            _options.TextEditor?.Update(state.Session);
            ReportTextResult();
            if (state.IsLabel)
            {
                _shownStale = true;
            }
            Publish(state.IsLabel ? CanvasChangeKinds.Text | CanvasChangeKinds.Content : CanvasChangeKinds.Text, null);
        }
        catch (CanvasError error)
        {
            Fail(error);
        }
    }

    /// <summary>
    /// Mac commitTextIfNeeded: ends text editing, keeping the text. Blank new text and unchanged text leave no object and no
    /// Undo step; an emptied label is removed; a label grows its shape to fit (12 points around the text).
    /// </summary>
    public void CommitText()
    {
        if (_text is null || !_working)
        {
            return;
        }
        try
        {
            _work = TextResult();
            EndTextSession();
            SubmitWork();
            _shownStale = true;
            Publish(CanvasChangeKinds.Content | CanvasChangeKinds.Selection | CanvasChangeKinds.Text | CanvasChangeKinds.Edit, null);
        }
        catch (CanvasError error)
        {
            Fail(error);
        }
    }

    /// <summary>Mac startText: edits the text under the pointer, the label of the shape under it, or new text there.</summary>
    private void StartText(Point point)
    {
        Element? target = ElementStacking.Frontmost(point, _document.Elements);
        if (target is { } locked && (locked.Tool.IsShape || locked.Tool == Tool.Text) && locked.Locked)
        {
            Status("Unlock this object to edit its text.");
            return;
        }
        if (target is { } owner && owner.Tool.IsShape)
        {
            StartShapeLabel(owner);
            return;
        }
        BeginWork(EditKind.Text);
        _work.Begin();
        Element? existing = null;
        foreach (ref readonly Element element in _work.Elements)
        {
            if (element.Tool == Tool.Text && !element.Locked && HitTesting.Contains(point, element))
            {
                existing = element;
            }
        }
        Style style = StyleFor(Tool.Text);
        Element text = existing ?? new Element(Tool.Text, new Rect(point.X, point.Y, 32, style.FontSize * 1.5), style: style);
        if (existing is null)
        {
            _work.Add(text);
        }
        _work.Select([text.Id]);
        InstallTextEditor(new TextState(text.Id, isLabel: false, existing, null, text.Text));
    }

    /// <summary>Mac startShapeLabel: a centered label that follows the shape's stroke, in the Text tool's style.</summary>
    private void StartShapeLabel(Element owner)
    {
        Style style = StyleFor(Tool.Text);
        style.Alignment = TextAlignment.Center;
        ShapeLabel label = owner.Label ?? new ShapeLabel(style: style, followsStroke: true);
        BeginWork(EditKind.Text);
        _work.Begin();
        if (owner.Label is null)
        {
            _work.Update(owner.Id, (ref Element element) => element.Label = label);
        }
        _work.Select([owner.Id]);
        if (ShapeLabelLayout.TextElement(owner, _options.TextMeasurer, label) is null)
        {
            throw CanvasError.InvalidGeometry;
        }
        InstallTextEditor(new TextState(owner.Id, isLabel: true, null, owner.Label, label.Text));
    }

    private void InstallTextEditor(TextState state)
    {
        _text = state;
        RefreshTextSession(state);
        _shownStale = true;
        ReportTextResult();
        _options.TextEditor?.Begin(this, state.Session);
    }

    /// <summary>Mac resizeTextEditor and installTextEditor: where the editor goes and how its text looks.</summary>
    private void RefreshTextSession(TextState state)
    {
        int index = _work.Elements.IndexOf(state.ElementId);
        if (index < 0)
        {
            throw CanvasError.MissingElement;
        }
        Element element = _work.Elements[index];
        if (state.IsLabel)
        {
            Style labelStyle = element.Label?.Style ?? new ShapeLabel().Style;
            var label = new ShapeLabel(state.Text, labelStyle, element.Label?.FollowsStroke);
            Element text = ShapeLabelLayout.TextElement(element, _options.TextMeasurer, label) ?? throw CanvasError.InvalidGeometry;
            Rect paint = element.Label?.FollowsStroke == true ? element.Frame : text.Frame;
            state.Session = new CanvasTextSession(element.Id, true, state.Text, text.Frame, element.Rotation, text.Style, paint,
                                                  "Shape label. Escape or Ctrl+Enter finishes editing.");
            return;
        }
        Size size = TextLayout.ContentSize(state.Text, TextFont.Resolve(element.Style), _options.TextMeasurer);
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height))
        {
            throw CanvasError.InvalidGeometry;
        }
        var frame = new Rect(element.Frame.X, element.Frame.Y, size.Width, size.Height);
        state.Session = new CanvasTextSession(element.Id, false, state.Text, frame, 0, element.Style, frame,
                                              "Annotation text. Escape or Ctrl+Enter finishes editing.");
    }

    /// <summary>The document that committing now would leave (Mac commitTextIfNeeded), not editing.</summary>
    private CanvasDocument TextResult()
    {
        TextState state = _text!;
        CanvasDocument result = _work;
        int index = result.Elements.IndexOf(state.ElementId);
        if (index < 0)
        {
            result.Cancel();
            return result;
        }
        Element element = result.Elements[index];
        if (state.IsLabel)
        {
            if (element.Label is not { } current)
            {
                result.Cancel();
                return result;
            }
            ShapeLabel? label = state.Text.Trim().Length == 0 ? null : new ShapeLabel(state.Text, current.Style, current.FollowsStroke);
            if (label == state.InitialLabel)
            {
                result.Cancel();
                result.Select([state.ElementId]);
                return result;
            }
            ITextMeasurer measurer = _options.TextMeasurer;
            result.Update(state.ElementId, (ref Element owner) =>
            {
                if (label is not null)
                {
                    owner.Frame = ShapeLabelLayout.FittedOwnerFrame(owner, label, measurer);
                }
                owner.Label = label;
            });
            result.Commit();
            return result;
        }
        bool emptyNewText = state.Initial is null && state.Text.Trim().Length == 0;
        bool unchangedExistingText = state.Initial is { } initial && state.Text == initial.Text && element.Equals(in initial);
        if (emptyNewText || unchangedExistingText)
        {
            result.Cancel();
            if (unchangedExistingText)
            {
                result.Select([state.ElementId]);
            }
            return result;
        }
        Rect frame = state.Session.Frame;
        string text = state.Text;
        result.Update(state.ElementId, (ref Element changed) =>
        {
            changed.Text = text;
            changed.Frame = frame;
        });
        result.Commit();
        return result;
    }

    /// <summary>
    /// Tells the workspace what an interruption should keep, and the style the toolbar shows while editing; for a shape label
    /// also whether it follows its shape's stroke and that stroke's paint (Match outline).
    /// </summary>
    private void ReportTextResult()
    {
        if (_text is not { } state || !_working)
        {
            return;
        }
        int index = _work.Elements.IndexOf(state.ElementId);
        if (index >= 0)
        {
            Element element = _work.Elements[index];
            Style style = state.IsLabel ? element.Label?.ResolvedStyle(element.Style) ?? element.Style : element.Style;
            LabelEdit? label = state.IsLabel
                ? new LabelEdit(element.Label?.FollowsStroke == true, element.Style.Color, element.Style.StrokeGradient)
                : null;
            if (state.ReportedStyle != style || state.ReportedLabel != label)
            {
                state.ReportedStyle = style;
                state.ReportedLabel = label;
                _channel.Send(new CanvasCommand(CanvasCommandKind.TextStyle, Display) { Style = style, Label = label });
            }
        }
        _channel.UpdateEdit(Display, _editBase, TextResult());
    }

    /// <summary>
    /// A style change on the toolbar while text is edited (Mac applyStyle with a text editor): the text takes the new style;
    /// a label that changes paint stops following its shape's stroke. Match outline (Mac matchLabelToStroke) makes the label
    /// follow the stroke again with its own style.
    /// </summary>
    private void FollowWorkspaceTextStyle()
    {
        if (_text is not { } state || !_working || _snapshot is not { ActiveEdit: EditKind.Text, TextStyle: { } held } display
            || (held == state.ReportedStyle && display.TextLabel == state.ReportedLabel))
        {
            return;
        }
        Style previous = state.ReportedStyle ?? held;
        LabelEdit? heldLabel = display.TextLabel;
        try
        {
            if (state.IsLabel)
            {
                bool changesPaint = held.Color != previous.Color || held.StrokeGradient != previous.StrokeGradient;
                _work.Update(state.ElementId, (ref Element owner) =>
                {
                    if (owner.Label is not { } label)
                    {
                        return;
                    }
                    if (heldLabel is { FollowsStroke: true } && label.FollowsStroke != true)
                    {
                        // Match outline: the label keeps its own style and follows the stroke again; style changes made on the
                        // toolbar after it, before this canvas saw them, come with it.
                        var matched = new ShapeLabel(label.Text, label.Style, followsStroke: true);
                        owner.Label = matched.ResolvedStyle(owner.Style) == held ? matched : new ShapeLabel(label.Text, held, followsStroke: true);
                        return;
                    }
                    bool unfollows = changesPaint || (heldLabel is { FollowsStroke: false } && label.FollowsStroke == true);
                    owner.Label = new ShapeLabel(label.Text, held, unfollows ? false : label.FollowsStroke);
                });
            }
            else
            {
                _work.Update(state.ElementId, (ref Element element) => element.Style = held);
            }
            state.ReportedStyle = held;
            state.ReportedLabel = heldLabel;
            RefreshTextSession(state);
            _options.TextEditor?.Update(state.Session);
            ReportTextResult();
            _shownStale = true;
        }
        catch (CanvasError error)
        {
            Fail(error);
        }
    }

    private void EndTextSession()
    {
        if (_text is null)
        {
            return;
        }
        _text = null;
        _options.TextEditor?.End();
    }

    private static bool TextContains(CanvasTextSession session, Point point)
    {
        Point local = session.Rotation == 0 ? point : point.Rotated(session.Frame.Center, -session.Rotation);
        return session.Frame.Contains(local);
    }
}
