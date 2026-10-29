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

@Test func sharingOutputShowsHalfAnUltrawideDisplayBesideTheRegion() throws {
    let display = Rect(x: 0, y: 0, width: 5120, height: 1440)
    let region = try SharingRegion(preset: .leftHalf, displayBounds: display)
    let available = Rect(x: 0, y: 25, width: 5120, height: 1415)
    let content = try region.outputContentFrame(available: available, frame: FrameInsets(top: 28))
    #expect(content.height == 1415 - 2 * 24 - 28)
    #expect(abs(content.width / content.height - 2560.0 / 1440) < 0.000001)
    #expect(content.maxX == 5120 - 24)
    #expect(content.y == 25 + 24 + 28)
    #expect(content.x > region.bounds.maxX, "The window stays out of the shared half.")
}

@Test func sharingOutputKeepsTheRegionsOwnSizeWhenItFits() throws {
    let display = Rect(x: 0, y: 0, width: 1920, height: 1080)
    let region = try SharingRegion(bounds: Rect(x: 100, y: 100, width: 800, height: 600), displayBounds: display)
    let content = try region.outputContentFrame(available: Rect(x: 0, y: 25, width: 1920, height: 1055),
                                                frame: FrameInsets(top: 28))
    #expect(content == Rect(x: 1096, y: 77, width: 800, height: 600))
}

@Test func sharingOutputTakesTheCornerThatCoversTheLeastOfTheRegion() throws {
    let laptop = Rect(x: 0, y: 0, width: 1512, height: 982)
    let right = try SharingRegion(preset: .rightHalf, displayBounds: laptop)
    let besideRight = try right.outputContentFrame(available: Rect(x: 0, y: 38, width: 1512, height: 944),
                                                   frame: FrameInsets(top: 28))
    #expect(besideRight.x == 24 && besideRight.y == 38 + 24 + 28)
    #expect(besideRight.height == 944 - 2 * 24 - 28)
    #expect(besideRight.maxX < right.bounds.x)

    let wide = Rect(x: 0, y: 0, width: 1920, height: 1080)
    let top = try SharingRegion(preset: .topHalf, displayBounds: wide)
    let belowTop = try top.outputContentFrame(available: Rect(x: 0, y: 25, width: 1920, height: 1055),
                                              frame: FrameInsets(top: 28))
    #expect(belowTop == Rect(x: 24, y: 529.5, width: 1872, height: 526.5))

    let whole = try SharingRegion(bounds: wide, displayBounds: wide)
    let tie = try whole.outputContentFrame(available: Rect(x: 0, y: 25, width: 1920, height: 1055),
                                           frame: FrameInsets(top: 28))
    #expect(tie.maxX == 1920 - 24 && tie.y == 25 + 24 + 28, "Every corner covers the region, so the window opens top right.")
}

@Test func sharingOutputHasAMinimumSizeAndChecksItsInputs() throws {
    let display = Rect(x: -1440, y: 0, width: 1440, height: 900)
    let small = try SharingRegion(bounds: Rect(x: -1440, y: 0, width: 40, height: 40), displayBounds: display)
    let content = try small.outputContentFrame(available: display, frame: FrameInsets(top: 28, left: 1, bottom: 1, right: 1))
    #expect(content == Rect(x: -24 - 1 - 320, y: 24 + 28, width: 320, height: 200))
    #expect(throws: CanvasError.invalidGeometry) { try small.outputContentFrame(available: .zero, frame: FrameInsets(top: 28)) }
    #expect(throws: CanvasError.invalidGeometry) {
        try small.outputContentFrame(available: display, frame: FrameInsets(top: -1))
    }
    #expect(throws: CanvasError.invalidGeometry) {
        try small.outputContentFrame(available: display, frame: FrameInsets(top: .nan))
    }
}
