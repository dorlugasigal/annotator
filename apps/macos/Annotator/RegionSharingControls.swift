import SwiftUI
import AnnotationCore

struct RegionSharingControls: View {
    @ObservedObject var sharing: RegionSharingController
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var x = ""
    @State private var y = ""
    @State private var width = ""
    @State private var height = ""
    @State private var appliedFields: [String] = []
    private var customBounds: Rect? {
        guard let x = Double(x), let y = Double(y), let width = Double(width), let height = Double(height) else { return nil }
        return Rect(x: x, y: y, width: width, height: height)
    }
    private var draftChanged: Bool { [x, y, width, height] != appliedFields }

    var body: some View {
        GeometryReader { geometry in
            if geometry.size.height < 580 || geometry.size.width < 820 {
                ScrollView { content }.accessibilityIdentifier("region-short-screen-scroll")
            } else { content }
        }
        .onAppear(perform: syncFields)
        .onChange(of: sharing.region?.bounds) { _, _ in syncFields() }
        .onExitCommand { sharing.controls?.close() }
    }

    private var content: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .top, spacing: 16) {
                Image(systemName: "rectangle.on.rectangle")
                    .font(.system(size: 24, weight: .medium))
                    .foregroundStyle(Color.accentColor)
                    .frame(width: 48, height: 48)
                    .background(Color.accentColor.opacity(0.08), in: RoundedRectangle(cornerRadius: 12))
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 5) {
                    Text("Share a region").font(.system(size: 24, weight: .semibold))
                    Text("Give the call a clear view. Keep the rest of your screen to yourself.")
                        .font(.callout).foregroundStyle(.secondary)
                }
                Spacer(minLength: 12)
                statusBadge
            }
            .padding(.bottom, 24)

            HStack(alignment: .top, spacing: 28) {
                VStack(alignment: .leading, spacing: 16) {
                    Picker("Display", selection: Binding(
                        get: { sharing.selectedDisplayID ?? 0 }, set: { sharing.selectDisplay($0) })) {
                        if sharing.selectedDisplay == nil { Text("Choose a display").tag(sharing.selectedDisplayID ?? 0) }
                        ForEach(sharing.displays) { display in Text(display.name).tag(display.id) }
                    }
                    .accessibilityIdentifier("region-display")
                    preview
                    HStack(alignment: .firstTextBaseline) {
                        VStack(alignment: .leading, spacing: 4) {
                            Text(sharing.selectedPreset?.title ?? "Custom region").font(.headline)
                            if let display = sharing.selectedDisplay, let region = sharing.region {
                                Text("\(Int((region.bounds.width * display.scale).rounded(.down))) × \(Int((region.bounds.height * display.scale).rounded(.down))) pixels")
                                    .font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                            }
                        }
                        Spacer()
                        Label("Outlined on your screen", systemImage: "viewfinder")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    Text("Anything inside this area can appear, including notifications. Keep private windows outside it. Blur is not secure redaction.")
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                .frame(maxWidth: .infinity, alignment: .topLeading)

                VStack(alignment: .leading, spacing: 14) {
                    presetGroup("Halves", presets: [.leftHalf, .rightHalf, .topHalf, .bottomHalf])
                    presetGroup("Thirds", presets: [.leftThird, .centerThird, .rightThird])
                    presetGroup("Corners", presets: [.topLeft, .topRight, .bottomLeft, .bottomRight])
                    Divider()
                    HStack {
                        Text("Your own area").font(.subheadline.weight(.semibold))
                        Spacer()
                        Button("Draw a custom region", systemImage: "viewfinder") { sharing.drawCustomRegion() }
                            .controlSize(.small).accessibilityIdentifier("region-draw-custom")
                    }
                    HStack(spacing: 10) {
                        field("X", text: $x)
                        field("Y", text: $y)
                        field("Width", text: $width)
                        field("Height", text: $height)
                    }
                    HStack {
                        Text("Points from the display's top-left corner.")
                            .font(.caption2).foregroundStyle(.secondary)
                        Spacer()
                        Button("Apply size", action: applyCustom).controlSize(.small)
                            .disabled(!draftChanged)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .topLeading)
            }
            Spacer(minLength: 16)
            if let message = sharing.validationMessage {
                Label(message, systemImage: "exclamationmark.circle").font(.caption).foregroundStyle(.red)
                    .accessibilityIdentifier("region-validation-error").padding(.bottom, 10)
            } else if let message = sharing.state.message {
                Text(message).font(.caption).foregroundStyle(.secondary).padding(.bottom, 10)
            }
            Divider()
            HStack(alignment: .center, spacing: 24) {
                VStack(alignment: .leading, spacing: 6) {
                    Text("In your meeting, choose Share > Window.")
                        .font(.callout.weight(.medium))
                    Text("Select \(SharedRegionWindow.title). Hiding drawings removes them from the call; the region stays on.")
                        .font(.caption).foregroundStyle(.secondary)
                    if Bundle.main.bundleIdentifier?.hasSuffix(".dev") == true {
                        Text("Local build").font(.caption2).foregroundStyle(.secondary)
                    }
                }
                Spacer(minLength: 0)
                Button(sharing.state.isCapturing ? "Stop region sharing" : "Start region sharing") { sharing.toggle() }
                    .buttonStyle(.borderedProminent).controlSize(.large)
                    .tint(sharing.state.isCapturing ? .red : .accentColor)
                    .disabled(!sharing.state.isCapturing &&
                              (sharing.region == nil || sharing.validationMessage != nil || draftChanged))
                    .accessibilityIdentifier("region-start-stop")
                    .accessibilityValue(sharing.state.isCapturing ? "On" : "Off")
            }
            .padding(.top, 18)
        }
        .padding(28)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private var statusBadge: some View {
        HStack(spacing: 7) {
            Circle().fill(sharing.state.isCapturing ? Color.green : Color.secondary).frame(width: 7, height: 7)
            Text(sharing.state == .starting ? "Starting" : sharing.state.isCapturing ? "On" : "Off")
                .font(.callout.weight(.semibold))
        }
        .padding(.horizontal, 14).padding(.vertical, 8)
        .background(Color.secondary.opacity(0.07), in: Capsule())
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(sharing.state.title)
    }

    private var preview: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 14).fill(Color.primary.opacity(0.04))
            if let display = sharing.selectedDisplay {
                RegionPreview(display: display.localBounds, region: sharing.region?.bounds, animated: true)
                    .padding(22)
            } else {
                Label("Choose a display", systemImage: "display").foregroundStyle(.secondary)
            }
        }
        .frame(height: 224)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Region preview")
        .accessibilityValue(sharing.selectedPreset?.title ?? "Custom region")
    }

    private func presetGroup(_ title: String, presets: [ShareRegionPreset]) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            Text(title).font(.caption.weight(.medium)).foregroundStyle(.secondary)
            HStack(spacing: 7) {
                ForEach(presets, id: \.self) { preset in
                    Button { sharing.choosePreset(preset) } label: {
                        VStack(spacing: 5) {
                            RegionPreview(display: Rect(x: 0, y: 0, width: 1, height: 1),
                                region: preset.normalizedBounds).frame(width: 30, height: 20)
                            Text(preset.title).font(.system(size: 10, weight: .medium))
                        }
                        .frame(maxWidth: .infinity, minHeight: 48)
                        .background(sharing.selectedPreset == preset ? Color.accentColor.opacity(0.12) : Color.primary.opacity(0.035),
                                    in: RoundedRectangle(cornerRadius: 8))
                        .overlay(RoundedRectangle(cornerRadius: 8)
                            .stroke(sharing.selectedPreset == preset ? Color.accentColor.opacity(0.8) : .clear, lineWidth: 1))
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(preset.title)
                    .accessibilityAddTraits(sharing.selectedPreset == preset ? .isSelected : [])
                }
            }
        }
        .animation(reduceMotion ? nil : Motion.fadeIn, value: sharing.selectedPreset)
    }

    private func field(_ title: String, text: Binding<String>) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.caption2).foregroundStyle(.secondary)
            TextField(title, text: text).textFieldStyle(.roundedBorder).onSubmit(applyCustom)
                .accessibilityLabel("Region \(title.lowercased()) in points")
        }
    }

    private func applyCustom() {
        if let customBounds {
            sharing.applyCustom(customBounds)
            if sharing.validationMessage == nil { syncFields() }
        } else { sharing.invalidCustomNumbers() }
    }

    private func syncFields() {
        guard let bounds = sharing.region?.bounds else { return }
        func value(_ number: Double) -> String {
            number.formatted(.number.locale(Locale(identifier: "en_US_POSIX")).grouping(.never).precision(.fractionLength(0...6)))
        }
        x = value(bounds.x); y = value(bounds.y)
        width = value(bounds.width); height = value(bounds.height)
        appliedFields = [x, y, width, height]
    }
}

private struct RegionPreview: View {
    let display: Rect
    let region: Rect?
    var animated = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        GeometryReader { proxy in
            let scale = min(proxy.size.width / display.width, proxy.size.height / display.height)
            let size = CGSize(width: display.width * scale, height: display.height * scale)
            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 5).fill(Color.secondary.opacity(0.12))
                RoundedRectangle(cornerRadius: 5).stroke(Color.secondary.opacity(0.25), lineWidth: 1)
                if let region {
                    RoundedRectangle(cornerRadius: 3)
                        .fill(Color.accentColor.opacity(0.18))
                        .overlay(RoundedRectangle(cornerRadius: 3).stroke(Color.accentColor, lineWidth: 1.5))
                        .frame(width: region.width * scale, height: region.height * scale)
                        .offset(x: (region.x - display.x) * scale, y: (region.y - display.y) * scale)
                        .animation(animated && !reduceMotion ? Motion.smooth : nil, value: region)
                }
            }
            .frame(width: size.width, height: size.height)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .accessibilityHidden(true)
    }
}
