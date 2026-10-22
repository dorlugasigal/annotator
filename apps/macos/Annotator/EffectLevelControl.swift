import SwiftUI
import AnnotationCore

struct EffectLevelControl: View {
    @ObservedObject var model: WorkspaceModel

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 12) {
                StyleValueControl(model: model, parameter: .effect, sliderWidth: 180)
                Spacer()
                if model.selectionCount > 0 { Button("Delete region") { model.deleteSelected() } }
            }
            Text("Adjusts the selected region. Visual effects are not irreversible redaction.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }
}
