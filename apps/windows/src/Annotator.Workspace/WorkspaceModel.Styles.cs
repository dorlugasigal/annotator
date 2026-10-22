using Annotator.Core;

namespace Annotator.Workspace;

/// <summary>Changes a style in place (paint changes: color, fill, gradients, opacity).</summary>
public delegate void StyleMutation(ref Style style);

public sealed partial class WorkspaceModel
{
    /// <summary>
    /// Mac choose(_:): selects a tool, shows its saved style and enters Draw. Choosing a drawing tool clears
    /// the selection and gives focus back to the ready canvas (spec 009, WIN-OVR-03). Cover stands for Effects and chooses
    /// the last effect (Mac chooseCover); choosing an effect remembers it and resumes live effects (Mac
    /// <c>presentation?.retry()</c>, <see cref="WorkspaceSnapshot.EffectsRetry"/>). Paste image pastes the clipboard's image
    /// and keeps the tool (Mac pasteImage).
    /// </summary>
    public ToolChoice Choose(Tool tool)
    {
        using ChangeScope scope = Change();
        if (tool == Tool.Cover)
        {
            tool = PreferredEffect(_preferences);
        }
        if (!IsAvailable(tool))
        {
            return ToolChoice.Unavailable;
        }
        if (tool == Tool.Image)
        {
            // Mac choose(.image): Paste image is an action, not a tool to draw with.
            PasteImage();
            return ToolChoice.Chosen;
        }
        if (tool == Tool.Text && !_textEditing)
        {
            _status = TextUnavailableMessage;
            return ToolChoice.Unavailable;
        }
        FinishStyleEditCore();
        if (_active is { } current)
        {
            CommitTextIfNeeded(current);
            FinishLinearPath(current);
            CancelGesture(current);
        }
        // Mac choose(_:): an effect needs screen access first (ensureCapturePermission); it is chosen once Windows allows it.
        if (tool.IsPixelEffect && !Ensure(Capture.ScreenAccessFeature.Effects, () => Choose(tool)))
        {
            return ToolChoice.NeedsScreenAccess;
        }
        _tool = tool;
        RecordUsage(Metrics.UsageMetricExtensions.ForTool(tool));
        if (tool.IsProtection)
        {
            _preferences.LastCoverMode = tool;
            PersistPreferences();
        }
        if (tool != Tool.Select && _active is { } active)
        {
            UseLocalSelection(active);
            if (!active.Document.Selection.IsEmpty)
            {
                Mutate(active, (ref CanvasDocument document) => document.Select([]));
            }
        }
        _style = StyleFor(tool);
        if (_mode != WorkspaceMode.Draw)
        {
            BeginDrawing(recordsUsage: false);
        }
        if (tool.IsPixelEffect)
        {
            _effectsRetry++;
        }
        Synchronize();
        if (_mode == WorkspaceMode.Draw && !_hidden && _active is { } target)
        {
            RequestFocus(target);
        }
        return ToolChoice.Chosen;
    }

