using System.Numerics;
using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Platform;
using Annotator.Platform.Displays;
using Annotator.Platform.Text;
using Annotator.Workspace;
using Annotator.Workspace.Canvas;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Annotator.App.Overlay;

/// <summary>
/// The Windows half of one display's canvas text editor (spec 013 S-7 option 3, WIN-INP-09), on the overlay thread. Each
/// editing session gets a <see cref="CanvasTextEditor"/> connected to the Text Services Framework, so input methods, the
/// emoji panel, voice typing and the touch keyboard type into it; typed characters (WM_CHAR) still work when that
/// connection fails. The host also shows the blinking caret as a composition visual and gives the editor plain text on
/// the Windows clipboard. The canvas draws the text and <see cref="Decorations"/> in its live layer, routes keys,
/// characters and pointer events to <see cref="Editor"/> first, and describes it to UI Automation.
/// </summary>
internal sealed class CanvasTextEditorHost : ITextEditorHost, ITextStoreDocument, ITextClipboard, IDisposable
{
    private const string ConnectOperation = "Connect the text box to input methods";
    private const uint Infinite = uint.MaxValue;
    private const uint DefaultCaretTimeout = 5_000;

    private readonly OverlayCanvasContext _context;
    private readonly DrawingCanvasFactory _factory;
    private readonly double _doubleClickTime;
    private readonly Size _doubleClickSize;
    private readonly Action _changed;
    private readonly ContainerVisual _parent;
    private readonly SpriteVisual _caret;
    private readonly CompositionColorBrush _caretBrush;
    private readonly List<CompositionRun> _runs = [];
    private TextServicesSession? _session;
    private StepEasingFunction? _hold;
    private ScalarKeyFrameAnimation? _blink;
    private CaretShape? _caretShape;
    private Viewport? _view;
    private bool _hasFocus;
    private bool _reportedConnection;
    private bool _disposed;

    /// <param name="parent">Where the caret goes: above the drawings and below the canvas's own controls.</param>
    /// <param name="changed">The editor changed: draw it again and tell UI Automation.</param>
    public CanvasTextEditorHost(OverlayCanvasContext context, DrawingCanvasFactory factory, ContainerVisual parent, double doubleClickTime,
                                Size doubleClickSize, Action changed)
    {
        _context = context;
        _factory = factory;
        _parent = parent;
        _doubleClickTime = doubleClickTime;
        _doubleClickSize = doubleClickSize;
        _changed = changed;
        Compositor compositor = context.Compositor;
        _caretBrush = compositor.CreateColorBrush();
        _caret = compositor.CreateSpriteVisual();
        _caret.Brush = _caretBrush;
        _caret.IsVisible = false;
        parent.Children.InsertAtTop(_caret);
        _hasFocus = PInvoke.GetFocus() == new Windows.Win32.Foundation.HWND(context.Overlay.Handle);
    }

    /// <summary>The open editor, or null while no text is edited.</summary>
    public CanvasTextEditor? Editor { get; private set; }

    /// <summary>What to draw below and above the edited text, or null while no text is edited.</summary>
    public TextEditorDecorations? Decorations => Editor?.Decorations;

    /// <summary>An input method is changing the text now (the Text Services Framework holds the lock): draw afterwards.</summary>
    public bool InputMethodIsEditing => _session?.IsLocked == true;

    /// <summary>
    /// Screen zoom's viewport, or null without zoom (Mac textViewport): the caret and the text's place for input methods are
    /// shown through it, as the canvas shows the text.
    /// </summary>
    public Viewport? View
    {
        get => _view;
        set
        {
            if (value != _view)
            {
                _view = value;
                _caretShape = null;
                _session?.LayoutChanged();
            }
        }
    }

