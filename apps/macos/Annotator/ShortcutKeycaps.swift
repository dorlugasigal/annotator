import AnnotationCore
import SwiftUI

struct Keycap: View {
    let text: String

    var body: some View {
        Text(text)
            .font(.system(.body, design: .monospaced).weight(.semibold))
            .padding(.horizontal, 8).frame(minWidth: 28, minHeight: 28)
            .background(.background, in: RoundedRectangle(cornerRadius: 6))
            .overlay(RoundedRectangle(cornerRadius: 6).stroke(.primary.opacity(0.18), lineWidth: 1))
            .shadow(color: .black.opacity(0.08), radius: 0, y: 2)
    }
}

struct ShortcutKeycaps: View {
    let shortcut: ActivationShortcut

    private var keys: [(symbol: String, name: String)] {
        var result: [(String, String)] = []
        if shortcut.control { result.append(("⌃", "Control")) }
        if shortcut.option { result.append(("⌥", "Option")) }
        if shortcut.shift { result.append(("⇧", "Shift")) }
        if shortcut.command { result.append(("⌘", "Command")) }
        result.append((shortcut.character.uppercased(), shortcut.character.uppercased()))
        return result
    }

    var body: some View {
        HStack(spacing: 5) {
            ForEach(Array(keys.enumerated()), id: \.offset) { _, key in
                Keycap(text: key.symbol).help(key.name)
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(keys.map(\.name).joined(separator: " + "))
        .accessibilityValue(shortcut.label)
        .help(keys.map(\.name).joined(separator: " + "))
    }
}
