import Testing
import AnnotationCore

@Test func sharingPresetsStayInsideAnOffsetUltrawideDisplay() throws {
    let display = Rect(x: -5120, y: 120, width: 5120, height: 1440)
    for preset in ShareRegionPreset.allCases {
        let region = try SharingRegion(preset: preset, displayBounds: display)
        #expect(display.contains(region.bounds))
        #expect(region.bounds.width >= 32 && region.bounds.height >= 32)
    }
    #expect(try SharingRegion(preset: .leftHalf, displayBounds: display).bounds ==
        Rect(x: -5120, y: 120, width: 2560, height: 1440))
    #expect(try SharingRegion(preset: .bottomRight, displayBounds: display).bounds ==
        Rect(x: -2560, y: 840, width: 2560, height: 720))
    let thirds = try [ShareRegionPreset.leftThird, .centerThird, .rightThird].map {
        try SharingRegion(preset: $0, displayBounds: display).bounds
    }
    #expect(abs(thirds[0].maxX - thirds[1].x) < 0.000001)
    #expect(abs(thirds[1].maxX - thirds[2].x) < 0.000001)
    #expect(abs(thirds[2].maxX - display.maxX) < 0.000001)
}

@Test func customSharingRegionsRejectInvalidOrOutsideCoordinates() throws {
    let display = Rect(x: 0, y: 0, width: 800, height: 600)
    for bounds in [
        Rect(x: -1, y: 0, width: 100, height: 100),
        Rect(x: 750, y: 0, width: 100, height: 100),
        Rect(x: 0, y: 580, width: 100, height: 100),
        Rect(x: 0, y: 0, width: 31, height: 100),
        Rect(x: 0, y: 0, width: 100, height: 31),
        Rect(x: .nan, y: 0, width: 100, height: 100),
        Rect(x: 0, y: 0, width: .infinity, height: 100)
    ] {
        #expect(throws: CanvasError.invalidGeometry) { try SharingRegion(bounds: bounds, displayBounds: display) }
    }
    #expect(try SharingRegion(bounds: Rect(x: 768, y: 568, width: 32, height: 32), displayBounds: display).bounds.maxX == 800)
}

@Test func sharingRegionLetterboxesAndUsesTheScreenZoomSource() throws {
    let display = Rect(x: 0, y: 0, width: 1000, height: 600)
    let region = try SharingRegion(preset: .leftHalf, displayBounds: display)
    let fit = try region.aspectFit(in: Rect(x: 10, y: 20, width: 800, height: 600))
    #expect(fit == Rect(x: 160, y: 20, width: 500, height: 600))
    let viewport = try Viewport(output: display, source: Rect(x: 200, y: 100, width: 500, height: 300))
    #expect(region.sourceBounds(through: viewport) == Rect(x: 200, y: 100, width: 250, height: 300))
    #expect(region.sourceBounds(through: nil) == region.bounds)
    #expect(throws: CanvasError.invalidGeometry) { try region.aspectFit(in: .zero) }
}
