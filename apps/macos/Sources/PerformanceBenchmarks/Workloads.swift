import AnnotationCore
import AppKit
import PresentationRendering

enum Scenario: String, CaseIterable {
    case add1000 = "document.add.1000"
    case add5000 = "document.add.5000"
    case select1000 = "document.select-hit-miss.1000"
    case select5000 = "document.select-hit-miss.5000"
    case move1000 = "document.move-100.history-0"
    case moveHistory = "document.move-100.history-80"
    case undoHistory = "document.undo.history-80"
    case append = "document.append.200000"
    case serialize = "document.roundtrip.1000-200000"
    case standardCold = "renderer.standard.cold"
    case standardCached = "renderer.standard.cached"
    case gradientsCold = "renderer.gradients-200.cold"
    case gradientsCached = "renderer.gradients-200.cached"
    case stressCold = "renderer.stress.cold"
    case stressCached = "renderer.stress.cached"
    case ellipseSmallCold = "renderer.rough-256.cold"
    case ellipseSmallCached = "renderer.rough-256.cached"
    case ellipseLargeCold = "renderer.rough-1000.cold"
    case ellipseLargeCached = "renderer.rough-1000.cached"
    case ellipseWideCold = "renderer.rough-wide-32.cold"
    case ellipseWideCached = "renderer.rough-wide-32.cached"
    case imageCold = "renderer.images-64.cold"
    case imageCached = "renderer.images-64.cached"
    case textCold = "renderer.text-1000.cold"
    case textCached = "renderer.text-1000.cached"
    case pressureCold = "renderer.pressure-200000.cold"
    case pressureCached = "renderer.pressure-200000.cached"
    case pressureClippedCached = "renderer.pressure-200000.clipped.cached"
    case decode = "image.decode.1920x1080"
    case encode = "image.png-encode.1920x1080"
    case exportPlain = "export.source-only.png"
    case exportAnnotated = "export.annotated-images.png"
    case exportEffects = "export.synthetic-effects.png"
    case cleanup = "memory.owned-cleanup-idle"
}

@MainActor
enum Workloads {
    static func run(_ scenario: Scenario, samples: Int, warmup: Int) throws -> Result {
        switch scenario {
        case .add1000, .add5000, .select1000, .select5000, .move1000, .moveHistory, .undoHistory:
            return try document(scenario, samples: samples, warmup: warmup)
        case .append:
            let points = Fixtures.stroke(200_000).points
            return try measure(scenario.rawValue, workload: ["appendedPoints": points.count, "transactions": 1],
                               scope: "Begin, add empty stroke, append every point, commit; input fixture excluded.",
                               samples: samples, warmup: warmup) {
                var document = CanvasDocument.empty()
                let stroke = Element(id: Fixtures.id(900_000), tool: .pen)
                return Job(run: {
                    document.begin()
                    try document.add(stroke)
                    for point in points { try document.appendSample(to: stroke.id, point: point) }
                    try document.commit()
                }, verify: {
                    try require(document.elements.count == 1 && document.elements[0].points == points,
                                "Append lost or changed samples.")
                    try require(document.canUndo && document.generation == 1, "Gesture must be one commit.")
                    return ["retainedPoints": UInt64(document.elements[0].points.count),
                            "historyBytes": UInt64(document.historyBytes)]
                })
            }
        case .serialize:
            let elements = Fixtures.elements(999) + [Fixtures.stroke(200_000)]
            let original = try CanvasDocument(elements: elements)
            return try measure(scenario.rawValue, workload: ["elements": 1_000, "longStrokePoints": 200_000],
                               scope: "DrawingFile JSON encode, decode and document validation; no disk IO.",
                               samples: samples, warmup: warmup) {
                var data = Data()
                var restored = CanvasDocument.empty()
                return Job(run: {
                    data = try DrawingFile(document: original).data()
                    restored = try DrawingFile.read(data)
                }, verify: {
                    try require(restored.elements == original.elements, "Drawing roundtrip changed elements.")
                    return ["jsonBytes": UInt64(data.count), "elements": UInt64(restored.elements.count)]
                })
            }
        case .standardCold, .standardCached, .gradientsCold, .gradientsCached, .stressCold, .stressCached,
             .ellipseSmallCold, .ellipseSmallCached, .ellipseLargeCold, .ellipseLargeCached,
             .ellipseWideCold, .ellipseWideCached,
             .imageCold, .imageCached, .textCold, .textCached, .pressureCold, .pressureCached, .pressureClippedCached:
            return try renderer(scenario, samples: samples, warmup: warmup)
        case .decode, .encode, .exportPlain, .exportAnnotated, .exportEffects:
            return try image(scenario, samples: samples, warmup: warmup)
        case .cleanup:
            return try cleanup(samples: samples, warmup: warmup)
        }
    }

