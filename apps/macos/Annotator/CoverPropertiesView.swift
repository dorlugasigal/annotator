import AppKit
import SwiftUI
import AnnotationCore

struct CoverPropertiesView: View {
    @ObservedObject var model: WorkspaceModel
    var compact = false
    var showsSlider = true

    var body: some View {
        HStack(spacing: 8) {
            if model.effectiveTool == .cover && model.selectionCount > 0 {
                Text("Solid cover · 100% opaque").font(.caption).foregroundStyle(.secondary)
                    .help("This existing cover stays opaque. Use Rectangle for new solid blocks.")
            } else {
                NativeEffectPicker(model: model).frame(width: compact ? 100 : 120)
                    .accessibilityIdentifier("cover-mode")
                if model.effectiveTool.isPixelEffect {
                    StyleValueControl(model: model, parameter: .effect, sliderWidth: showsSlider ? (compact ? 64 : 90) : 0, showLabel: !compact)
                }
            }
        }
        .controlSize(.small).disabled(model.selectionIsLocked)
    }
}

private struct NativeEffectPicker: NSViewRepresentable {
    @ObservedObject var model: WorkspaceModel

    func makeCoordinator() -> Coordinator { Coordinator(model: model) }

    func makeNSView(context: Context) -> NSPopUpButton {
        let picker = NSPopUpButton(frame: .zero, pullsDown: false)
        picker.controlSize = .small
        picker.font = .systemFont(ofSize: NSFont.systemFontSize(for: .small))
        picker.setAccessibilityLabel("Effect")
        picker.setAccessibilityIdentifier("cover-mode")
        for tool in [Tool.blur, .pixelate, .distort] {
            picker.addItem(withTitle: tool.rawValue.capitalized)
            let item = picker.lastItem
            item?.representedObject = tool.rawValue
            item?.target = context.coordinator
            item?.action = #selector(Coordinator.choose(_:))
        }
        return picker
    }

    func updateNSView(_ picker: NSPopUpButton, context: Context) {
        context.coordinator.model = model
        let tool = model.effectiveTool.isPixelEffect ? model.effectiveTool : model.preferredEffectTool
        picker.selectItem(withTitle: tool.rawValue.capitalized)
        picker.isEnabled = context.environment.isEnabled
    }

    @MainActor
    final class Coordinator: NSObject {
        weak var model: WorkspaceModel?
        init(model: WorkspaceModel) { self.model = model }

        @objc func choose(_ item: NSMenuItem) {
            guard let name = item.representedObject as? String,
                  let tool = Tool(rawValue: name), tool.isPixelEffect else {
                model?.report(CanvasError.invalidDocument("The effect choice is unavailable. Choose Blur, Pixelate or Distort."))
                return
            }
            model?.setCoverMode(tool)
        }
    }
}
