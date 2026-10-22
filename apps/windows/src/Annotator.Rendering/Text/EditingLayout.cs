using Annotator.Core;
using Annotator.Core.Geometry;
using Annotator.Rendering.Interop;
using Annotator.Workspace.Canvas;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.DirectWrite;

namespace Annotator.Rendering.Text;

/// <summary>
/// The text editor's questions about laid-out text (spec 013 S-7), answered from the layout the renderer draws:
/// <see cref="TextSystem.CreateLayout"/> with the frame's size, moved from the frame's top-left corner until its first
/// baseline is <see cref="TextLayoutRequest.Baseline"/> below the frame's top, as the renderer moves it onto
/// <see cref="Annotator.Core.Scene.TextRun.Baseline"/>. So the caret, the selection and pointer hits sit exactly on the
/// drawn glyphs. One layout for one request; use it on one thread.
/// </summary>
internal sealed unsafe class EditingLayout : IDisposable
{
    private const int ENotSufficientBuffer = unchecked((int)0x8007007A);

    private readonly TextLayoutRequest request;
    private readonly int[] starts;
    private readonly int[] contentEnds;
    private readonly double[] tops;
    private readonly double[] bottoms;
    private readonly double shift;
    private IDWriteTextLayout* layout;

    public EditingLayout(TextSystem system, in TextLayoutRequest request)
    {
        this.request = request;
        Size frame = request.Frame;
        layout = system.CreateLayout(request.Text, request.Font, request.Alignment, (float)Math.Max(0, frame.Width), (float)Math.Max(0, frame.Height));
        try
        {
            uint count;
            var result = layout->GetLineMetrics(null, 0, &count);
            if (result.Value != ENotSufficientBuffer)
            {
                Native.Check(result, "IDWriteTextLayout::GetLineMetrics");
            }
            var lines = new DWRITE_LINE_METRICS[Math.Max(1, count)];
            fixed (DWRITE_LINE_METRICS* pointer = lines)
            {
                Native.Check(layout->GetLineMetrics(pointer, (uint)lines.Length, &count), "IDWriteTextLayout::GetLineMetrics");
            }
            starts = new int[count];
            contentEnds = new int[count];
            tops = new double[count];
            bottoms = new double[count];
            // The renderer's rule (DisplayListRenderer.Text): a layout whose first line has a baseline moves onto the run's.
            shift = count > 0 && lines[0].baseline > 0 ? request.Baseline - lines[0].baseline : 0;
            int start = 0;
            double top = shift;
            for (int line = 0; line < count; line++)
            {
                starts[line] = start;
                contentEnds[line] = start + (int)(lines[line].length - lines[line].newlineLength);
                tops[line] = top;
                top += lines[line].height;
                bottoms[line] = top;
                start += (int)lines[line].length;
            }
        }
        catch
        {
            Native.Release(ref layout);
            throw;
        }
    }

    public TextLayoutRequest Request => request;

    public TextCaret Caret(int index)
    {
        string text = request.Text;
        index = Math.Clamp(index, 0, text.Length);
        int line = LineOf(index);
        double x;
        if (contentEnds[line] == starts[line])
        {
            x = EmptyLineX();
        }
        else if (index == text.Length)
        {
            x = HitTestPosition((uint)(index - 1), trailing: true);
        }
        else
        {
            x = HitTestPosition((uint)index, trailing: false);
        }
        return new TextCaret(x, tops[line], bottoms[line]);
    }

    public void Range(int start, int end, List<Rect> rectangles)
    {
        string text = request.Text;
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        if (start == end)
        {
            return;
        }
        uint count;
        var result = layout->HitTestTextRange((uint)start, (uint)(end - start), 0, 0, null, 0, &count);
        if (result.Value != ENotSufficientBuffer)
        {
            Native.Check(result, "IDWriteTextLayout::HitTestTextRange");
        }
        if (count == 0)
        {
            return;
        }
        Span<DWRITE_HIT_TEST_METRICS> metrics = count <= 16 ? stackalloc DWRITE_HIT_TEST_METRICS[(int)count] : new DWRITE_HIT_TEST_METRICS[count];
        fixed (DWRITE_HIT_TEST_METRICS* pointer = metrics)
        {
            Native.Check(layout->HitTestTextRange((uint)start, (uint)(end - start), 0, 0, pointer, count, &count), "IDWriteTextLayout::HitTestTextRange");
        }
        foreach (ref readonly DWRITE_HIT_TEST_METRICS hit in metrics[..(int)count])
        {
            if (hit.width > 0)
            {
                rectangles.Add(new Rect(hit.left, hit.top + shift, hit.width, hit.height));
            }
        }
        // A selected line break has no width of its own; show a quarter of an em at the end of its line, as text fields do.
        for (int line = 0; line < starts.Length - 1; line++)
        {
            int lineBreak = contentEnds[line];
            if (lineBreak >= start && lineBreak < end && lineBreak < starts[line + 1])
            {
                rectangles.Add(new Rect(Caret(lineBreak).X, tops[line], request.Font.Size / 4, bottoms[line] - tops[line]));
            }
        }
    }

    public int HitTest(Point point)
    {
        int line = LineAt(point.Y);
        if (contentEnds[line] == starts[line])
        {
            return starts[line];
        }
        BOOL trailing, inside;
        DWRITE_HIT_TEST_METRICS metrics;
        // The line is chosen above; its middle, in the layout's own coordinates, keeps DirectWrite on that line.
        float middle = (float)((tops[line] + bottoms[line]) / 2 - shift);
        Native.Check(layout->HitTestPoint((float)point.X, middle, &trailing, &inside, &metrics), "IDWriteTextLayout::HitTestPoint");
        int index = (int)metrics.textPosition + (trailing ? (int)metrics.length : 0);
        return Math.Clamp(index, starts[line], contentEnds[line]);
    }

    public TextLine Line(int index)
    {
        int line = LineOf(Math.Clamp(index, 0, request.Text.Length));
        return new TextLine(starts[line], contentEnds[line], tops[line], bottoms[line]);
    }

    public void Dispose() => Native.Release(ref layout);

    /// <summary>The line whose caret positions include <paramref name="index"/>: its start through the end of its text.</summary>
    private int LineOf(int index)
    {
        for (int line = 0; line < starts.Length; line++)
        {
            if (index <= contentEnds[line])
            {
                return line;
            }
        }
        return starts.Length - 1;
    }

    private int LineAt(double y)
    {
        for (int line = 0; line < starts.Length; line++)
        {
            if (y < bottoms[line])
            {
                return line;
            }
        }
        return starts.Length - 1;
    }

    private double HitTestPosition(uint position, bool trailing)
    {
        float x, y;
        DWRITE_HIT_TEST_METRICS metrics;
        Native.Check(layout->HitTestTextPosition(position, trailing, &x, &y, &metrics), "IDWriteTextLayout::HitTestTextPosition");
        return x;
    }

    /// <summary>Where a caret on an empty line goes: the side or middle the text is aligned to (absolute alignment).</summary>
    private double EmptyLineX() => request.Alignment switch
    {
        TextAlignment.Center => request.Frame.Width / 2,
        TextAlignment.Right => request.Frame.Width,
        _ => 0,
    };
}