    private static func document(_ scenario: Scenario, samples: Int, warmup: Int) throws -> Result {
        let count = scenario == .add5000 || scenario == .select5000 ? 5_000 : 1_000
        let historyCount = scenario == .moveHistory || scenario == .undoHistory ? 80 : 0
        let elements = Fixtures.elements(count)
        let initial = try CanvasDocument(elements: elements)
        let selected = Set(elements.prefix(100).map(\.id))
        let adding = scenario == .add1000 || scenario == .add5000
        let selecting = scenario == .select1000 || scenario == .select5000
        let hitPoint = elements.last!.frame.center
        let expectedHit = ElementStacking.frontmost(at: hitPoint, in: elements)
        try require(expectedHit != nil, "Selection fixture must hit.")
        return try measure(scenario.rawValue,
                           workload: ["initialElements": count, "preparedHistoryEntries": historyCount,
                                      "selectedElements": adding || selecting ? 0 : 100,
                                      "operationsPerSample": selecting ? 100 : 1],
                           scope: selecting ? "50 frontmost hit + 50 off-canvas miss selections, alternating."
                               : "One production mutation including commit/bindings/history; prepared history excluded.",
                           samples: samples, warmup: warmup) {
            var document = initial
            document.select(selected)
            for _ in 0..<historyCount { try document.editSelection { $0.translate(by: Point(1, 0)) } }
            let snapshotBytes = 64 + elements.reduce(0) { $0 + $1.estimatedBytes }
            try require(document.historyBytes == historyCount * snapshotBytes, "Prepared history fixture was trimmed.")
            let previousX = document.elements[0].frame.x
            let extra = Element(id: Fixtures.id(700_000), tool: .rectangle,
                                frame: Rect(x: 10, y: 10, width: 30, height: 30))
            var hits = 0
            return Job(run: {
                if adding { try document.add(extra) }
                else if selecting {
                    for index in 0..<100 {
                        document.select(at: index.isMultiple(of: 2) ? hitPoint : Point(-1_000, -1_000))
                        if !document.selection.isEmpty { hits += 1 }
                    }
                } else if scenario == .undoHistory { document.undo() }
                else { try document.editSelection { $0.translate(by: Point(1, 0)) } }
            }, verify: {
                let counters: [String: UInt64] = [
                    "elements": UInt64(document.elements.count), "historyBytes": UInt64(document.historyBytes),
                    "hits": UInt64(hits), "generation": document.generation
                ]
                if adding {
                    try require(document.elements.count == count + 1 && document.elements.last?.id == extra.id,
                                "Add failed.")
                    document.undo()
                    try require(document.elements == elements, "Added element did not undo.")
                } else if selecting {
                    try require(hits == 50 && document.selection.isEmpty, "Hit/miss selection mismatch.")
                } else {
                    let expectedX = previousX + (scenario == .undoHistory ? -1 : 1)
                    try require(document.elements[0].frame.x == expectedX, "Move/undo geometry mismatch.")
                    try require(document.elements[100].frame == elements[100].frame, "Unselected element moved.")
                }
                return counters
            })
        }
    }

    private static func render(_ renderer: SceneRenderer, elements: [Element], context: CGContext, clip: CGRect? = nil) {
        context.clear(CGRect(x: 0, y: 0, width: context.width, height: context.height))
        context.saveGState()
        if let clip { context.clip(to: clip) }
        renderer.draw(elements, in: context)
        context.restoreGState()
        context.flush()
    }

    private static func referenceChecksum(_ elements: [Element], clip: CGRect? = nil) throws -> UInt64 {
        try autoreleasepool {
            let context = try Fixtures.context()
            render(SceneRenderer(), elements: elements, context: context, clip: clip)
            return try Fixtures.checksum(context)
        }
    }

