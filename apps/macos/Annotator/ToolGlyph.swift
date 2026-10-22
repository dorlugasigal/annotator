import SwiftUI
import AnnotationCore
import PresentationRendering

struct ToolGlyph: View {
    let tool: Tool
    var size: CGFloat = 21

    var body: some View {
        Group {
            if tool.systemShapeAspectRatio != nil {
                SymbolOutline(tool: tool)
                    .stroke(style: StrokeStyle(lineWidth: size/12, lineCap: .round, lineJoin: .round))
                    .padding(size/12)
            } else {
                Image(systemName: tool.toolbarSymbol).font(.system(size: size))
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }

    private struct SymbolOutline: Shape {
        let tool: Tool
        func path(in rect: CGRect) -> Path {
            Path(SystemDiagramGeometry.path(for: Element(tool: tool, frame: Rect(rect))))
        }
    }
}

/// A toolbar item's icon: a tool's drawn glyph or a button's symbol.
struct CommandGlyph: View {
    let command: ShortcutCommand
    var size: CGFloat = 21

    var body: some View {
        switch command {
        case .tool(let tool): ToolGlyph(tool: tool, size: size)
        case .action(let action):
            Image(systemName: action.symbol).font(.system(size: size * 0.88))
                .frame(width: size, height: size).accessibilityHidden(true)
        }
    }
}
