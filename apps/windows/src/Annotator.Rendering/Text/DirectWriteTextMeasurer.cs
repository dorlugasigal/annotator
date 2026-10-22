using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Workspace.Canvas;

namespace Annotator.Rendering.Text;

/// <summary>
/// The Windows <see cref="ITextMeasurer"/>: DirectWrite with the D-4 font rule (see <see cref="TextSystem"/>).
/// Results are deterministic for equal inputs and cached; safe to call from any thread. It also answers the text
/// editor's caret, selection and hit-test questions (<see cref="ITextEditingLayout"/>) from the layout the renderer draws.
/// </summary>
public sealed class DirectWriteTextMeasurer : ITextMeasurer, ITextEditingLayout, IDisposable
{
    private const int MeasureCacheLimit = 4_096;

    private readonly Lock gate = new();
    private readonly TextSystem text = TextSystem.Create();
    private readonly Dictionary<(string Text, TextFont Font), Size> measured = [];
    private readonly Dictionary<TextFont, FontLineMetrics> metrics = [];
    private EditingLayout? editing;
    private bool disposed;

    /// <summary>The DirectWrite family chosen for <paramref name="design"/> at <paramref name="size"/> points.</summary>
    public string FamilyName(FontDesign design, double size)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return text.Family(design, size);
        }
    }

    public Size Measure(string text, TextFont font)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (measured.TryGetValue((text, font), out var known))
            {
                return known;
            }
            var size = this.text.Measure(text, font);
            if (measured.Count >= MeasureCacheLimit)
            {
                measured.Clear();
            }
            measured[(text, font)] = size;
            return size;
        }
    }

    public FontLineMetrics LineMetrics(TextFont font)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!metrics.TryGetValue(font, out var known))
            {
                known = text.LineMetrics(font);
                metrics[font] = known;
            }
            return known;
        }
    }

    public TextCaret Caret(in TextLayoutRequest request, int index)
    {
        lock (gate)
        {
            return Editing(request).Caret(index);
        }
    }

    public void Range(in TextLayoutRequest request, int start, int end, List<Rect> rectangles)
    {
        ArgumentNullException.ThrowIfNull(rectangles);
        lock (gate)
        {
            Editing(request).Range(start, end, rectangles);
        }
    }

    public int HitTest(in TextLayoutRequest request, Point point)
    {
        lock (gate)
        {
            return Editing(request).HitTest(point);
        }
    }

    public TextLine Line(in TextLayoutRequest request, int index)
    {
        lock (gate)
        {
            return Editing(request).Line(index);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            editing?.Dispose();
            editing = null;
            text.Dispose();
            measured.Clear();
            metrics.Clear();
        }
    }

    /// <summary>The editor asks several questions about one text in a row; the last layout is kept for them.</summary>
    private EditingLayout Editing(in TextLayoutRequest request)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(request.Text);
        if (editing is { } known && known.Request == request)
        {
            return known;
        }
        editing?.Dispose();
        editing = null;
        editing = new EditingLayout(text, request);
        return editing;
    }
}
