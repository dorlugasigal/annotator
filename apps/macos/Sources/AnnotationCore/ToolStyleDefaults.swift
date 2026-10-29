import Foundation

extension Style {
    public static func defaults(for tool: Tool) -> Style {
        var style = Style()
        if tool == .arrow { style.headScale = 0.75 }
        if tool == .highlighter {
            style.color = RGBA(0.93, 1, 0.05)
            style.width = 4
        }
        if tool == .laser {
            style.color = LaserTrail.defaultColor
            style.width = LaserTrail.defaultWidth
        }
        return style
    }

    /// The gradient a brand-new profile starts with.
    public static let firstUseGradient = GradientPreset.sunset

    /// What a tool draws with in a brand-new profile: the defaults, with the first-use gradient on drawing tools.
    public static func firstUse(for tool: Tool) -> Style {
        var style = defaults(for: tool)
        if tool.startsWithGradient { style.setGradient(firstUseGradient.gradient, fill: false) }
        return style
    }
}

extension Tool {
    /// Shapes, lines, arrows, pen and text start with a gradient in a new profile. The highlighter keeps its yellow, the
    /// laser pointer its red, and tools whose color is not ink (select, eraser, censors, ruler, counter, image) keep their
    /// defaults.
    public var startsWithGradient: Bool {
        switch self {
        case .select, .highlighter, .laser, .eraser, .cover, .blur, .pixelate, .distort, .ruler, .counter, .image: false
        default: true
        }
    }
}

extension WorkspacePreferences {
    /// Preferences for someone with no saved profile. Saved profiles never pass through here.
    public static var firstUse: WorkspacePreferences {
        var preferences = WorkspacePreferences()
        for tool in Tool.allCases where tool.startsWithGradient {
            preferences.toolStyles[tool.rawValue] = Style.firstUse(for: tool)
        }
        return preferences
    }
}
