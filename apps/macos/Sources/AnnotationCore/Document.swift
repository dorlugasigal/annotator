import Foundation

public struct CanvasDocument: Sendable {
    // New documents may share a generation counter, but not a render identity.
    public let identity = UUID()
    private struct Snapshot: Sendable {
        let elements: [Element]
        let selection: Set<UUID>
        var presentation: PresentationSettings
        let bytes: Int
        var operationID: UUID?

        init(elements: [Element], selection: Set<UUID>, presentation: PresentationSettings) {
            self.elements = elements
            self.selection = selection
            self.presentation = presentation
            bytes = 64 + elements.reduce(0) { $0 + $1.estimatedBytes }
        }
    }

    public private(set) var elements: [Element]
    public private(set) var selection: Set<UUID> = []
    public private(set) var generation: UInt64 = 0
    public private(set) var presentation = PresentationSettings()
    private var past: [Snapshot] = []
    private var future: [Snapshot] = []
    private var pending: Snapshot?
    private var restoredStrokeIDs: Set<UUID> = []
    public let historyByteLimit: Int
    public let historyCountLimit: Int

    public init(elements: [Element] = [], historyByteLimit: Int = 64 * 1_024 * 1_024,
                historyCountLimit: Int = 100) throws {
        guard historyByteLimit > 0, historyCountLimit > 0 else {
            throw CanvasError.invalidDocument("History limits must be positive.")
        }
        guard Set(elements.map(\.id)).count == elements.count else {
            throw CanvasError.invalidDocument("Duplicate object identifiers.")
        }
        try elements.forEach { try $0.validate() }
        self.elements = elements
        self.historyByteLimit = historyByteLimit
        self.historyCountLimit = historyCountLimit
        resolveBindings()
    }

    public static func empty() -> CanvasDocument {
        CanvasDocument(validatedEmpty: ())
    }

    private init(validatedEmpty: Void) {
        elements = []
        historyByteLimit = 64 * 1_024 * 1_024
        historyCountLimit = 100
    }

    public var canUndo: Bool { !past.isEmpty }
    public var canRedo: Bool { !future.isEmpty }
    public var undoOperationID: UUID? { past.last?.operationID }
    public var redoOperationID: UUID? { future.last?.operationID }
    public var historyOperationIDs: Set<UUID> { Set((past + future).compactMap(\.operationID)) }
    public var isEditing: Bool { pending != nil }
    public var hasPendingChanges: Bool {
        guard let pending else { return false }
        return pending.elements != elements || pending.presentation != presentation
    }
    public var hasProtection: Bool { elements.contains { $0.tool.isProtection } }
    public var historyBytes: Int { past.reduce(0) { $0 + $1.bytes } + future.reduce(0) { $0 + $1.bytes } }
    private var snapshot: Snapshot { Snapshot(elements: elements, selection: selection, presentation: presentation) }

    public mutating func begin() {
        if pending == nil { pending = snapshot }
    }

    public mutating func commit(operationID: UUID? = nil) throws {
        guard var original = pending else { throw CanvasError.noTransaction }
        resolveBindings()
        pending = nil
        if original.elements != elements || original.presentation != presentation || operationID != nil {
            generation &+= 1
            original.operationID = operationID
            past.append(original)
            future.removeAll()
            trimHistory()
        }
    }

    public mutating func cancel() {
        guard let original = pending else { return }
        restore(original)
        pending = nil
    }

    public mutating func undo() {
        if pending != nil {
            cancel()
            return
        }
        guard let previous = past.popLast() else { return }
        var next = snapshot
        next.operationID = previous.operationID
        future.append(next)
        restore(previous)
        generation &+= 1
        trimHistory()
    }

    public mutating func redo() {
        cancel()
        guard let next = future.popLast() else { return }
        var previous = snapshot
        previous.operationID = next.operationID
        past.append(previous)
        restore(next)
        generation &+= 1
        trimHistory()
    }

