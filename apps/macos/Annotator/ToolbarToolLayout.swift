import Foundation
import AnnotationCore

/// Sizes for the two toolbar densities. Expanded keeps the original measurements.
struct ToolbarMetrics: Equatable {
    let size: ToolbarSize
    init(_ size: ToolbarSize = .expanded) { self.size = size }

    var minimal: Bool { size == .minimal }
    var buttonWidth: CGFloat { minimal ? 34 : 44 }
    var buttonHeight: CGFloat { minimal ? 36 : 48 }
    var glyphSize: CGFloat { minimal ? 17 : 19 }
    var showsKeyLabels: Bool { !minimal }
    var familyGap: CGFloat { minimal ? 9 : 11 }
    var dividerPadding: CGFloat { minimal ? 2 : 3 }
    var padding: CGFloat { minimal ? 8 : 12 }
    var spacing: CGFloat { minimal ? 4 : 6 }
    var cornerRadius: CGFloat { minimal ? 13 : 15 }
    var actionSize: CGSize { minimal ? CGSize(width: 22, height: 24) : CGSize(width: 24, height: 26) }
    var boardWidth: CGFloat { minimal ? 34 : 44 }
    var modeWidth: CGFloat { minimal ? 46 : 60 }
    /// Room for the one-row action strip, gaps and padding next to the tools.
    var regularReserve: CGFloat { minimal ? 316 : 392 }
    var compactReserve: CGFloat { minimal ? 316 : 332 }
    var railReserve: CGFloat { regularReserve - 16 }
    var compactRailReserve: CGFloat { minimal ? 150 : 214 }
    var propertiesWidth: CGFloat { minimal ? 640 : 700 }
    /// Full properties below a minimal rail keep the expanded toolbar's layout, drawn smaller by as much as the tool buttons,
    /// so a tool's properties stay on one row.
    var propertiesScale: CGFloat { minimal ? 34.0 / 44.0 : 1 }
    var minimumWidth: CGFloat { minimal ? 300 : 400 }
    var compactThreshold: CGFloat { minimal ? 420 : 700 }
    var verticalRailWidth: CGFloat { buttonWidth + padding * 2 }
    var verticalToolPitch: CGFloat { minimal ? 39 : 51 }
    /// Room for the rail's own buttons below the tools, with gaps and padding.
    var verticalReserve: CGFloat { minimal ? 346 : 354 }

    /// The width one of the toolbar's own controls takes in the horizontal row, with its gap.
    func horizontalRoom(_ control: ToolbarControl) -> CGFloat {
        switch control {
        case .library: buttonWidth + spacing
        case .board: boardWidth + spacing
        case .mode: modeWidth + spacing
        case .undo, .redo, .clearAll, .properties, .settings, .hide: actionSize.width + spacing
        }
    }

    /// The divider before Board and Draw, which goes when both of them do.
    func dividerRoom(hiding hidden: Set<ToolbarControl>) -> CGFloat {
        hidden.isSuperset(of: [.board, .mode]) ? 1 + spacing : 0
    }

    func regularReserve(hiding hidden: Set<ToolbarControl>) -> CGFloat {
        regularReserve - hidden.reduce(0) { $0 + horizontalRoom($1) } - dividerRoom(hiding: hidden)
    }

    /// A narrow toolbar keeps More tools, Board and Draw beside the tools and moves the other buttons below them.
    func compactReserve(hiding hidden: Set<ToolbarControl>) -> CGFloat {
        compactReserve - firstRowRoom(hiding: hidden)
    }

    func railReserve(hiding hidden: Set<ToolbarControl>) -> CGFloat { regularReserve(hiding: hidden) - 16 }

    func compactRailReserve(hiding hidden: Set<ToolbarControl>) -> CGFloat {
        compactRailReserve - firstRowRoom(hiding: hidden)
    }

    private func firstRowRoom(hiding hidden: Set<ToolbarControl>) -> CGFloat {
        hidden.intersection([.library, .board, .mode]).reduce(0) { $0 + horizontalRoom($1) } + dividerRoom(hiding: hidden)
    }

    /// The height one of the toolbar's own controls takes in the vertical rail, with its gap.
    func verticalRoom(_ control: ToolbarControl, hiding hidden: Set<ToolbarControl>) -> CGFloat {
        let gap: CGFloat = minimal ? 3 : 5
        switch control {
        case .library: return buttonHeight + gap
        case .board: return (minimal ? 30 : 36) + gap
        case .mode: return 36 + gap
        case .properties, .clearAll, .settings, .hide: return actionSize.height + gap
        // Undo and Redo share a row in the expanded rail and stack in the minimal one.
        case .undo, .redo:
            if minimal { return actionSize.height + 3 }
            return control == .redo && hidden.isSuperset(of: [.undo, .redo]) ? actionSize.height + gap : 0
        }
    }

