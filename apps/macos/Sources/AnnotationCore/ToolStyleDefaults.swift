import Foundation

extension Style {
    public static func defaults(for tool: Tool) -> Style {
        var style = Style()
        if tool == .arrow { style.headScale = 0.75 }
        if tool == .highlighter {
            style.color = RGBA(0.93, 1, 0.05)
            style.width = 4
        }
        return style
    }
}
