import AppKit
import AnnotationCore

public enum NativeTextFont {
    /// "monospace" is the system's monospaced font. "handwriting" is Caveat, in `Fonts/Caveat.ttf` (SIL Open Font
    /// License 1.1, `Fonts/Caveat-OFL.txt`); the Windows app ships the same file. Any other name is the system font.
    public static func resolve(_ style: Style, scale: Double = 1) -> NSFont {
        let weight: NSFont.Weight = style.bold ? .semibold : .regular
        let size = style.fontSize * scale
        switch style.fontName {
        case "monospace": return NSFont.monospacedSystemFont(ofSize: size, weight: weight)
        case "handwriting": return handwritingFont(size: size, bold: style.bold) ?? NSFont.systemFont(ofSize: size, weight: weight)
        default: return NSFont.systemFont(ofSize: size, weight: weight)
        }
    }

    /// Caveat's weight axis: 400 is its Regular, and Bold text uses 600, the semibold the other fonts use.
    private static let weightAxis = NSNumber(value: 0x7767_6874)
    private static let semibold: CGFloat = 600

    /// Caveat's Regular, registered for this process so that text views and copied text find it by name too.
    private nonisolated(unsafe) static let handwritingDescriptor: CTFontDescriptor? = {
        guard let url = Bundle.module.url(forResource: "Caveat", withExtension: "ttf", subdirectory: "Fonts") else { return nil }
        CTFontManagerRegisterFontsForURL(url as CFURL, .process, nil)
        return (CTFontManagerCreateFontDescriptorsFromURL(url as CFURL) as? [CTFontDescriptor])?.first
    }()

    private static func handwritingFont(size: Double, bold: Bool) -> NSFont? {
        guard let regular = handwritingDescriptor else { return nil }
        let descriptor = bold ? CTFontDescriptorCreateCopyWithVariation(regular, weightAxis, semibold) : regular
        return CTFontCreateWithFontDescriptor(descriptor, size, nil) as NSFont
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
