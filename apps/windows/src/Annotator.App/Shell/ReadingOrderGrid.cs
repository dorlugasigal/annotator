using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using WinRect = Windows.Foundation.Rect;

namespace Annotator.App.Shell;

/// <summary>
/// A grid whose content UI Automation reads in the order SwiftUI gives an accessibility container's elements: by rows from
/// top to bottom, and from left to right within a row (two elements share a row when they overlap by more than half the
/// shorter one's height). Side-by-side columns read row by row across them, as the Mac's do; groups keep their own order
/// inside. The grid itself is not an element, so its sorted children belong to its parent.
/// </summary>
internal partial class ReadingOrderGrid : Grid
{
    /// <summary>SwiftUI's reading order of two frames.</summary>
    public static int Compare(WinRect a, WinRect b)
    {
        double overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (overlap > Math.Min(a.Height, b.Height) / 2)
        {
            int byX = a.X.CompareTo(b.X);
            return byX != 0 ? byX : a.Y.CompareTo(b.Y);
        }
        int byY = a.Y.CompareTo(b.Y);
        return byY != 0 ? byY : a.X.CompareTo(b.X);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed partial class Peer(ReadingOrderGrid owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;

        protected override IList<AutomationPeer> GetChildrenCore()
        {
            IList<AutomationPeer>? children = base.GetChildrenCore();
            var sorted = new AutomationPeerList();
            if (children is null)
            {
                return sorted;
            }
            var frames = new List<WinRect>(children.Count);
            foreach (AutomationPeer child in children)
            {
                WinRect frame = child.GetBoundingRectangle();
                // A stable insertion sort: equal frames keep the layout's order.
                int index = sorted.Count;
                while (index > 0 && Compare(frame, frames[index - 1]) < 0)
                {
                    index--;
                }
                sorted.Insert(index, child);
                frames.Insert(index, frame);
            }
            return sorted;
        }
    }
}