    public void Begin(CanvasController canvas, CanvasTextSession session)
    {
        if (_disposed)
        {
            return;
        }
        End();
        var editor = new CanvasTextEditor(canvas, session, _factory.TextLayout, this, _doubleClickTime, _doubleClickSize);
        editor.SetFocus(_hasFocus);
        editor.TextEdited += OnTextEdited;
        editor.SelectionMoved += OnSelectionMoved;
        editor.FinishCompositionRequested += OnFinishCompositionRequested;
        editor.Changed += OnEditorChanged;
        editor.Failed += OnFailed;
        Editor = editor;
        try
        {
            _session = TextServicesSession.Start(_context.Overlay.Handle, this);
            _session.Failed += OnInputMethodFailed;
        }
        catch (Exception error) when (error is NativeCallException or InvalidOperationException)
        {
            // Typed characters still arrive as WM_CHAR; only input methods, voice typing and the emoji panel are missing.
            if (!_reportedConnection)
            {
                _reportedConnection = true;
                _factory.Report(new OverlayEvent.Failure(ConnectOperation, error));
            }
        }
        _changed();
    }

    public void Update(CanvasTextSession session)
    {
        if (Editor is not { } editor)
        {
            return;
        }
        editor.Update(session);
        _session?.LayoutChanged();
    }

    public void End()
    {
        if (Editor is not { } editor)
        {
            return;
        }
        Editor = null;
        editor.TextEdited -= OnTextEdited;
        editor.SelectionMoved -= OnSelectionMoved;
        editor.FinishCompositionRequested -= OnFinishCompositionRequested;
        editor.Changed -= OnEditorChanged;
        editor.Failed -= OnFailed;
        editor.End();
        Disconnect();
        _changed();
    }

    /// <summary>The overlay window got or lost the keyboard focus: the caret shows only with it.</summary>
    public void SetFocus(bool focused)
    {
        _hasFocus = focused;
        Editor?.SetFocus(focused);
    }

    /// <summary>
    /// UI Automation sets the whole text (ValuePattern.SetValue), on its own thread: the editor takes it on the overlay
    /// thread, as one Undo step.
    /// </summary>
    public void SetValueFromAutomation(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string text = TextClipboard.EditorLineEnds(value);
        if (!_factory.Queue.TryEnqueue(() =>
            {
                if (!_disposed && Editor is { IsEnded: false } editor)
                {
                    editor.ReplaceAll(text);
                }
            }))
        {
            throw new InvalidOperationException("The text box is closing. Try again.");
        }
    }

    /// <summary>Shows the caret where the editor puts it, and restarts its blinking when it moved.</summary>
    public void UpdateCaret()
    {
        if (_disposed)
        {
            return;
        }
        if (Editor?.Caret is not { } caret)
        {
            if (_caretShape is not null)
            {
                _caretShape = null;
                _caret.StopAnimation(nameof(Visual.Opacity));
                _caret.IsVisible = false;
            }
            return;
        }
        CaretShape shape = Shape(caret);
        if (shape == _caretShape)
        {
            return;
        }
        _caretShape = shape;
        _caret.Offset = new Vector3(shape.Offset, 0);
        _caret.Size = shape.Size;
        // The caret turns with a rotated label around its top, which stays on the text's line.
        _caret.CenterPoint = new Vector3(shape.Size.X / 2, 0, 0);
        _caret.RotationAngle = shape.Rotation;
        _caretBrush.Color = shape.Color;
        _caret.IsVisible = true;
        Blink();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        End();
        _disposed = true;
        _caret.StopAnimation(nameof(Visual.Opacity));
        _parent.Children.Remove(_caret);
        _caret.Dispose();
        _caretBrush.Dispose();
        _blink?.Dispose();
        _hold?.Dispose();
    }

    // ITextClipboard: the editor's Cut, Copy and Paste. Failures carry the message the person reads.

    string? ITextClipboard.GetText() => TextClipboard.GetText(_context.Overlay.Handle);

    void ITextClipboard.SetText(string text) => TextClipboard.SetText(_context.Overlay.Handle, text);

    // ITextStoreDocument: the text as the Text Services Framework reads and changes it.

    int ITextStoreDocument.Length => Editor?.Text.Length ?? 0;

    void ITextStoreDocument.CopyText(int start, Span<char> destination) =>
        (Editor?.Text ?? string.Empty).AsSpan(start, destination.Length).CopyTo(destination);

    TextStoreSelection ITextStoreDocument.Selection => Editor is { } editor
        ? new TextStoreSelection(editor.Buffer.Selection.Start, editor.Buffer.Selection.End, editor.Buffer.Selection.ActiveAtStart,
                                 editor.Buffer.InterimCharacter)
        : default;

