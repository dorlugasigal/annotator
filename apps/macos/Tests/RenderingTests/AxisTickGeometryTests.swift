import CoreGraphics
import Testing
import AnnotationCore
@testable import PresentationRendering

@Test func explicitAxisTicksAreEvenlySpacedFromTheOrigin() {
    let frame = Rect(x: 10, y: 20, width: 100, height: 200)
    var style = Style()
    style.axisTickCount = 3
    let positive = AxisTickGeometry.positions(in: frame, style: style)
    #expect(positive.horizontal == [44, 66, 88])
    #expect(positive.vertical == [64, 108, 152])
    style.negativeAxes = true
    let centered = AxisTickGeometry.positions(in: frame, style: style)
    #expect(centered.horizontal == [22.5, 35, 47.5, 72.5, 85, 97.5])
    #expect(centered.vertical == [45, 70, 95, 145, 170, 195])
    #expect(!centered.horizontal.contains(frame.center.x))
    #expect(!centered.vertical.contains(frame.center.y))
}

@Test func axisTicksCanBeHiddenAndLegacySpacingIsPreserved() {
    let frame = Rect(x: 10, y: 20, width: 100, height: 200)
    var style = Style()
    let legacy = AxisTickGeometry.positions(in: frame, style: style)
    #expect(legacy.horizontal == [20, 30, 40, 50, 60, 70, 80, 90, 100])
    #expect(legacy.vertical == [40, 60, 80, 100, 120, 140, 160, 180, 200])
    style.axisTickCount = 0
    let hidden = AxisTickGeometry.positions(in: frame, style: style)
    #expect(hidden.horizontal.isEmpty)
    #expect(hidden.vertical.isEmpty)
    style.axisTickCount = 24
    style.negativeAxes = true
    let maximum = AxisTickGeometry.positions(in: .zero, style: style)
    #expect(maximum.horizontal.count == 48)
    #expect(maximum.vertical.count == 48)
    #expect(maximum.horizontal.allSatisfy { $0.isFinite })
    #expect(maximum.vertical.allSatisfy { $0.isFinite })
}

@Test func renderedAxisPathUsesTheConfiguredTicks() {
    var element = Element(tool: .axes, frame: Rect(x: 10, y: 20, width: 100, height: 200))
    element.style.axisTickCount = 0
    let bare = pathElementCount(DiagramGeometry.path(for: element))
    element.style.axisTickCount = 3
    #expect(pathElementCount(DiagramGeometry.path(for: element)) == bare + 12)
    element.style.negativeAxes = true
    #expect(pathElementCount(DiagramGeometry.path(for: element)) == bare + 24)
}

private func pathElementCount(_ path: CGPath) -> Int {
    var count = 0
    path.applyWithBlock { _ in count += 1 }
    return count
}
