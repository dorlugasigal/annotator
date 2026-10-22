import AppKit
import AnnotationCore

public enum ShapeLabelLayout {
    public static func textElement(for owner: Element, label: ShapeLabel? = nil) -> Element? {
        guard let label = label ?? owner.label, owner.tool.isShape else { return nil }
        let size = NativeTextFont.contentSize(label.text, font: NativeTextFont.resolve(label.style))
        var style = label.resolvedStyle(for: owner.style)
        style.opacity *= owner.style.opacity
        var text = Element(id: owner.id, tool: .text,
            frame: Rect(x: owner.frame.center.x-size.width/2, y: owner.frame.center.y-size.height/2,
                        width: size.width, height: size.height), text: label.text, style: style)
        text.rotation = owner.rotation
        return text
    }

    public static func fittedOwnerFrame(_ owner: Element, label: ShapeLabel) -> Rect {
        guard let text = textElement(for: owner, label: label) else { return owner.frame }
        if owner.tool.systemShapeAspectRatio != nil {
            let box = owner.symbolFrame
            let scale = max(1, max((text.frame.width+24)/max(1,box.width), (text.frame.height+24)/max(1,box.height)))
            return Rect(x: owner.frame.center.x-owner.frame.width*scale/2,
                        y: owner.frame.center.y-owner.frame.height*scale/2,
                        width: owner.frame.width*scale, height: owner.frame.height*scale)
        }
        let width = max(owner.frame.width, text.frame.width + 24)
        let height = max(owner.frame.height, text.frame.height + 24)
        return Rect(x: owner.frame.center.x-width/2, y: owner.frame.center.y-height/2,
                    width: width, height: height)
    }
}
