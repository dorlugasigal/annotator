import Foundation

// Immutable labels can be shared safely by document history and duplicate shapes.
public final class ShapeLabel: Codable, Equatable, Sendable {
    public let text: String
    public let style: Style
    public let followsStroke: Bool?

    public init(text: String = "", style: Style? = nil, followsStroke: Bool? = nil) {
        self.text = text
        self.followsStroke = followsStroke
        var resolved = style ?? Style()
        if style == nil {
            resolved.alignment = .center
            resolved.color = .ink
        }
        self.style = resolved
    }

    public var estimatedBytes: Int { 384 + text.utf8.count + style.fontName.utf8.count + style.paintEstimatedBytes }
    public var hasVisibleText: Bool { !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    public func resolvedStyle(for owner: Style) -> Style {
        var resolved = style
        if followsStroke == true {
            resolved.color = owner.color
            resolved.strokeGradient = owner.strokeGradient
        }
        return resolved
    }

    public static func defaultColor(on fill: RGBA?) -> RGBA {
        guard let fill, fill.alpha > 0.6 else { return RGBA(0, 0, 0) }
        func linear(_ value: Double) -> Double {
            value <= 0.04045 ? value/12.92 : pow((value+0.055)/1.055, 2.4)
        }
        let luminance = linear(fill.red)*0.2126 + linear(fill.green)*0.7152 + linear(fill.blue)*0.0722
        return luminance < 0.179 ? .white : RGBA(0, 0, 0)
    }

    public func validate() throws {
        guard text.utf8.count <= 1_000_000 else {
            throw CanvasError.invalidDocument("The shape label exceeds the supported text limit.")
        }
        try style.validate()
    }

    public static func == (lhs: ShapeLabel, rhs: ShapeLabel) -> Bool {
        lhs.text == rhs.text && lhs.style == rhs.style && lhs.followsStroke == rhs.followsStroke
    }
}
