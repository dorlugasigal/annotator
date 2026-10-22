using System.Windows.Automation;

namespace Annotator.UXReference;

/// <summary>
/// The Mac reports frames for a Settings page's whole content, also the part that is scrolled out of view; UI Automation
/// gives content outside the window or its scroll viewer no frame, or a clipped one. This scrolls the window's scroll area
/// with the Scroll pattern, three quarters of a view at a time, and fills in each node's frame from a view where the node
/// shows whole, moved back by the distance scrolled, which the nodes seen in both views give. It scrolls back to the top.
/// </summary>
internal static class ScrolledFrames
{
    /// <summary>
    /// How far inside the scroll area's edges a frame must end to count as uncut, in DIPs: the area's reported frame and the
    /// clip of its content are rounded to pixels apart, so a cut frame can end up to a pixel inside the reported edge.
    /// </summary>
    private const double Edge = 1;

    /// <param name="note">What happened: how far it scrolled, how many frames it filled in and why it stopped.</param>
    public static List<AxNode> Complete(nint window, double scale, List<AxNode> nodes, out string note)
    {
        note = "no scroll area";
        List<(AxNode Node, int Depth)> flat = Flatten(nodes, 0);
        int area = flat.FindIndex(entry => entry.Node.Role == "AXScrollArea" && entry.Node.Frame is not null);
        if (area < 0)
        {
            return nodes;
        }
        AutomationElement root = AutomationElement.FromHandle(window);
        ScrollPattern? FindScroll() =>
            root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true)) is { } scroller &&
            scroller.TryGetCurrentPattern(ScrollPattern.Pattern, out object? pattern) && pattern is ScrollPattern found ? found : null;
        if (FindScroll() is not { } scroll || !scroll.Current.VerticallyScrollable)
        {
            note = "the scroll area does not scroll";
            return nodes;
        }
        double[] view = flat[area].Node.Frame!;
        // The window can be shorter than the scroll area reports (a Settings state taller than the screen): what it cuts off
        // is not shown either.
        double windowHeight = root.Current.BoundingRectangle.Height / scale;
        double top = view[1], bottom = Math.Min(view[1] + view[3], windowHeight);
        double[]?[] frames = [.. flat.Select(entry => entry.Node.Frame)];
        List<int> content = Content(flat);
        var pending = new HashSet<int>(content.Where(index => !Whole(frames[index], top, bottom)));
        // A node taller than the view never shows whole: its top comes from a view where the top shows, its bottom from one
        // where the bottom shows (in the content's coordinates of the first view).
        var tops = new Dictionary<int, double>();
        var bottoms = new Dictionary<int, double>();
        foreach (int index in pending)
        {
            Edges(index, frames[index], 0, atEnd: false);
        }
        int missing = pending.Count, pages = 0;
        string stopped = "it reached the end";
        int retries = 0;
        // A busy app can fail a UI Automation call now and then, and the scroll viewer's element can be replaced (in full runs,
        // after the first scroll): each call is tried again after a second, with the scroll viewer found again, up to three
        // times, before the frames found so far are kept.
        T Retry<T>(Func<T> call)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return call();
                }
                catch (Exception error) when (attempt < 3 && error is ElementNotAvailableException or System.Runtime.InteropServices.COMException or InvalidOperationException)
                {
                    retries++;
                    Thread.Sleep(1000);
                    if (error is ElementNotAvailableException && FindScroll() is { } again)
                    {
                        scroll = again;
                    }
                }
            }
        }
        double Percent() => Retry(() => scroll.Current.VerticalScrollPercent);
        try
        {
            double viewSize = Retry(() => scroll.Current.VerticalViewSize);
            double step = Math.Max(1, 75 * viewSize / Math.Max(1, 100 - viewSize));
            for (int page = 0; page < 40 && pending.Count > 0 && Percent() < 100; page++)
            {
                double target = Math.Min(100, Percent() + step);
                Retry(() =>
                {
                    scroll.SetScrollPercent(ScrollPattern.NoScroll, target);
                    return true;
                });
                pages++;
                // The scroll viewer may animate: wait until it stays put, then a little longer for UI Automation's clipping.
                double settled = double.NaN;
                for (int wait = 0; wait < 20 && settled != Percent(); wait++)
                {
                    settled = Percent();
                    Thread.Sleep(150);
                }
                Thread.Sleep(250);
                // At the end of the scroll range the last content can sit flush with the bottom edge and still be whole.
                bool atEnd = Percent() >= 99.99;
                List<(AxNode Node, int Depth)> again = Flatten(Retry(() => UiaDump.Read(window, scale)), 0);
                // The scroll bar shows its arrows while it scrolls, so the content alone is matched, node by node.
                List<int> moved = Content(again);
                if (moved.Count != content.Count || !moved.Select(index => again[index].Node.Key).SequenceEqual(content.Select(index => flat[index].Node.Key)))
                {
                    stopped = $"the content changed while it scrolled ({moved.Count} nodes, {content.Count} before)";
                    break;
                }
                // Nodes whole in both views give the distance scrolled; the median keeps one odd node from moving the rest.
                List<double> shifts = [.. Enumerable.Range(0, content.Count)
                    .Where(at => !pending.Contains(content[at]) && frames[content[at]] is not null && Whole(again[moved[at]].Node.Frame, top, bottom))
                    .Select(at => frames[content[at]]![1] - again[moved[at]].Node.Frame![1])
                    .Order()];
                if (shifts.Count == 0)
                {
                    stopped = "no node showed whole in two views";
                    break;
                }
                double shift = shifts[shifts.Count / 2];
                for (int at = 0; at < content.Count; at++)
                {
                    if (!pending.Contains(content[at]) || again[moved[at]].Node.Frame is not { } frame)
                    {
                        continue;
                    }
                    if (Whole(frame, top, bottom, atEnd))
                    {
                        frames[content[at]] = [frame[0], frame[1] + shift, frame[2], frame[3]];
                        pending.Remove(content[at]);
                    }
                    else if (Edges(content[at], frame, shift, atEnd) is { } joined)
                    {
                        frames[content[at]] = joined;
                        pending.Remove(content[at]);
                    }
                }
            }
            Retry(() =>
            {
                scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
                return true;
            });
        }
        catch (Exception error) when (error is ElementNotAvailableException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // The window went away or stopped answering: keep the frames found so far.
            stopped = $"UI Automation failed: {error.GetType().Name}: {error.Message}";
        }
        stopped = pending.Count == 0 ? "every frame was found" : stopped;
        note = $"scrolled {pages} times; filled in {missing - pending.Count} of {missing} frames out of view; stopped because {stopped}" +
            (retries > 0 ? $"; {retries} UI Automation calls were tried again" : string.Empty);
        int next = 0;
        return [.. nodes.Select(node => Rebuild(node, frames, ref next))];

        // Records the edges of a pending node that this view shows uncut; returns its whole frame once both are known.
        double[]? Edges(int index, double[]? frame, double shift, bool atEnd)
        {
            if (frame is null || frame[3] <= 0)
            {
                return null;
            }
            if (frame[1] > top + Edge)
            {
                tops[index] = frame[1] + shift;
            }
            if (frame[1] + frame[3] < bottom - Edge || (atEnd && frame[1] + frame[3] <= bottom + 1))
            {
                bottoms[index] = frame[1] + frame[3] + shift;
            }
            return tops.TryGetValue(index, out double upper) && bottoms.TryGetValue(index, out double lower) && lower > upper
                ? [frame[0], upper, frame[2], lower - upper]
                : null;
        }
    }

    /// <summary>The scroll area's content in a flattened dump: its descendants, without its own scroll bar.</summary>
    private static List<int> Content(List<(AxNode Node, int Depth)> flat)
    {
        var content = new List<int>();
        int area = flat.FindIndex(entry => entry.Node.Role == "AXScrollArea");
        if (area < 0)
        {
            return content;
        }
        for (int index = area + 1; index < flat.Count && flat[index].Depth > flat[area].Depth; index++)
        {
            if (flat[index].Node.Role == "AXScrollBar")
            {
                int depth = flat[index].Depth;
                while (index + 1 < flat.Count && flat[index + 1].Depth > depth)
                {
                    index++;
                }
                continue;
            }
            content.Add(index);
        }
        return content;
    }

    /// <summary>
    /// A frame that the scroll area does not clip: inside it and touching neither edge, or, at the end of the scroll range
    /// (<paramref name="atEnd"/>), ending at the bottom edge within a pixel's rounding.
    /// </summary>
    private static bool Whole(double[]? frame, double top, double bottom, bool atEnd = false) =>
        frame is not null && frame[3] > 0 && frame[1] > top + Edge &&
        (frame[1] + frame[3] < bottom - Edge || (atEnd && frame[1] + frame[3] <= bottom + 1));

    private static AxNode Rebuild(AxNode node, double[]?[] frames, ref int at)
    {
        var copy = new AxNode
        {
            Role = node.Role,
            Label = node.Label,
            Identifier = node.Identifier,
            Value = node.Value,
            Help = node.Help,
            Title = node.Title,
            Frame = frames[at++],
            Enabled = node.Enabled,
        };
        foreach (AxNode child in node.Children)
        {
            copy.Children.Add(Rebuild(child, frames, ref at));
        }
        return copy;
    }

    private static List<(AxNode Node, int Depth)> Flatten(IEnumerable<AxNode> nodes, int depth)
    {
        var list = new List<(AxNode, int)>();
        foreach (AxNode node in nodes)
        {
            list.Add((node, depth));
            list.AddRange(Flatten(node.Children, depth + 1));
        }
        return list;
    }
}
