namespace Annotator.Core.Scene;

/// <summary>
/// The x positions of hatch lines, Swift <c>SceneRenderer.hatchPositions</c>: <c>stride(from: x - height, through:
/// maxX + height, by: spacing)</c> with <c>spacing = max(6, width * 3, (frame width + frame height * 2) / 4096)</c>.
/// Swift's floating-point stride computes <c>start + i * spacing</c> with a fused multiply-add and includes the end
/// when it lands on it (corpus <c>platform/hatch.jsonl</c>).
/// </summary>
internal struct HatchStride
{
    private long index;
    private bool finished;

    public HatchStride(Rect frame, double width)
    {
        Spacing = SwiftMath.Max(SwiftMath.Max(6, width * 3), (frame.Width + frame.Height * 2) / 4_096);
        Start = frame.X - frame.Height;
        End = frame.MaxX + frame.Height;
    }

    public double Spacing { get; }

    public double Start { get; }

    public double End { get; }

    public double Current { get; private set; }

    public bool MoveNext()
    {
        if (finished)
        {
            return false;
        }
        var x = index == 0 ? Start : Math.FusedMultiplyAdd(index, Spacing, Start);
        index++;
        if (!double.IsFinite(x) || (x >= End && x != End))
        {
            finished = true;
            return false;
        }
        finished = x >= End;
        Current = x;
        return true;
    }
}