    func verticalReserve(hiding hidden: Set<ToolbarControl>) -> CGFloat {
        verticalReserve - hidden.reduce(0) { $0 + verticalRoom($1, hiding: hidden) }
    }
}

enum ToolbarToolLayout {
    static let buttonWidth: CGFloat = 44
    /// The buttons a narrow toolbar moves below its tools.
    static let secondRowControls: Set<ToolbarControl> = [.undo, .redo, .clearAll, .properties, .settings, .hide]
    static func horizontalWidth(tools: [Tool], available: CGFloat, propertiesVisible: Bool,
                                size: ToolbarSize = .expanded, hiding hidden: Set<ToolbarControl> = []) -> CGFloat {
        horizontalWidth(items: tools.map(ShortcutCommand.tool), available: available,
                        propertiesVisible: propertiesVisible, size: size, hiding: hidden)
    }

    /// The toolbar's width for its tools and buttons. Full properties can make it wider (`ToolbarWidthLayout`).
    static func horizontalWidth(items: [ShortcutCommand], available: CGFloat, propertiesVisible: Bool,
                                size: ToolbarSize = .expanded, hiding hidden: Set<ToolbarControl> = []) -> CGFloat {
        let metrics = ToolbarMetrics(size)
        let toolWidth = width(of: items, size: size)
        // Reserve the measured full or compact action strip, including gaps and padding.
        let regular = toolWidth + metrics.regularReserve(hiding: hidden)
        // A narrow toolbar moves Undo, Redo and the other buttons to a second row; without them it keeps one row.
        let desired = regular >= metrics.compactThreshold || hidden.isSuperset(of: secondRowControls) ? regular :
            toolWidth + metrics.compactReserve(hiding: hidden)
        // The default tools fit in 1000 points; a toolbar with added items may grow up to the screen width.
        return min(max(320, available), max(propertiesVisible ? metrics.propertiesWidth : metrics.minimumWidth, desired))
    }

    /// The toolbar button that stands for the current tool. Effects share one button unless they have their own.
    static func identity(for tool: Tool, in items: [ShortcutCommand]) -> Tool {
        if tool.isProtection { return items.contains(.tool(tool)) ? tool : .cover }
        if tool.isShape && !items.contains(.tool(tool)) && items.contains(.tool(.rectangle)) { return .rectangle }
        return tool
    }

    static func identity(for tool: Tool, in tools: [Tool]) -> Tool {
        identity(for: tool, in: tools.map(ShortcutCommand.tool))
    }

    static func family(_ tool: Tool) -> Int {
        if tool.isShape || tool.isConnector { return 1 }
        return switch tool {
        case .select: 0
        case .rectangle, .diamond, .ellipse, .triangle, .hexagon, .star, .arrow, .line, .database, .queue, .person, .grid, .axes: 1
        case .pen, .text, .highlighter: 2
        case .eraser: 3
        case .cover, .blur, .pixelate, .distort: 4
        default: 5
        }
    }

    static func family(_ item: ShortcutCommand) -> Int {
        switch item {
        case .tool(let tool): family(tool)
        case .action: 6
        }
    }

    static func width(of items: [ShortcutCommand], size: ToolbarSize = .expanded) -> CGFloat {
        let metrics = ToolbarMetrics(size)
        return items.enumerated().reduce(0) { width, item in
            let gap: CGFloat = item.offset == 0 ? 0 : family(items[item.offset - 1]) == family(item.element) ? 2 : metrics.familyGap
            return width + gap + metrics.buttonWidth
        }
    }

    static func width(of tools: [Tool], size: ToolbarSize = .expanded) -> CGFloat {
        width(of: tools.map(ShortcutCommand.tool), size: size)
    }

    static func visible(_ items: [ShortcutCommand], selected: ShortcutCommand, width: CGFloat,
                        size: ToolbarSize = .expanded) -> [ShortcutCommand] {
        func fit(from start: Int) -> [ShortcutCommand] {
            var result: [ShortcutCommand] = []
            for item in items.dropFirst(start) {
                guard Self.width(of: result + [item], size: size) <= width else { break }
                result.append(item)
            }
            return result
        }
        let first = fit(from: 0)
        guard let index = items.firstIndex(of: selected), !first.contains(selected) else { return first }
        var start = max(0, index - first.count + 1)
        while start < index {
            let result = fit(from: start)
            if result.contains(selected) { return result }
            start += 1
        }
        return fit(from: index)
    }

    static func visible(_ tools: [Tool], selected: Tool, width: CGFloat, size: ToolbarSize = .expanded) -> [Tool] {
        visible(tools.map(ShortcutCommand.tool), selected: .tool(selected), width: width, size: size)
            .compactMap { if case .tool(let tool) = $0 { tool } else { nil } }
    }
}