    void ITextStoreDocument.Select(TextStoreSelection selection) =>
        Editor?.SelectFromInputMethod(TextSelection.Range(selection.Start, selection.End, selection.ActiveAtStart), selection.InterimCharacter);

    TextStoreChange ITextStoreDocument.Replace(int start, int end, ReadOnlySpan<char> text)
    {
        if (Editor is not { } editor)
        {
            throw new InvalidOperationException("The text box is closed.");
        }
        TextChange change = editor.ReplaceFromInputMethod(start, end, text);
        return new TextStoreChange(change.Start, change.OldEnd, change.NewEnd);
    }

    PixelRect ITextStoreDocument.TextBounds(int start, int end) => Screen(Editor?.Bounds(start, end) ?? default);

    PixelRect ITextStoreDocument.ScreenBounds => Screen(Editor?.FrameBounds ?? default);

    int? ITextStoreDocument.OffsetAt(int x, int y, bool nearest)
    {
        if (Editor is not { } editor)
        {
            return null;
        }
        Point point = _context.Display.ToLocal(new Point(x + 0.5, y + 0.5));
        if (_view is { } view)
        {
            point = view.SourcePoint(point);
        }
        return nearest || editor.Contains(point) ? editor.HitTest(point) : null;
    }

    void ITextStoreDocument.CompositionStarted() => Editor?.CompositionStarted();

    void ITextStoreDocument.CompositionChanged(int start, int end) => Editor?.CompositionUpdated(start, end);

    void ITextStoreDocument.CompositionStyled(ReadOnlySpan<TextStoreAttributeRun> runs)
    {
        if (Editor is not { } editor)
        {
            return;
        }
        _runs.Clear();
        foreach (TextStoreAttributeRun run in runs)
        {
            // The text keeps its own color, as on the Mac; input methods choose the underline and the background.
            _runs.Add(new CompositionRun(run.Start, run.End, new CompositionStyle(
                run.Line switch
                {
                    TextStoreLine.Solid => CompositionLine.Solid,
                    TextStoreLine.Dot => CompositionLine.Dot,
                    TextStoreLine.Dash => CompositionLine.Dash,
                    TextStoreLine.Squiggle => CompositionLine.Squiggle,
                    _ => CompositionLine.None,
                },
                run.BoldLine,
                Color(run.LineColor),
                Color(run.BackgroundColor))));
        }
        editor.CompositionStyled([.. _runs]);
    }

    void ITextStoreDocument.CompositionEnded() => Editor?.CompositionEnded();

    private void OnTextEdited(TextChange change) => _session?.TextChanged(change.Start, change.OldEnd, change.NewEnd);

    private void OnSelectionMoved() => _session?.SelectionChanged();

    private void OnFinishCompositionRequested()
    {
        try
        {
            _session?.FinishComposition();
        }
        catch (NativeCallException error)
        {
            _factory.Report(new OverlayEvent.Failure("Keep the input method's text", error));
        }
    }

    private void OnEditorChanged()
    {
        _session?.LayoutChanged();
        _changed();
    }

    private void OnFailed(string message) =>
        _context.Channel.Send(new CanvasCommand(CanvasCommandKind.Report, _context.Display.Id) { Message = message });

    private void OnInputMethodFailed(Exception error) => _factory.Report(new OverlayEvent.Failure("Type with the input method", error));

    private void Disconnect()
    {
        if (_session is not { } session)
        {
            return;
        }
        _session = null;
        session.Failed -= OnInputMethodFailed;
        if (session.IsLocked)
        {
            // The session ended inside an input method's edit; let go of it once that edit returned.
            _factory.Queue.TryEnqueue(session.Dispose);
            return;
        }
        session.Dispose();
    }

    /// <summary>A canvas rectangle in screen pixels, for input methods placing their windows.</summary>
    private PixelRect Screen(Rect rect)
    {
        Point first = rect.Origin, last = new(rect.MaxX, rect.MaxY);
        if (_view is { } view)
        {
            first = view.OutputPoint(first);
            last = view.OutputPoint(last);
        }
        Point topLeft = _context.Display.ToDesktop(first);
        Point bottomRight = _context.Display.ToDesktop(last);
        return new PixelRect((int)Math.Floor(topLeft.X), (int)Math.Floor(topLeft.Y), (int)Math.Ceiling(bottomRight.X), (int)Math.Ceiling(bottomRight.Y));
    }

