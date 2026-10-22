import AppKit
import SwiftUI
import AnnotationCore

/// Settings > Break timer (specification 014).
struct BreakTimerSettingsView: View {
    @ObservedObject var model: WorkspaceModel

    private var settings: BreakTimerSettings { model.breakTimerSettings }
    private var shortcut: ActivationShortcut { model.preferences.breakShortcut ?? .breakTimer }
    private var lockAvailable: Bool { model.breakTimerEnvironment.lock.isAvailable() }

    private func binding<Value>(_ keyPath: WritableKeyPath<BreakTimerSettings, Value>) -> Binding<Value> {
        Binding(get: { model.breakTimerSettings[keyPath: keyPath] }, set: { value in
            var updated = model.breakTimerSettings
            updated[keyPath: keyPath] = value
            model.setBreakTimerSettings(updated)
        })
    }

    private var minutes: Binding<Int> {
        Binding(get: { settings.minutes }, set: { value in
            var updated = settings
            updated.minutes = min(max(value, BreakTimerSettings.minuteRange.lowerBound), BreakTimerSettings.minuteRange.upperBound)
            model.setBreakTimerSettings(updated)
        })
    }

    private var title: Binding<String> {
        Binding(get: { settings.title }, set: { value in
            var updated = settings
            updated.title = BreakTimerSettings.cleanTitle(value)
            model.setBreakTimerSettings(updated)
        })
    }

    var body: some View {
        Form {
            Section {
                HStack(alignment: .center, spacing: 20) {
                    BreakSettingsPreview(settings: settings)
                        .frame(width: 320, height: 200)
                    VStack(alignment: .leading, spacing: 12) {
                        Text("Show a big countdown during a break, so everyone knows when to come back.")
                            .font(.callout)
                            .fixedSize(horizontal: false, vertical: true)
                        Button {
                            model.startBreak()
                        } label: {
                            Label("Start a \(settings.minutes)-minute break", systemImage: "timer")
                        }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.large)
                        .disabled(settings.lockWhenTouched && !lockAvailable)
                        .accessibilityIdentifier("settings-start-break")
                        HStack(spacing: 8) {
                            Text("Shortcut").font(.caption).foregroundStyle(.secondary)
                            ShortcutKeycaps(shortcut: shortcut)
                        }
                        .accessibilityElement(children: .combine)
                        .help("Change it in Toolbar & Shortcuts.")
                    }
                }
                .padding(.vertical, 6)
            }
            Section("Timer") {
                LabeledContent("Length") {
                    HStack(spacing: 6) {
                        TextField("Length", value: minutes, format: .number)
                            .labelsHidden()
                            .multilineTextAlignment(.trailing)
                            .frame(width: 52)
                            .accessibilityLabel("Length in minutes")
                            .accessibilityIdentifier("break-length")
                        Stepper("Length", value: minutes, in: BreakTimerSettings.minuteRange)
                            .labelsHidden()
                        Text("minutes").foregroundStyle(.secondary)
                    }
                }
                TextField("Title", text: title, prompt: Text("No title"))
                    .accessibilityIdentifier("break-title")
                Toggle("Show when the break ends", isOn: binding(\.showEndTime))
                    .help("Shows the end time, such as Back at 12:45.")
                Toggle("Keep counting after zero", isOn: binding(\.countOver))
                    .help("After zero, shows how long the break has run over, such as +1:23.")
                Toggle("Play a sound at zero", isOn: binding(\.chime))
            }
            Section("Look") {
                Picker("Background", selection: binding(\.background)) {
                    ForEach(BreakTimerSettings.Background.allCases, id: \.self) { background in
                        Text(background.label).tag(background)
                    }
                }
                .pickerStyle(.segmented)
                .accessibilityIdentifier("break-background")
                if settings.background == .blur {
                    Text("Blurred desktop needs no screen access. Large text can stay readable through the blur.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                LabeledContent("Colors") {
                    HStack(spacing: 10) {
                        ForEach(BreakTimerSettings.Palette.allCases, id: \.self) { palette in
                            PaletteSwatch(palette: palette, selected: settings.palette == palette) {
                                binding(\.palette).wrappedValue = palette
                            }
                        }
                    }
                }
                Toggle("Show on every display", isOn: binding(\.allDisplays))
                    .help("When this is off, the timer shows on the display with the pointer.")
            }
            Section("Locking") {
                Toggle("Lock when someone touches it", isOn: binding(\.lockWhenTouched))
                    .accessibilityIdentifier("break-lock")
                Text(settings.lockWhenTouched ?
                     "macOS doesn't let apps show anything on the lock screen. So the timer stays on screen, and from 3 seconds after it starts, your Mac locks as soon as someone presses a key, clicks or moves the mouse. Until then, your Mac stays signed in behind the timer. While your Mac is locked, the timer keeps counting and plays its sound at zero. It comes back when you sign in. If Annotator quits, the timer goes away." :
                     "To close the timer, press Esc or click End break. Anyone at your keyboard can do this.")
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if settings.lockWhenTouched {
                    Label("Sharing your screen in a meeting? Turn this off. Meeting apps can't show the lock screen, so people would see a frozen timer.",
                          systemImage: "video")
                        .font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .accessibilityIdentifier("break-meeting-note")
                }
                if settings.lockWhenTouched && !lockAvailable {
                    Label("Annotator can't lock this Mac. Turn this off to use the timer without locking.",
                          systemImage: "exclamationmark.triangle.fill")
                        .font(.callout).foregroundStyle(.orange)
                }
            }
        }
        .formStyle(.grouped)
    }
}