    /// <summary>
    /// An unmodified ink color key (r, g, b, y, o, p, c, w, k) changes the stroke color of the tool or selection.
    /// Returns false when <paramref name="key"/> is not an ink key.
    /// </summary>
    public bool ApplyInkKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!InkShortcut.Choices.TryGetValue(key, out InkShortcut ink))
        {
            return false;
        }
        using ChangeScope scope = Change();
        List<Element> selected = SelectedElements();
        Tool effective = EffectiveTool(selected);
        if (effective.IsProtection || effective == Tool.Image)
        {
            _status = "Color keys apply to drawing and text tools.";
            return true;
        }
        ApplyStyle(static _ => true, (ref Style style) => style.SetPaintColor(ink.Color, fill: false));
        _status = $"{ink.Name} ink";
        return true;
    }

    /// <summary>A palette choice for the stroke or fill (Mac InspectorColorPalette.choose).</summary>
    public void SetPaintColor(RGBA? color, bool fill, bool useColorAlpha = false)
    {
        using ChangeScope scope = Change();
        ApplyStyle(element => !fill || element.Tool.SupportsFill, (ref Style style) => style.SetPaintColor(color, fill, useColorAlpha));
    }

    /// <summary>
    /// Mac applyStyle: changes the style of the selection, or of the tool when nothing is selected, and saves it as the tool's
    /// style. <paramref name="supports"/> limits which selected objects change (for example fill only on fillable shapes).
    /// While a canvas edits text, the text takes the change.
    /// </summary>
    public void ApplyStyle(StyleMutation mutation, Func<Element, bool>? supports = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        using ChangeScope scope = Change();
        ApplyStyle(supports ?? (static _ => true), mutation);
    }

    /// <summary>Starts one Undo step for style changes to the selection, for example while the color palette is open.</summary>
    public void BeginStyleEdit()
    {
        using ChangeScope scope = Change();
        BeginStyleEditCore();
    }

    /// <summary>Ends the style Undo step; a workspace-wide one becomes one coordinated step.</summary>
    public void FinishStyleEdit()
    {
        using ChangeScope scope = Change();
        FinishStyleEditCore();
    }

    private void BeginStyleEditCore()
    {
        // A canvas that edits text applies style changes to its text itself (Mac applyTextStyle).
        List<Canvas> targets = [.. SelectionSessions().Where(canvas => canvas.Edit is not { Kind: EditKind.Text })];
        if (_styleSessions.Count != 0 || SelectedElements().Count == 0 || targets.Count == 0
            || targets.Any(canvas => canvas.Document.IsEditing))
        {
            return;
        }
        foreach (Canvas canvas in targets)
        {
            CancelGesture(canvas);
            Mutate(canvas, (ref CanvasDocument document) => document.Begin());
        }
        _styleSessions = targets;
    }

    private void FinishStyleEditCore()
    {
        List<Canvas> targets = _styleSessions;
        if (targets.Count == 0)
        {
            return;
        }
        _styleSessions = [];
        try
        {
            if (!targets.Any(canvas => canvas.Document.HasPendingChanges))
            {
                foreach (Canvas canvas in targets.Where(canvas => canvas.Document.IsEditing))
                {
                    Mutate(canvas, (ref CanvasDocument document) => document.Commit());
                }
            }
            else
            {
                CommitWorkspaceDocuments([.. targets.Select(canvas => canvas.Document)], targets);
            }
        }
        catch (Exception error) when (IsReportable(error))
        {
            foreach (Canvas canvas in targets)
            {
                Mutate(canvas, (ref CanvasDocument document) => document.Cancel());
            }
            Report(error);
        }
        Synchronize();
    }

    /// <summary>
    /// Mac applyStyle: a change of text size, font or weight also resizes text objects to their text, measured on this
    /// platform (<see cref="WorkspaceOptions.TextMeasurer"/>).
    /// </summary>
    private void ApplyStyle(Func<Element, bool> supports, StyleMutation mutation)
    {
        Style changed = _style;
        List<Element> selected = SelectedElements();
        bool editsMixedSelection = EffectiveTool(selected) == Tool.Select;
        mutation(ref changed);
        bool ownsBatch = false;
        try
        {
            changed.Validate();
            if (_workspaceWide && _styleSessions.Count == 0)
            {
                BeginStyleEditCore();
                ownsBatch = _styleSessions.Count != 0;
            }
            Style previous = _style;
            _style = changed;
            if (_active is { Edit: { Kind: EditKind.Text } textEdit } editing)
            {
                // The canvas that edits text applies the change to its text (Mac applyTextStyle); a label whose paint changes
                // stops following its shape's stroke (Mac applyStyle's changesPaint).
                bool changesPaint = changed.Color != previous.Color || changed.StrokeGradient != previous.StrokeGradient;
                editing.Edit = textEdit with
                {
                    TextStyle = changed,
                    Label = changesPaint && textEdit.Label is { FollowsStroke: true } label ? label with { FollowsStroke = false } : textEdit.Label,
                };
            }
            Tool effective = EffectiveTool(SelectedElements());
            if (effective != Tool.Select)
            {
                _preferences.ToolStyles = _preferences.ToolStyles.SetItem(effective.RawValue, changed);
                if (_preferences.ShareToolStyles == true && effective.SharesStyle)
                {
                    ShareStyleChange(effective, previous, changed);
                }
            }
            PersistPreferences();
            foreach (Canvas canvas in SelectionSessions().Where(canvas =>
                         !canvas.Document.Selection.IsEmpty && canvas.Edit is not { Kind: EditKind.Text }))
            {
                Mutate(canvas, (ref CanvasDocument document) => document.EditSelection((ref Element element) =>
                {
                    if (!supports(element) || (editsMixedSelection && element.Tool.IsProtection))
                    {
                        return;
                    }
                    Style before = element.Style;
                    mutation(ref element.Style);
                    if (element.Tool == Tool.Text && _textMeasurer is { } measurer &&
                        (before.FontSize != element.Style.FontSize || before.Bold != element.Style.Bold ||
                         !string.Equals(before.FontName, element.Style.FontName, StringComparison.Ordinal)))
                    {
                        Annotator.Core.Geometry.Size size = Annotator.Core.Geometry.TextLayout.ContentSize(element.Text,
                            Annotator.Core.Geometry.TextFont.Resolve(element.Style), measurer);
                        element.Frame = new Rect(element.Frame.X, element.Frame.Y, size.Width, size.Height);
                    }
                }));
            }
            if (ownsBatch)
            {
                FinishStyleEditCore();
            }
            else
            {
                Synchronize();
            }
        }
        catch (Exception error) when (IsReportable(error))
        {
            foreach (Canvas canvas in _styleSessions)
            {
                Mutate(canvas, (ref CanvasDocument document) => document.Cancel());
            }
            _styleSessions = [];
            Report(error);
            Synchronize();
        }
    }

    /// <summary>With "Share style between tools" on, copies what changed to every other drawing tool.</summary>
    private void ShareStyleChange(Tool effective, Style previous, Style changed)
    {
        foreach (Tool other in Tool.AllCases)
        {
            if (other == effective || !other.SharesStyle)
            {
                continue;
            }
            Style current = StyleFor(other);
            Style shared = current;
            shared.AdoptSharedChanges(previous, changed, other.SupportsFill);
            if (shared != current)
            {
                _preferences.ToolStyles = _preferences.ToolStyles.SetItem(other.RawValue, shared);
            }
        }
    }
}
