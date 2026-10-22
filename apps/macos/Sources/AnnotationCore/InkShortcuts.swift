import Foundation

public struct InkShortcut: Sendable {
    public let name: String
    public let color: RGBA
    public static let choices: [String: InkShortcut] = [
        "r": InkShortcut(name: "Red", color: .coral),
        "g": InkShortcut(name: "Green", color: RGBA(0.28, 0.69, 0.4)),
        "b": InkShortcut(name: "Blue", color: .blue),
        "y": InkShortcut(name: "Yellow", color: .yellow),
        "o": InkShortcut(name: "Orange", color: RGBA(1, 0.66, 0.23)),
        "p": InkShortcut(name: "Pink", color: RGBA(0.89, 0.26, 0.4)),
        "c": InkShortcut(name: "Cyan", color: RGBA(0, 0.6, 0.7)),
        "w": InkShortcut(name: "White", color: .white),
        "k": InkShortcut(name: "Black", color: RGBA(0, 0, 0))
    ]
}
