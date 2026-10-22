import AppKit
import AnnotationCore

public enum NativeTextFont {
    public static func resolve(_ style: Style, scale: Double = 1) -> NSFont {
        let weight: NSFont.Weight = style.bold ? .semibold : .regular
        let size = style.fontSize * scale
        return style.fontName == "monospace"
            ? NSFont.monospacedSystemFont(ofSize: size, weight: weight)
            : NSFont.systemFont(ofSize: size, weight: weight)
    }

    public static func contentSize(_ text: String, font: NSFont) -> CGSize {
        let measured = ((text.isEmpty ? " " : text) as NSString).boundingRect(
            with: CGSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude),
            options: [.usesLineFragmentOrigin, .usesFontLeading], attributes: [.font: font])
        let extraLine = text.hasSuffix("\n") ? font.ascender - font.descender + font.leading : 0
        return CGSize(width: max(32, ceil(measured.width) + 2),
                      height: max(ceil(font.ascender - font.descender), ceil(measured.height + extraLine) + 2))
    }
}
