import AnnotationCore

enum AxisTickGeometry {
    static func positions(in frame: Rect, style: Style) -> (horizontal: [Double], vertical: [Double]) {
        guard let count = style.axisTickCount else {
            return ((1..<10).map { frame.x + frame.width * Double($0) / 10 },
                    (1..<10).map { frame.y + frame.height * Double($0) / 10 })
        }
        precondition((0...24).contains(count), "Axis tick count must be validated before rendering.")
        guard count > 0 else { return ([], []) }
        let centered = style.negativeAxes == true
        let origin = centered ? frame.center : Point(frame.x + frame.width * 0.12, frame.y + frame.height * 0.88)
        let fractions = (1...count).map { Double($0) / Double(count + 1) }
        var x = fractions.map { origin.x + (frame.maxX - origin.x) * $0 }
        var y = fractions.map { origin.y - (origin.y - frame.y) * $0 }
        if centered {
            x += fractions.map { origin.x - (origin.x - frame.x) * $0 }
            y += fractions.map { origin.y + (frame.maxY - origin.y) * $0 }
        }
        return (x.sorted(), y.sorted())
    }
}
