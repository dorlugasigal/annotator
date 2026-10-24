import Foundation

/// One key of the tutorial's keyboard: its id, its row, where in the row it starts and how wide it is, in key units.
public struct TutorialKey: Equatable, Sendable {
    public let id: String
    public let row: Int
    public let x: Double
    public let width: Double

    public init(id: String, row: Int, x: Double, width: Double) {
        self.id = id; self.row = row; self.x = x; self.width = width
    }
}

/// The left part of a keyboard that the tutorial draws for shortcuts with modifiers and for Escape: the Escape row, the
/// number row and the three letter rows to B and 5, and the bottom row of the platform. Keys the shortcut uses light up.
/// The keyboard carries on past its right edge, which fades out.
public enum TutorialKeyboard {
    /// A key unit, the gap between keys and a key's height, in points.
    public static let unit = 22.0
    public static let gap = 3.0
    public static let keyHeight = 20.0
    public static let rows = 6

    /// How much of the keyboard shows, in key units; keys past it are cut and the edge fades.
    public static let visibleUnits = 6.5

    /// The width that fades out at the right edge, in points.
    public static let fadeWidth = 36.0

    public static var width: Double { visibleUnits * unit }

    public static var height: Double { Double(rows) * keyHeight + Double(rows - 1) * gap }

    /// The keys, row by row from the Escape row; `mac` chooses the platform's bottom row.
    public static func keys(mac: Bool) -> [TutorialKey] {
        var result: [TutorialKey] = []
        func row(_ row: Int, _ items: [(id: String, width: Double)]) {
            var x = 0.0
            for item in items {
                result.append(TutorialKey(id: item.id, row: row, x: x, width: item.width))
                x += item.width
            }
        }
        row(0, [("esc", 1), ("f1", 1), ("f2", 1), ("f3", 1), ("f4", 1), ("f5", 1), ("f6", 1)])
        row(1, [("`", 1), ("1", 1), ("2", 1), ("3", 1), ("4", 1), ("5", 1), ("6", 1)])
        row(2, [("tab", 1.5), ("q", 1), ("w", 1), ("e", 1), ("r", 1), ("t", 1)])
        row(3, [("caps", 1.75), ("a", 1), ("s", 1), ("d", 1), ("f", 1), ("g", 1)])
        row(4, [("shift", 2.25), ("z", 1), ("x", 1), ("c", 1), ("v", 1), ("b", 1)])
        if mac {
            row(5, [("fn", 1), ("control", 1.25), ("option", 1.25), ("command", 1.5), ("space", 3)])
        } else {
            row(5, [("ctrl", 1.5), ("win", 1.25), ("alt", 1.25), ("space", 3)])
        }
        return result
    }

    /// Where `key` sits, in points from the keyboard's top-left corner.
    public static func frame(_ key: TutorialKey) -> Rect {
        Rect(x: key.x * unit + gap / 2, y: Double(key.row) * (keyHeight + gap), width: key.width * unit - gap,
             height: keyHeight)
    }

    /// The keyboard's key ids for a shortcut's keycaps: Windows' "Ctrl", "Shift", "Alt", "Win", "Esc" and characters, or
    /// the Mac's ⌃ ⇧ ⌥ ⌘ symbols, "esc" and characters. Nil when a key is not on the keyboard.
    public static func ids(_ keycaps: [String], mac: Bool) -> [String]? {
        let known = Set(keys(mac: mac).map(\.id))
        var result: [String] = []
        for cap in keycaps {
            let parts = mac && cap.count > 1 && cap != "esc" && cap != "return" ? cap.map { String($0) } : [cap]
            for part in parts {
                let id: String = switch part {
                case "Ctrl": "ctrl"
                case "Shift", "⇧": "shift"
                case "Alt": "alt"
                case "Win": "win"
                case "Esc", "esc": "esc"
                case "⌃": "control"
                case "⌥": "option"
                case "⌘": "command"
                default: part.lowercased()
                }
                guard known.contains(id) else { return nil }
                result.append(id)
            }
        }
        return result
    }

    /// Whether the card shows the keyboard for these keys: a shortcut with a modifier, or Escape, whose keys are all on the
    /// keyboard. Single keys such as 7 or B show only in the line.
    public static func shows(_ keycaps: [String], mac: Bool) -> Bool {
        guard let lit = ids(keycaps, mac: mac), !lit.isEmpty else { return false }
        let modifiers: Set<String> = ["esc", "ctrl", "shift", "alt", "win", "control", "option", "command"]
        return lit.contains { modifiers.contains($0) }
    }
}
