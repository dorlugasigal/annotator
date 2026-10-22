import Testing
@testable import AnnotationCore

@Test func roundedDiamondDoesNotSelectCutAwayCorner() {
    var diamond = Element(tool: .diamond, frame: Rect(x: 30, y: 30, width: 120, height: 120))
    #expect(HitTesting.contains(Point(90, 32), element: diamond, tolerance: 0))
    diamond.style.cornerRadius = 24
    #expect(!HitTesting.contains(Point(90, 32), element: diamond, tolerance: 0))
    #expect(HitTesting.contains(Point(90, 90), element: diamond, tolerance: 0))
    for corner in RoundedDiamond.corners(frame: diamond.frame, radius: 24) {
        #expect(HitTesting.contains(corner.entry, element: diamond, tolerance: 0))
        #expect(HitTesting.contains(corner.exit, element: diamond, tolerance: 0))
    }
}
