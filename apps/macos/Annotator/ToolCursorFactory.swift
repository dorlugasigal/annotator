import AppKit
import AnnotationCore

@MainActor
enum ToolCursorFactory {
    struct Key: Hashable {
        let tool: Tool
        let width: Double
        var color: RGBA? = nil
    }
    private static var cache: [Key: NSCursor] = [:]
    static let rotationCursor: NSCursor = {
        let image = NSImage(size: NSSize(width: 32, height: 32), flipped: false) { _ in
            let path = NSBezierPath()
            path.appendArc(withCenter: NSPoint(x: 16, y: 16), radius: 10, startAngle: 45, endAngle: 315, clockwise: false)
            path.move(to: NSPoint(x: 23, y: 9))
            path.line(to: NSPoint(x: 29, y: 10))
            path.line(to: NSPoint(x: 26, y: 4))
            path.lineCapStyle = .round; path.lineJoinStyle = .round
            NSColor.white.setStroke(); path.lineWidth = 4; path.stroke()
            NSColor.black.setStroke(); path.lineWidth = 2; path.stroke()
            return true
        }
        return NSCursor(image: image, hotSpot: NSPoint(x: 16, y: 16))
    }()
    // Keep legacy style arguments compatible without hiding the tool footprint.
    static func cursor(tool: Tool, width: Double, color: RGBA = LaserTrail.defaultColor,
                       style: ToolCursorStyle = .toolPreview) -> NSCursor {
        if tool == .select { return .arrow }
        if tool == .text { return .iBeam }
        if tool == .laser { return laserCursor(width: width, color: color) }
        if !tool.isFreehand && tool != .eraser { return .crosshair }
        let key = Key(tool: tool, width: width)
        if let cached = cache[key] { return cached }
        let footprint = min(500, tool == .highlighter ? width * 5 : tool == .eraser ? width * 2 : width)
        let side = min(520, max(24, ceil(footprint) + 10))
        let image = NSImage(size: NSSize(width: side, height: side), flipped: false) { rect in
            let height = max(4, footprint)
            let nibWidth = tool == .highlighter ? min(500, max(4, width * 1.6)) : height
            let box = CGRect(x: (rect.width - nibWidth) / 2, y: (rect.height - height) / 2,
                             width: nibWidth, height: height)
            let path = tool == .highlighter ? NSBezierPath(rect: box) : NSBezierPath(ovalIn: box)
            NSColor.white.setStroke(); path.lineWidth = 3; path.stroke()
            NSColor.black.setStroke(); path.lineWidth = 1; path.stroke()
            if tool != .eraser {
                NSColor.black.withAlphaComponent(0.12).setFill(); path.fill()
                NSColor.white.setStroke()
                let cross = NSBezierPath()
                cross.move(to: NSPoint(x: rect.midX - 3, y: rect.midY)); cross.line(to: NSPoint(x: rect.midX + 3, y: rect.midY))
                cross.move(to: NSPoint(x: rect.midX, y: rect.midY - 3)); cross.line(to: NSPoint(x: rect.midX, y: rect.midY + 3))
                cross.lineWidth = 2; cross.stroke()
                NSColor.black.setStroke(); cross.lineWidth = 0.7; cross.stroke()
            }
            return true
        }
        let cursor = NSCursor(image: image, hotSpot: NSPoint(x: side / 2, y: side / 2))
        if cache.count >= 32 { cache.removeAll() }
        cache[key] = cursor
        return cursor
    }

    /// The laser pointer: a dot as wide as the trail's head, in the laser's color, inside a white ring and a soft glow.
    static func laserCursor(width: Double, color: RGBA) -> NSCursor {
        let solid = RGBA(color.red, color.green, color.blue)
        let key = Key(tool: .laser, width: width, color: solid)
        if let cached = cache[key] { return cached }
        let radius = min(250, max(2, width / 2))
        let side = ceil(radius * 2 + 12)
        let image = NSImage(size: NSSize(width: side, height: side), flipped: false) { rect in
            let center = NSPoint(x: rect.midX, y: rect.midY)
            func circle(_ r: CGFloat) -> NSBezierPath {
                NSBezierPath(ovalIn: CGRect(x: center.x - r, y: center.y - r, width: r * 2, height: r * 2))
            }
            solid.nsColor.withAlphaComponent(0.28).setFill(); circle(radius + 5).fill()
            NSColor.white.setFill(); circle(radius + 1.5).fill()
            solid.nsColor.setFill(); circle(radius).fill()
            return true
        }
        let cursor = NSCursor(image: image, hotSpot: NSPoint(x: side / 2, y: side / 2))
        if cache.count >= 32 { cache.removeAll() }
        cache[key] = cursor
        return cursor
    }
}