    private static func renderer(_ scenario: Scenario, samples: Int, warmup: Int) throws -> Result {
        let cached = scenario.rawValue.hasSuffix(".cached")
        let elements: [Element]
        switch scenario {
        case .standardCold, .standardCached: elements = Fixtures.elements(200) + [Fixtures.stroke(20_000)]
        case .gradientsCold, .gradientsCached:
            elements = (Fixtures.elements(200) + [Fixtures.stroke(20_000)]).map { original in
                var element = original
                element.style.setGradient(GradientPreset.spectrum.gradient, fill: false)
                if element.tool.supportsFill { element.style.setGradient(GradientPreset.sunset.gradient, fill: true) }
                return element
            }
        case .stressCold, .stressCached: elements = Fixtures.elements(999) + [Fixtures.stroke(200_000)]
        case .ellipseSmallCold, .ellipseSmallCached: elements = Fixtures.elements(256, ellipsesOnly: true)
        case .ellipseLargeCold, .ellipseLargeCached: elements = Fixtures.elements(1_000, ellipsesOnly: true)
        case .ellipseWideCold, .ellipseWideCached:
            elements = Fixtures.elements(32, ellipsesOnly: true).map { original in
                var element = original
                element.frame = Rect(x: 160, y: 90, width: 1_600, height: 900)
                return element
            }
        case .imageCold, .imageCached:
            elements = try Fixtures.images(64, data: Fixtures.png(Fixtures.source(width: 512, height: 512)))
        case .textCold, .textCached:
            elements = Fixtures.elements(1_000).map { original in
                var element = original
                element.tool = .text
                element.points = []
                element.text = "Note \(element.seed % 1_000)"
                return element
            }
        case .pressureCold, .pressureCached, .pressureClippedCached:
            var stroke = Fixtures.stroke(200_000)
            stroke.style.pressure = true
            stroke.style.roughness = 6
            stroke.pressures = stroke.points.indices.map { 0.2 + Double($0 % 101) * 0.008 }
            elements = [stroke]
        default: throw BenchmarkFailure.invalid("Not a renderer scenario.")
        }
        try elements.forEach { try $0.validate() }
        let clip = scenario == .pressureClippedCached ? CGRect(x: 912, y: 492, width: 96, height: 96) : nil
        let expectedChecksum = try referenceChecksum(elements, clip: clip)
        let pathElements = elements.filter { $0.tool.isShape || $0.tool.isConnector || $0.tool.isFreehand }.count
        return try measure(scenario.rawValue,
                           workload: ["elements": elements.count, "pathElements": pathElements,
                                      "storedPoints": elements.reduce(0) { $0 + $1.points.count },
                                      "roughEllipses": elements.filter { $0.tool == .ellipse && $0.style.roughness > 0 }.count,
                                      "imageElements": elements.filter { $0.tool == .image }.count,
                                      "maximumElementWidth": Int(ceil(elements.map(\.frame.width).max() ?? 0)),
                                      "maximumElementHeight": Int(ceil(elements.map(\.frame.height).max() ?? 0)),
                                      "bitmapWidth": Fixtures.width, "bitmapHeight": Fixtures.height,
                                      "clipWidth": Int(clip?.width ?? CGFloat(Fixtures.width)),
                                      "clipHeight": Int(clip?.height ?? CGFloat(Fixtures.height))],
                           scope: cached ? "Clear + draw + flush CPU bitmap; renderer primed once outside each sample."
                               : "Clear + draw + flush CPU bitmap with empty renderer cache; context allocated outside timing.",
                           samples: samples, warmup: warmup) {
            let context = try Fixtures.context()
            let renderer = SceneRenderer()
            if cached { render(renderer, elements: elements, context: context, clip: clip) }
            let builds = renderer.fullPathBuilds
            let appends = renderer.incrementalAppends
            return Job(run: { render(renderer, elements: elements, context: context, clip: clip) }, verify: {
                let checksum = try Fixtures.checksum(context)
                try require(checksum == expectedChecksum, "Renderer pixels changed across cache/sample states.")
                let delta = renderer.fullPathBuilds - builds
                if !cached && scenario != .pressureCold {
                    try require(delta == pathElements, "Cold path build count mismatch.")
                }
                if cached && pathElements <= 512 { try require(delta == 0, "Fitting scene unexpectedly rebuilt paths.") }
                return ["fullPathBuilds": UInt64(delta), "incrementalAppends": UInt64(renderer.incrementalAppends - appends),
                        "pixelChecksumFNV1a": checksum]
            })
        }
    }