    public mutating func select(_ ids: Set<UUID>, extending: Bool = false) {
        let valid = ids.intersection(Set(elements.map(\.id)))
        selection = extending ? selection.symmetricDifference(valid) : valid
    }

    public mutating func setPresentation(_ settings: PresentationSettings) throws {
        try settings.validate()
        try transaction { $0.presentation = settings }
    }

    public mutating func stopPresentationWithoutHistory() {
        presentation.mode = .off
        if pending != nil { pending?.presentation.mode = .off }
        if let last = past.last, last.elements == elements, last.presentation == presentation { past.removeLast() }
    }

    public mutating func select(at point: Point, extending: Bool = false) {
        guard let hit = ElementStacking.frontmost(at: point, in: elements) else {
            if !extending { selection.removeAll() }
            return
        }
        let ids = hit.groupID.map { group in Set(elements.filter { $0.groupID == group }.map(\.id)) } ?? [hit.id]
        select(ids, extending: extending)
    }

    public mutating func add(_ element: Element) throws {
        try element.validate()
        guard !elements.contains(where: { $0.id == element.id }) else {
            throw CanvasError.invalidDocument("Duplicate object identifier.")
        }
        try transaction { $0.elements.append(element) }
    }

    public mutating func update(_ id: UUID, _ mutation: (inout Element) -> Void) throws {
        guard let index = elements.firstIndex(where: { $0.id == id }) else { throw CanvasError.missingElement }
        guard !elements[index].locked else { return }
        var changed = elements[index]
        mutation(&changed)
        guard changed.id == id else { throw CanvasError.invalidDocument("Object identity cannot change.") }
        try changed.validate()
        changed.revision &+= 1
        changed.invalidateGeometry()
        try transaction {
            $0.elements[index] = changed
            if changed.tool.isShape { $0.resolveBindings() }
        }
    }

    public mutating func appendSample(to id: UUID, point: Point, pressure: Double = 1) throws {
        guard point.isFinite, pressure.isFinite, (0...1).contains(pressure) else {
            throw CanvasError.invalidGeometry
        }
        guard let index = elements.firstIndex(where: { $0.id == id }) else { throw CanvasError.missingElement }
        guard elements[index].tool.isFreehand else { throw CanvasError.invalidGeometry }
        guard !elements[index].locked else { return }
        try transaction { document in
            if document.restoredStrokeIDs.remove(id) != nil {
                document.elements[index].appendEpoch = UUID()
            }
            let hadPoints = !document.elements[index].points.isEmpty
            if hadPoints && document.elements[index].pressures.isEmpty {
                document.elements[index].pressures = Array(repeating: 1, count: document.elements[index].points.count)
            }
            document.elements[index].points.append(point)
            document.elements[index].pressures.append(pressure)
            document.elements[index].frame = hadPoints
                ? document.elements[index].frame.union(Rect(from: point, to: point))
                : Rect(from: point, to: point)
            document.elements[index].revision &+= 1
            document.elements[index].geometryToken = UUID()
        }
    }

    public mutating func editSelection(_ mutation: (inout Element) -> Void) throws {
        try transaction { document in
            for index in document.elements.indices
            where document.selection.contains(document.elements[index].id) && !document.elements[index].locked {
                var changed = document.elements[index]
                mutation(&changed)
                guard changed.id == document.elements[index].id else {
                    throw CanvasError.invalidDocument("Object identity cannot change.")
                }
                try changed.validate()
                changed.revision &+= 1
                changed.invalidateGeometry()
                document.elements[index] = changed
            }
            document.resolveBindings()
        }
    }

    public mutating func deleteSelection(includingProtection: Bool = true) throws {
        try transaction { document in
            document.elements.removeAll {
                document.selection.contains($0.id) && !$0.locked
                    && (includingProtection || !$0.tool.isProtection)
            }
            document.selection.formIntersection(Set(document.elements.map(\.id)))
            document.resolveBindings()
        }
    }

    public mutating func clearAll() throws {
        try transaction { document in
            document.elements.removeAll()
            document.selection.removeAll()
            document.presentation.mode = .off
        }
    }

