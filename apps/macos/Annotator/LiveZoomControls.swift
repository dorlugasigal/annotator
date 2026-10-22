import SwiftUI

struct LiveZoomControls: View {
    @ObservedObject var model: WorkspaceModel
    let compact: Bool

    var body: some View {
        VStack(spacing: 4) {
            HStack(spacing: 2) {
                Button { model.magnification = max(1, model.magnification / 1.25) } label: {
                    Image(systemName: "minus").frame(width: compact ? 20 : 26, height: 26)
                }.accessibilityLabel("Decrease live zoom").disabled(model.magnification <= 1)
                if !compact { Text(String(format: "%.1f×", model.magnification)).font(.caption.monospacedDigit()).frame(width: 40) }
                Button { model.magnification = min(16, model.magnification * 1.25) } label: {
                    Image(systemName: "plus").frame(width: compact ? 20 : 26, height: 26)
                }.accessibilityLabel("Increase live zoom").disabled(model.magnification >= 16)
            }.buttonStyle(.plain)
            if compact { Text(String(format: "%.1f×", model.magnification)).font(.system(size: 10).monospacedDigit()) }
        }
        .frame(width: compact ? 44 : 145)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Live zoom level")
    }
}