    private static func image(_ scenario: Scenario, samples: Int, warmup: Int) throws -> Result {
        let source = try Fixtures.source()
        let png = try Fixtures.png(source)
        var elements: [Element] = []
        if scenario == .exportAnnotated || scenario == .exportEffects {
            elements = Fixtures.elements(200) + [Fixtures.stroke(20_000)]
                + Fixtures.images(8, data: try Fixtures.png(Fixtures.source(width: 512, height: 512)))
        }
        if scenario == .exportEffects {
            for (index, tool) in [Tool.blur, .pixelate, .distort, .cover].enumerated() {
                elements.append(Element(id: Fixtures.id(600_000 + index), tool: tool,
                                        frame: Rect(x: Double(index * 400), y: 200, width: 300, height: 300)))
            }
        }
        let exportElements = elements
        return try measure(scenario.rawValue,
                           workload: ["width": source.width, "height": source.height, "sourcePNGBytes": png.count,
                                      "annotationElements": elements.count, "imageElements": elements.isEmpty ? 0 : 8],
                           scope: scenario == .decode ? "RasterAsset.decode plus full decoded-byte read (forces pixel materialization)."
                               : scenario == .encode ? "CGImage PNG encoding to memory."
                               : "SnapshotExporter.image plus PNG encoding to memory; synthetic source, no disk IO.",
                           samples: samples, warmup: warmup) {
            var result = Data()
            var decoded: CGImage?
            var pixelBytes = 0
            var pixelChecksum: UInt64 = 0
            return Job(run: {
                if scenario == .decode {
                    let image = try RasterAsset.decode(png)
                    decoded = image
                    guard let bytes = image.dataProvider?.data else { throw BenchmarkFailure.invalid("Decoded bytes missing.") }
                    pixelBytes = CFDataGetLength(bytes)
                    guard let pointer = CFDataGetBytePtr(bytes) else { throw BenchmarkFailure.invalid("Decoded pointer missing.") }
                    for index in 0..<pixelBytes { pixelChecksum &+= UInt64(pointer[index]) }
                } else if scenario == .encode { result = try Fixtures.png(source) }
                else { result = try Fixtures.png(SnapshotExporter.image(source: source, elements: exportElements)) }
            }, verify: {
                let image: CGImage
                if let decoded { image = decoded }
                else { image = try RasterAsset.decode(result) }
                try require(image.width == Fixtures.width && image.height == Fixtures.height, "Image size mismatch.")
                if scenario == .decode {
                    try require(pixelBytes >= Fixtures.width * Fixtures.height * 4 && pixelChecksum > 0,
                                "Decode did not materialize pixels.")
                } else { try require(!result.isEmpty, "PNG output is empty.") }
                return ["outputPNGBytes": UInt64(result.count), "materializedPixelBytes": UInt64(pixelBytes),
                        "pixelByteSum": pixelChecksum]
            })
        }
    }

    private static func cleanup(samples: Int, warmup: Int) throws -> Result {
        var stages: [MemoryStage] = []
        var measurements: [Sample] = []
        for cycle in 0..<(samples + warmup) {
            let before = try MemoryReading.read()
            stages.append(MemoryStage(cycle: cycle, stage: "before-owned-allocation", memory: before))
            let start = DispatchTime.now().uptimeNanoseconds
            try autoreleasepool {
                let png = try Fixtures.png(Fixtures.source(width: 512, height: 512))
                var document = try CanvasDocument(elements: Fixtures.images(64, data: png))
                document.select(Set(document.elements.map(\.id)))
                for _ in 0..<20 { try document.editSelection { $0.translate(by: Point(1, 0)) } }
                let renderer = SceneRenderer()
                let context = try Fixtures.context()
                render(renderer, elements: document.elements, context: context)
                stages.append(MemoryStage(cycle: cycle, stage: "owned-live", memory: try MemoryReading.read()))
                renderer.reset()
                document = .empty()
                try require(document.elements.isEmpty && !document.canUndo, "Owned document cleanup failed.")
            }
            let end = DispatchTime.now().uptimeNanoseconds
            let drained = try MemoryReading.read()
            stages.append(MemoryStage(cycle: cycle, stage: "after-reset-ARC-autorelease-drain", memory: drained))
            let idleStart = DispatchTime.now().uptimeNanoseconds
            Thread.sleep(forTimeInterval: 0.25)
            let idleEnd = DispatchTime.now().uptimeNanoseconds
            let idle = try MemoryReading.read()
            stages.append(MemoryStage(cycle: cycle, stage: "after-harness-idle", memory: idle,
                                      idleWallMS: Double(idleEnd - idleStart) / 1_000_000,
                                      idleCPUTimeMS: (idle.processCPUSeconds - drained.processCPUSeconds) * 1_000))
            if cycle >= warmup {
                measurements.append(Sample(durationMS: Double(end - start) / 1_000_000, before: before, after: drained,
                                           counters: ["imageElements": 64, "historyEdits": 20]))
            }
        }
        let sorted = measurements.map(\.durationMS).sorted()
        return Result(scenario: Scenario.cleanup.rawValue,
                      workload: ["imageElements": 64, "sourceImageWidth": 512, "sourceImageHeight": 512,
                                 "historyEdits": 20, "idleSleepMS": 250],
                      timingScope: "Owned fixture allocation, history, draw, reset and pool drainage; includes RSS probes, excludes idle sleep.",
                      warmupCount: warmup, samples: measurements,
                      p50MS: sorted[Int(ceil(Double(samples) * 0.5)) - 1],
                      p95MS: sorted[Int(ceil(Double(samples) * 0.95)) - 1], memoryStages: stages)
    }
}
