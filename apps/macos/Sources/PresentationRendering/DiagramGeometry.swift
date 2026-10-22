import CoreGraphics
import AnnotationCore

enum DiagramGeometry {
    static func path(for element: Element) -> CGPath {
        let b = element.frame
        let path = CGMutablePath()
        switch element.tool {
        case .database:
            let cap = b.height * 0.24
            path.move(to: CGPoint(x: b.x, y: b.y + cap / 2))
            path.addLine(to: CGPoint(x: b.x, y: b.maxY - cap / 2))
            path.addCurve(to: CGPoint(x: b.maxX, y: b.maxY - cap / 2),
                          control1: CGPoint(x: b.x, y: b.maxY + cap / 3),
                          control2: CGPoint(x: b.maxX, y: b.maxY + cap / 3))
            path.addLine(to: CGPoint(x: b.maxX, y: b.y + cap / 2))
            path.addCurve(to: CGPoint(x: b.x, y: b.y + cap / 2),
                          control1: CGPoint(x: b.maxX, y: b.y - cap / 3),
                          control2: CGPoint(x: b.x, y: b.y - cap / 3))
            path.closeSubpath()
            path.addEllipse(in: CGRect(x: b.x, y: b.y, width: b.width, height: cap))
        case .queue:
            let gap = b.height * 0.06, rowHeight = (b.height - gap * 2) / 3
            for row in 0..<3 {
                let frame = CGRect(x: b.x, y: b.y + Double(row) * (rowHeight + gap), width: b.width, height: rowHeight)
                let radius = min(6, rowHeight / 5)
                path.addRoundedRect(in: frame, cornerWidth: radius, cornerHeight: radius)
                path.move(to: CGPoint(x: b.x + b.width * 0.2, y: frame.minY))
                path.addLine(to: CGPoint(x: b.x + b.width * 0.2, y: frame.maxY))
            }
        case .person:
            path.addEllipse(in: CGRect(x: b.x + b.width * 0.31, y: b.y, width: b.width * 0.38, height: b.height * 0.34))
            path.move(to: CGPoint(x: b.x, y: b.maxY))
            path.addLine(to: CGPoint(x: b.x, y: b.y + b.height * 0.82))
            path.addCurve(to: CGPoint(x: b.maxX, y: b.y + b.height * 0.82),
                          control1: CGPoint(x: b.x, y: b.y + b.height * 0.32),
                          control2: CGPoint(x: b.maxX, y: b.y + b.height * 0.32))
            path.addLine(to: CGPoint(x: b.maxX, y: b.maxY))
            path.closeSubpath()
        case .grid:
            path.addRect(b.cgRect)
            let columns = element.style.gridColumns ?? 4, rows = element.style.gridRows ?? 4
            for column in 1..<columns {
                let x = b.x + b.width * Double(column) / Double(columns)
                path.move(to: CGPoint(x: x, y: b.y)); path.addLine(to: CGPoint(x: x, y: b.maxY))
            }
            for row in 1..<rows {
                let y = b.y + b.height * Double(row) / Double(rows)
                path.move(to: CGPoint(x: b.x, y: y)); path.addLine(to: CGPoint(x: b.maxX, y: y))
            }
        case .axes:
            let center = element.style.negativeAxes == true ? b.center : Point(b.x + b.width * 0.12, b.y + b.height * 0.88)
            path.move(to: CGPoint(x: b.x, y: center.y)); path.addLine(to: CGPoint(x: b.maxX, y: center.y))
            path.move(to: CGPoint(x: center.x, y: b.maxY)); path.addLine(to: CGPoint(x: center.x, y: b.y))
            let tip = min(10, min(b.width, b.height) * 0.12)
            path.move(to: CGPoint(x: b.maxX - tip, y: center.y - tip / 2))
            path.addLine(to: CGPoint(x: b.maxX, y: center.y))
            path.addLine(to: CGPoint(x: b.maxX - tip, y: center.y + tip / 2))
            path.move(to: CGPoint(x: center.x - tip / 2, y: b.y + tip))
            path.addLine(to: CGPoint(x: center.x, y: b.y))
            path.addLine(to: CGPoint(x: center.x + tip / 2, y: b.y + tip))
            let ticks = AxisTickGeometry.positions(in: b, style: element.style)
            for x in ticks.horizontal {
                path.move(to: CGPoint(x: x, y: center.y - 3)); path.addLine(to: CGPoint(x: x, y: center.y + 3))
            }
            for y in ticks.vertical {
                path.move(to: CGPoint(x: center.x - 3, y: y)); path.addLine(to: CGPoint(x: center.x + 3, y: y))
            }
        default: break
        }
        return path
    }
}