private struct PaletteSwatch: View {
    let palette: BreakTimerSettings.Palette
    let selected: Bool
    let action: () -> Void
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        let colors = palette.colors
        Button(action: action) {
            Circle()
                .fill(LinearGradient(colors: colors.fields.map { Color(nsColor: $0) },
                                     startPoint: .topLeading, endPoint: .bottomTrailing))
                .overlay(Circle().strokeBorder(Color(nsColor: colors.accent), lineWidth: selected ? 2.5 : 0))
                .padding(selected ? 0 : 3)
                .frame(width: 26, height: 26)
                .overlay {
                    if selected {
                        Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)).foregroundStyle(.white)
                    }
                }
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .animation(reduceMotion ? Motion.reduced : Motion.snappy, value: selected)
        .help(palette.label)
        .accessibilityLabel(palette.label)
        .accessibilityAddTraits(selected ? .isSelected : [])
        .accessibilityIdentifier("break-palette-\(palette.rawValue)")
    }
}

/// A small, live copy of the break screen. Blurred desktop shows over a drawn desktop, never a capture.
struct BreakSettingsPreview: View {
    let settings: BreakTimerSettings

    var body: some View {
        ZStack {
            if settings.background == .blur { PreviewDesktop() }
            BreakScreenFace(state: .preview(settings), preview: true)
        }
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.primary.opacity(0.12)))
        .shadow(color: .black.opacity(0.18), radius: 10, y: 4)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Preview of the break screen")
        .accessibilityValue("\(settings.title.isEmpty ? "No title" : settings.title), \(settings.minutes) minutes, \(settings.background.label), \(settings.palette.label)")
        .accessibilityIdentifier("break-preview")
    }
}

private struct PreviewDesktop: View {
    var body: some View {
        GeometryReader { proxy in
            let size = proxy.size
            ZStack(alignment: .topLeading) {
                LinearGradient(colors: [Color(red: 0.55, green: 0.70, blue: 0.86), Color(red: 0.93, green: 0.78, blue: 0.62)],
                               startPoint: .topLeading, endPoint: .bottomTrailing)
                window(width: size.width * 0.5, height: size.height * 0.55, lines: 5)
                    .offset(x: size.width * 0.08, y: size.height * 0.14)
                window(width: size.width * 0.38, height: size.height * 0.46, lines: 4)
                    .offset(x: size.width * 0.52, y: size.height * 0.38)
            }
        }
    }

    private func window(width: CGFloat, height: CGFloat, lines: Int) -> some View {
        VStack(alignment: .leading, spacing: height * 0.07) {
            HStack(spacing: 3) {
                ForEach([Color.red, .yellow, .green], id: \.self) { Circle().fill($0).frame(width: 5, height: 5) }
            }
            ForEach(0..<lines, id: \.self) { index in
                RoundedRectangle(cornerRadius: 2).fill(Color.black.opacity(0.35))
                    .frame(width: width * (index % 2 == 0 ? 0.8 : 0.55), height: 4)
            }
        }
        .padding(8)
        .frame(width: width, height: height, alignment: .topLeading)
        .background(RoundedRectangle(cornerRadius: 6).fill(.white))
        .shadow(color: .black.opacity(0.2), radius: 4, y: 2)
    }
}