    public mutating func clearAnnotations() throws {
        try transaction { document in
            document.elements.removeAll { !$0.tool.isProtection && !$0.locked }
            document.selection.formIntersection(Set(document.elements.map(\.id)))
            document.resolveBindings()
        }
    }

    public mutating func clearProtection() throws {
        try transaction { document in
            document.elements.removeAll { $0.tool.isProtection }
            document.selection.formIntersection(Set(document.elements.map(\.id)))
        }
    }

    public mutating func duplicateSelection() throws {
        try insertCopies(of: elements.filter { selection.contains($0.id) && !$0.locked }, offset: Point(18, 18))
    }

    /// Adds unlocked copies with fresh identities and selects them. Groups and connector
    /// attachments are kept only between the copied objects.
    public mutating func insertCopies(of originals: [Element], offset: Point) throws {
        guard !originals.isEmpty else { return }
        let remapping = Dictionary(originals.map { ($0.id, UUID()) }, uniquingKeysWith: { first, _ in first })
        try transaction { document in
            var groups: [UUID: UUID] = [:]
            let copies = originals.map { original in
                var copy = original
                copy.id = remapping[original.id] ?? UUID()
                copy.locked = false
                if let group = original.groupID {
                    if groups[group] == nil { groups[group] = UUID() }
                    copy.groupID = groups[group]
                }
                copy.translate(by: offset)
                copy.startAnchor = original.startAnchor.flatMap { anchor in
                    remapping[anchor.elementID].map { Anchor(elementID: $0, relative: anchor.relative) }
                }
                copy.endAnchor = original.endAnchor.flatMap { anchor in
                    remapping[anchor.elementID].map { Anchor(elementID: $0, relative: anchor.relative) }
                }
                return copy
            }
            try copies.forEach { try $0.validate() }
            document.elements.append(contentsOf: copies)
            document.selection = Set(copies.map(\.id))
            document.resolveBindings()
        }
    }

    public mutating func groupSelection() throws {
        let group = UUID()
        try editSelection { $0.groupID = group }
    }

    public mutating func ungroupSelection() throws { try editSelection { $0.groupID = nil } }

    public mutating func setSelectionLocked(_ locked: Bool) throws {
        try transaction { document in
            for index in document.elements.indices where document.selection.contains(document.elements[index].id) {
                document.elements[index].locked = locked
            }
        }
    }

    public mutating func arrangeSelection(toFront: Bool) throws {
        try transaction { document in
            let moved = document.elements.filter { document.selection.contains($0.id) && !$0.locked }
            let ids = Set(moved.map(\.id))
            let rest = document.elements.filter { !ids.contains($0.id) }
            document.elements = toFront ? rest + moved : moved + rest
        }
    }

    public func nearestAnchor(to point: Point, excluding: UUID, threshold: Double = 18) -> Anchor? {
        var nearest: (anchor: Anchor, distance: Double)?
        for element in elements where element.id != excluding && element.tool.isShape {
            for location in element.anchorLocations {
                let distance = element.anchorPoint(location).distance(to: point)
                if distance <= threshold, distance < (nearest?.distance ?? .infinity) {
                    nearest = (Anchor(elementID: element.id, relative: location), distance)
                }
            }
        }
        return nearest?.anchor
    }

    @discardableResult
    public mutating func applyRecognition(_ replacement: Element, expectedGeneration: UInt64,
                                          expectedGeometryToken: UUID) throws -> Bool {
        guard pending == nil, generation == expectedGeneration,
              let index = elements.firstIndex(where: { $0.id == replacement.id }),
              elements[index].geometryToken == expectedGeometryToken,
              elements[index].tool == .pen, !elements[index].locked,
              replacement.tool.isShape || replacement.tool == .arrow else { return false }
        try replacement.validate()
        elements[index] = replacement
        elements[index].invalidateGeometry()
        generation &+= 1
        return true
    }

