import Foundation
import AnnotationCore

enum ToolbarPlacement {
    static func constrainedDragFrame(_ frame: CGRect, visibleFrame: CGRect) -> CGRect {
        var result = frame
        result.origin.x = max(visibleFrame.minX, min(frame.minX, visibleFrame.maxX - frame.width))
        result.origin.y = min(visibleFrame.maxY - frame.height, max(visibleFrame.minY, frame.minY))
        return result
    }

    static func dockEdge(at point: CGPoint, visibleFrame: CGRect, threshold: CGFloat = 1) -> ToolbarDockEdge? {
        guard point.x.isFinite, point.y.isFinite, visibleFrame.width > 0, visibleFrame.height > 0,
              visibleFrame.insetBy(dx: -threshold, dy: -threshold).contains(point) else { return nil }
        let distances: [(ToolbarDockEdge, CGFloat)] = [
            (.top, abs(point.y - visibleFrame.maxY)), (.bottom, abs(point.y - visibleFrame.minY)),
            (.left, abs(point.x - visibleFrame.minX)), (.right, abs(point.x - visibleFrame.maxX))
        ]
        guard let nearest = distances.min(by: { $0.1 < $1.1 }), nearest.1 <= threshold else { return nil }
        return nearest.0
    }

    static func frame(size: CGSize, visibleFrame: CGRect, orientation: ToolbarOrientation,
                      edge: ToolbarDockEdge, inset: CGFloat = 12) -> CGRect {
        let safe = visibleFrame.insetBy(dx: inset, dy: inset)
        let size = CGSize(width: min(size.width, safe.width), height: min(size.height, safe.height))
        var frame = CGRect(x: safe.midX - size.width / 2, y: safe.midY - size.height / 2,
                           width: size.width, height: size.height)
        if orientation == .vertical {
            frame.origin.x = edge == .left ? safe.minX : safe.maxX - size.width
        } else {
            frame.origin.y = edge == .bottom ? safe.minY : safe.maxY - size.height
        }
        frame.origin.x = floor(frame.origin.x)
        frame.origin.y = floor(frame.origin.y)
        return frame
    }
}