    /// <summary>
    /// The caret in overlay pixels: as wide as Windows' text cursor setting and at least one point, so it is as thick as
    /// the Mac's insertion point at every scale; upright carets sit on whole pixels.
    /// </summary>
    private CaretShape Shape(TextCaretPlacement caret)
    {
        double scale = _context.Display.Scale;
        float width = Math.Max(CaretWidth(), (float)Math.Max(1, Math.Round(scale)));
        if (_view is { } view)
        {
            // Through zoom the caret moves and grows with the text; its width stays the system's.
            Point top = view.OutputPoint(caret.Top);
            caret = caret with { Top = top, Height = caret.Height * view.Output.Height / view.Source.Height };
        }
        Vector2 offset, size;
        if (caret.Rotation == 0)
        {
            float left = MathF.Round((float)(caret.Top.X * scale) - width / 2);
            float top = MathF.Round((float)(caret.Top.Y * scale));
            float bottom = MathF.Round((float)((caret.Top.Y + caret.Height) * scale));
            offset = new Vector2(left, top);
            size = new Vector2(width, Math.Max(1, bottom - top));
        }
        else
        {
            offset = new Vector2((float)(caret.Top.X * scale) - width / 2, (float)(caret.Top.Y * scale));
            size = new Vector2(width, (float)(caret.Height * scale));
        }
        RGBA color = caret.Color;
        return new CaretShape(offset, size, (float)caret.Rotation, Windows.UI.Color.FromArgb(Byte(color.Alpha), Byte(color.Red),
                                                                                              Byte(color.Green), Byte(color.Blue)));
    }

    /// <summary>Blinks the caret at Windows' cursor blink rate, from fully shown, and stops blinking after Windows' timeout.</summary>
    private void Blink()
    {
        _caret.StopAnimation(nameof(Visual.Opacity));
        _caret.Opacity = 1;
        uint blink = PInvoke.GetCaretBlinkTime();
        if (blink == 0 || blink == Infinite)
        {
            return;
        }
        if (_blink is null)
        {
            Compositor compositor = _context.Compositor;
            // A step holds each value until the end of its half: shown, then hidden, then shown again.
            _hold = compositor.CreateStepEasingFunction();
            _blink = compositor.CreateScalarKeyFrameAnimation();
            _blink.InsertKeyFrame(0f, 1f);
            _blink.InsertKeyFrame(0.5f, 0f, _hold);
            _blink.InsertKeyFrame(1f, 1f, _hold);
        }
        _blink.Duration = TimeSpan.FromMilliseconds(2.0 * blink);
        uint timeout = CaretTimeout();
        if (timeout == Infinite)
        {
            _blink.IterationBehavior = AnimationIterationBehavior.Forever;
        }
        else
        {
            _blink.IterationBehavior = AnimationIterationBehavior.Count;
            _blink.IterationCount = Math.Max(1, (int)Math.Ceiling(timeout / (2.0 * blink)));
        }
        _caret.StartAnimation(nameof(Visual.Opacity), _blink);
    }

    private static unsafe float CaretWidth()
    {
        uint width = 1;
        return PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCARETWIDTH, 0, &width, 0) ? Math.Clamp(width, 1, 20) : 1;
    }

    /// <summary>How long the caret blinks after it last moved (Windows stops it to save power), in milliseconds.</summary>
    private static unsafe uint CaretTimeout()
    {
        uint timeout = DefaultCaretTimeout;
        return PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCARETTIMEOUT, 0, &timeout, 0) ? timeout : DefaultCaretTimeout;
    }

    private static RGBA? Color(uint? colorReference) => colorReference is { } value
        ? new RGBA((value & 0xFF) / 255.0, ((value >> 8) & 0xFF) / 255.0, ((value >> 16) & 0xFF) / 255.0)
        : null;

    private static byte Byte(double component) => (byte)Math.Clamp(Math.Round(component * 255), 0, 255);

    private readonly record struct CaretShape(Vector2 Offset, Vector2 Size, float Rotation, Windows.UI.Color Color);
}