    private mutating func transaction(_ mutation: (inout CanvasDocument) throws -> Void) throws {
        let ownsTransaction = pending == nil
        if ownsTransaction { begin() }
        do {
            try mutation(&self)
            if ownsTransaction { try commit() }
        } catch {
            cancel()
            throw error
        }
    }

    private mutating func resolveBindings() {
        let targets = Dictionary(uniqueKeysWithValues: elements.filter { $0.tool.isShape }.map { ($0.id, $0) })
        for index in elements.indices where elements[index].tool.isConnector {
            guard elements[index].points.count >= 2 else { continue }
            for isStart in [true, false] {
                guard let anchor = isStart ? elements[index].startAnchor : elements[index].endAnchor else { continue }
                guard let target = targets[anchor.elementID] else {
                    if isStart { elements[index].startAnchor = nil } else { elements[index].endAnchor = nil }
                    continue
                }
                let pointIndex = isStart ? 0 : elements[index].points.count - 1
                let resolved = target.anchorPoint(anchor.relative)
                if elements[index].points[pointIndex] != resolved {
                    elements[index].points[pointIndex] = resolved
                    elements[index].frame = Rect.enclosing(elements[index].points)
                    elements[index].revision &+= 1
                    elements[index].invalidateGeometry()
                }
            }
        }
    }

    private mutating func restore(_ value: Snapshot) {
        elements = value.elements
        // Fork the append lineage on the next sample, keeping restored snapshots exact.
        restoredStrokeIDs = Set(elements.filter { $0.tool.isFreehand }.map(\.id))
        selection = value.selection.intersection(Set(elements.map(\.id)))
        presentation = value.presentation
    }

    private mutating func trimHistory() {
        while past.count > historyCountLimit { past.removeFirst() }
        while future.count > historyCountLimit { future.removeFirst() }
        while historyBytes > historyByteLimit {
            if !past.isEmpty { past.removeFirst() }
            else if !future.isEmpty { future.removeFirst() }
            else { break }
        }
    }
}

public struct DrawingFile: Codable, Sendable {
    public static let currentSchema = 3
    public let schemaVersion: Int
    public var elements: [Element]
    public init(document: CanvasDocument) {
        schemaVersion = Self.requiresPaintSchema(document.elements) ? 3 :
            document.elements.contains { $0.label != nil } ? 2 : 1
        elements = document.elements
    }

    public func document() throws -> CanvasDocument {
        guard (1...Self.currentSchema).contains(schemaVersion) else { throw CanvasError.unsupportedSchema(schemaVersion) }
        try validatePaintSchema()
        guard schemaVersion >= 2 || !elements.contains(where: { $0.label != nil }) else {
            throw CanvasError.invalidDocument("Shape labels require board format 2.")
        }
        return try CanvasDocument(elements: elements)
    }

    public func data() throws -> Data {
        try validatePaintSchema()
        guard schemaVersion >= 2 || !elements.contains(where: { $0.label != nil }) else {
            throw CanvasError.invalidDocument("Shape labels require board format 2.")
        }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        let data = try encoder.encode(self)
        guard data.count <= 128 * 1_024 * 1_024 else {
            throw CanvasError.invalidDocument("The board exceeds the supported file budget. Export a PNG or split the board; no objects were discarded.")
        }
        return data
    }

    private static func requiresPaintSchema(_ elements: [Element]) -> Bool {
        elements.contains { $0.style.usesGradient || $0.label?.style.usesGradient == true || $0.label?.followsStroke == true }
    }

    private func validatePaintSchema() throws {
        guard schemaVersion >= 3 || !Self.requiresPaintSchema(elements) else {
            throw CanvasError.invalidDocument("Gradients and matching labels need board format 3.")
        }
    }

    public static func read(_ data: Data) throws -> CanvasDocument {
        guard data.count <= 128 * 1_024 * 1_024 else {
            throw CanvasError.invalidDocument("The file exceeds the safe import memory budget.")
        }
        return try JSONDecoder().decode(Self.self, from: data).document()
    }
}
