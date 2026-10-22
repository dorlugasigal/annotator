import AppKit

struct CornerRoutes {
    struct Item: Equatable {
        let id: String
        let displayID: UInt32
        let visibleFrame: CGRect
        let center: CGPoint
    }
    struct Destination {
        let corner: Int
        let point: CGPoint
        let bendSign: CGFloat
    }

    private var signature: [Item] = []
    private(set) var destinations: [String: Destination] = [:]

    mutating func update(_ items: [Item]) {
        let sorted = items.sorted { $0.id < $1.id }
        guard sorted != signature else { return }
        signature = sorted
        destinations.removeAll(keepingCapacity: true)
        for item in sorted {
            var nearest = 0
            var distance = CGFloat.infinity
            for corner in 0..<4 {
                let target = Self.point(corner: corner, in: item.visibleFrame)
                let candidate = hypot(target.x - item.center.x, target.y - item.center.y)
                if candidate < distance {
                    nearest = corner
                    distance = candidate
                }
            }
            let parity = item.id.utf8.reduce(UInt8(0), ^)
            destinations[item.id] = Destination(corner: nearest, point: Self.point(corner: nearest, in: item.visibleFrame),
                                                bendSign: parity.isMultiple(of: 2) ? 1 : -1)
        }
    }

    static func point(corner: Int, in frame: CGRect) -> CGPoint {
        let inset = min(24, min(frame.width, frame.height) / 4)
        return CGPoint(x: corner.isMultiple(of: 2) ? frame.minX + inset : frame.maxX - inset,
                       y: corner < 2 ? frame.maxY - inset : frame.minY + inset)
    }
}
