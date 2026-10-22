import AnnotationCore
import Foundation

extension Fixture {
    static let prefix = "00000000-0000-4000-8000-"
}

/// Gives UUIDs that the Mac created at random stable stand-ins, `FFFFFFFF-0000-4000-8000-00000000000N`,
/// numbered in order of first appearance. Elements are visited in document order and, in each element,
/// `id`, `groupID`, `startAnchor`, `endAnchor`, `geometryToken`, then `appendEpoch`. Fixture UUIDs and
/// UUIDs that appear in an input file keep their value.
@MainActor
final class FreshIDs {
    static let prefix = "FFFFFFFF-0000-4000-8000-"
    private var names: [UUID: UUID] = [:]
    private var kept: Set<UUID> = []

    init(keepingUUIDsIn text: String = "") { keep(text) }

    func keep(_ text: String) {
        let pattern = /[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}/
        for match in text.matches(of: pattern) {
            if let id = UUID(uuidString: String(match.output)) { kept.insert(id) }
        }
    }

    func callAsFunction(_ id: UUID) -> UUID {
        if kept.contains(id) || id.uuidString.hasPrefix(Fixture.prefix) { return id }
        if let name = names[id] { return name }
        let name = UUID(uuidString: Self.prefix + String(format: "%012d", names.count + 1))!
        names[id] = name
        return name
    }

    /// A UUID that must already have a stand-in, so set iteration order can never number it.
    func known(_ id: UUID) -> UUID {
        if kept.contains(id) || id.uuidString.hasPrefix(Fixture.prefix) { return id }
        guard let name = names[id] else { preconditionFailure("A random UUID appeared outside an element walk.") }
        return name
    }

    func element(_ original: Element) -> Element {
        var element = original
        element.id = self(element.id)
        element.groupID = element.groupID.map { self($0) }
        element.startAnchor = element.startAnchor.map { Anchor(elementID: self($0.elementID), relative: $0.relative) }
        element.endAnchor = element.endAnchor.map { Anchor(elementID: self($0.elementID), relative: $0.relative) }
        element.geometryToken = self(element.geometryToken)
        element.appendEpoch = self(element.appendEpoch)
        return element
    }

    func elements(_ list: [Element]) -> [Element] { list.map { element($0) } }

    func set(_ ids: Set<UUID>) -> J {
        .array(ids.map { known($0).uuidString }.sorted().map { .string($0) })
    }
}

/// The observable state of a document after a step.
@MainActor
func documentRecord(_ document: CanvasDocument, _ ids: FreshIDs) -> [String: J] {
    let elements = ids.elements(document.elements)
    return [
        "elements": .value(elements),
        "selection": ids.set(document.selection),
        "generation": .uint(document.generation),
        "canUndo": .bool(document.canUndo),
        "canRedo": .bool(document.canRedo),
        "isEditing": .bool(document.isEditing),
        "hasPendingChanges": .bool(document.hasPendingChanges),
        "hasProtection": .bool(document.hasProtection),
        "historyBytes": .int(document.historyBytes),
        "presentation": ["mode": .string(document.presentation.mode.rawValue), "zoom": .double(document.presentation.zoom)],
        "undoOperationID": document.undoOperationID.map { .string(ids($0).uuidString) } ?? .null,
        "redoOperationID": document.redoOperationID.map { .string(ids($0).uuidString) } ?? .null
    ]
}

/// The bytes the app writes for a board, without the checks `DrawingFile` makes, so files can hold
/// any schema number or stale bindings.
struct RawBoard: Encodable {
    let elements: [Element]
    let schemaVersion: Int

    static func bytes(_ elements: [Element], schema: Int) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return try encoder.encode(RawBoard(elements: elements, schemaVersion: schema))
    }
}

/// Elements shared by several sections.
enum Samples {
    static func wave(from start: Point, count: Int, step: Double, amplitude: Double) -> [Point] {
        (0..<count).map { Point(start.x + Double($0) * step, start.y + amplitude * sin(Double($0) * 0.45)) }
    }

    /// Common tools that need board format 1, with an arrow bound to two shapes.
    static func basic() -> [Element] {
        let blue = RGBA(0.17, 0.45, 0.88)
        return [
            Fixture.element(1, .rectangle, frame: Rect(x: 40, y: 40, width: 160, height: 100), style: Fixture.style(.rectangle) {
                $0.color = blue; $0.cornerRadius = 8; $0.fill = RGBA(0.17, 0.45, 0.88, 0.4); $0.fillPattern = .hatch
            }),
            Fixture.element(2, .ellipse, frame: Rect(x: 320, y: 60, width: 140, height: 90), style: Fixture.style(.ellipse) {
                $0.opacity = 0.8; $0.fill = RGBA(0.96, 0.89, 0.22, 0.4)
            }),
            Fixture.element(3, .arrow, points: [Point(200, 90), Point(320, 105)]) {
                $0.startAnchor = Anchor(elementID: Fixture.id(1), relative: Point(1, 0.5))
                $0.endAnchor = Anchor(elementID: Fixture.id(2), relative: Point(0, 0.5))
            },
            Fixture.element(4, .pen, points: wave(from: Point(40, 200), count: 24, step: 8, amplitude: 14),
                            style: Fixture.style(.pen) { $0.pressure = true; $0.width = 5 }) {
                $0.pressures = (0..<24).map { 0.25 + 0.75 * abs(sin(Double($0) * 0.3)) }
            },
            Fixture.element(5, .highlighter, points: [Point(260, 200), Point(330, 204), Point(400, 198)]),
            Fixture.element(6, .line, points: [Point(40, 260), Point(240, 300)],
                            style: Fixture.style(.line) { $0.pattern = .dashed; $0.color = .ink }),
            Fixture.element(7, .text, frame: Rect(x: 280, y: 240, width: 180, height: 40), text: "Hello, board",
                            style: Fixture.style(.text) { $0.color = .ink; $0.fontSize = 24 }),
            Fixture.element(8, .counter, frame: Rect(x: 480, y: 40, width: 36, height: 36), text: "1"),
            Fixture.element(9, .ruler, points: [Point(40, 340), Point(160, 340)], text: "120 px"),
            Fixture.element(10, .diamond, frame: Rect(x: 480, y: 120, width: 100, height: 80),
                            style: Fixture.style(.diamond) { $0.cornerRadius = 10 })
        ]
    }
}
