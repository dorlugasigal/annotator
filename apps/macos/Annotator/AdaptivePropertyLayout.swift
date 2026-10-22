import SwiftUI

struct AdaptivePropertyLayout: Layout {
    var spacing: CGFloat = 14

    static func frames(for sizes: [CGSize], width: CGFloat, spacing: CGFloat) -> [CGRect] {
        var frames: [CGRect] = []
        var x: CGFloat = 0
        var y: CGFloat = 0
        var rowHeight: CGFloat = 0
        for size in sizes {
            let itemWidth = min(width, size.width)
            if x > 0, x + itemWidth > width {
                x = 0
                y += rowHeight + spacing
                rowHeight = 0
            }
            frames.append(CGRect(x: x, y: y, width: itemWidth, height: size.height))
            x += itemWidth + spacing
            rowHeight = max(rowHeight, size.height)
        }
        return frames
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let ideal = subviews.map { $0.sizeThatFits(.unspecified) }
        let width = max(1, proposal.width ?? (ideal.reduce(0) { $0 + $1.width } + CGFloat(max(0, ideal.count - 1)) * spacing))
        let sizes = zip(subviews, ideal).map { view, size in
            view.sizeThatFits(ProposedViewSize(width: min(width, size.width), height: nil))
        }
        let frames = Self.frames(for: sizes, width: width, spacing: spacing)
        return CGSize(width: width, height: frames.map(\.maxY).max() ?? 0)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let sizes = subviews.map { view in
            let ideal = view.sizeThatFits(.unspecified)
            return view.sizeThatFits(ProposedViewSize(width: min(bounds.width, ideal.width), height: nil))
        }
        for (view, frame) in zip(subviews, Self.frames(for: sizes, width: bounds.width, spacing: spacing)) {
            view.place(
                at: CGPoint(x: bounds.minX + frame.minX, y: bounds.minY + frame.minY),
                anchor: .topLeading, proposal: ProposedViewSize(frame.size))
        }
    }
}
