using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Workspace.Canvas;

namespace Annotator.Workspace.Tests.Canvas;

/// <summary>
/// Lays text out like <see cref="FixedTextMeasurer"/>: every character is half the font size wide and a line is 1.2 font
/// sizes high, aligned in the frame as the renderer aligns it.
/// </summary>
internal sealed class FixedEditingLayout : ITextEditingLayout
{
    public TextCaret Caret(in TextLayoutRequest request, int index)
    {
        (int line, int start, int end) = LineOf(request.Text, index);
        double x = Offset(request, start, end) + (Math.Min(index, end) - start) * Width(request);
        return new TextCaret(x, line * Height(request), (line + 1) * Height(request));
    }

    public void Range(in TextLayoutRequest request, int start, int end, List<Rect> rectangles)
    {
        string text = request.Text;
        int lineStart = 0, line = 0;
        while (true)
        {
            int newline = text.IndexOf('\n', lineStart);
            int contentEnd = newline < 0 ? text.Length : newline;
            int lineLimit = newline < 0 ? text.Length : newline + 1;
            if (start < lineLimit && end > lineStart)
            {
                int from = Math.Max(start, lineStart), to = Math.Min(end, contentEnd);
                double width = Math.Max(0, to - from) * Width(request) + (newline >= 0 && end > newline ? request.Font.Size / 4 : 0);
                rectangles.Add(new Rect(Offset(request, lineStart, contentEnd) + (from - lineStart) * Width(request), line * Height(request),
                                        width, Height(request)));
            }
            if (newline < 0)
            {
                return;
            }
            lineStart = newline + 1;
            line++;
        }
    }

    public int HitTest(in TextLayoutRequest request, Point point)
    {
        string[] lines = request.Text.Split('\n');
        int line = Math.Clamp((int)Math.Floor(point.Y / Height(request)), 0, lines.Length - 1);
        int start = 0;
        for (int index = 0; index < line; index++)
        {
            start += lines[index].Length + 1;
        }
        int end = start + lines[line].Length;
        double offset = Offset(request, start, end);
        int column = (int)Math.Round((point.X - offset) / Width(request));
        return start + Math.Clamp(column, 0, end - start);
    }

    public TextLine Line(in TextLayoutRequest request, int index)
    {
        (int line, int start, int end) = LineOf(request.Text, index);
        return new TextLine(start, end, line * Height(request), (line + 1) * Height(request));
    }

    private static (int Line, int Start, int End) LineOf(string text, int index)
    {
        int line = 0, start = 0;
        while (true)
        {
            int end = text.IndexOf('\n', start);
            if (end < 0 || index <= end)
            {
                return (line, start, end < 0 ? text.Length : end);
            }
            start = end + 1;
            line++;
        }
    }

    private static double Width(in TextLayoutRequest request) => request.Font.Size * 0.5;

    private static double Height(in TextLayoutRequest request) => request.Font.Size * 1.2;

    private static double Offset(in TextLayoutRequest request, int start, int end)
    {
        double width = (end - start) * Width(request);
        return request.Alignment switch
        {
            TextAlignment.Center => (request.Frame.Width - width) / 2,
            TextAlignment.Right => request.Frame.Width - width,
            _ => 0,
        };
    }
}

/// <summary>A clipboard in memory; set <see cref="Failure"/> to make the next call fail.</summary>
internal sealed class MemoryTextClipboard : ITextClipboard
{
    public string? Text { get; set; }

    public string? Failure { get; set; }

    public string? GetText() => Failure is { } message ? throw new InvalidOperationException(message) : Text;

    public void SetText(string text)
    {
        if (Failure is { } message)
        {
            throw new InvalidOperationException(message);
        }
        Text = text;
    }
}

/// <summary>
/// The editor host as the app wires it, without Windows: a <see cref="CanvasTextEditor"/> per session, keys and
/// characters go to it first, and it stands in for the Text Services Framework by recording what it is told.
/// </summary>
internal sealed class EditorHost : ITextEditorHost
{
    public EditorHost(MemoryTextClipboard? clipboard = null) => Clipboard = clipboard ?? new MemoryTextClipboard();

    public MemoryTextClipboard Clipboard { get; }

    public CanvasTextEditor? Editor { get; private set; }

    public List<TextChange> ToldInputMethod { get; } = [];

    public int SelectionsTold { get; private set; }

    public int Finishes { get; private set; }

    public List<string> Failures { get; } = [];

    /// <summary>Stands in for an input method that holds a composition: it finishes it with this text when asked.</summary>
    public Action<CanvasTextEditor>? FinishComposition { get; set; }

    public void Begin(CanvasController canvas, CanvasTextSession session)
    {
        var editor = new CanvasTextEditor(canvas, session, new FixedEditingLayout(), Clipboard);
        editor.TextEdited += ToldInputMethod.Add;
        editor.SelectionMoved += () => SelectionsTold++;
        editor.FinishCompositionRequested += () =>
        {
            Finishes++;
            FinishComposition?.Invoke(editor);
        };
        editor.Failed += Failures.Add;
        Editor = editor;
    }

    public void Update(CanvasTextSession session) => Editor?.Update(session);

    public void End()
    {
        Editor?.End();
        Editor = null;
    }
}
