import AppKit
import SwiftUI
import XCTest
import AnnotationCore
import PresentationRendering
@testable import AnnotatorApp

@MainActor
final class InspectorPolishTests: XCTestCase {
    func testSmartDrawGlyphUsesLightInkInDarkModeAndDarkInkInLightMode() throws {
        let suite = "smart-draw-ink-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName:suite))
        let model = WorkspaceModel(defaults:defaults)
        defer { model.closeAll();defaults.removePersistentDomain(forName:suite) }
        for dark in [false, true] {
            let renderer = ImageRenderer(content: SmartDrawButton(model:model)
                .environment(\.colorScheme,dark ? .dark : .light))
            renderer.scale = 2
            let bitmap = NSBitmapImageRep(cgImage:try XCTUnwrap(renderer.cgImage))
            var ink: [Double] = []
            for x in 0..<bitmap.pixelsWide {
                for y in 0..<bitmap.pixelsHigh {
                    if let color=bitmap.colorAt(x:x,y:y)?.usingColorSpace(.sRGB),color.alphaComponent>0.8 {
                        ink.append((color.redComponent+color.greenComponent+color.blueComponent)/3)
                    }
                }
            }
            XCTAssertGreaterThan(ink.count,10)
            let brightness=ink.reduce(0,+)/Double(max(1,ink.count))
            if dark { XCTAssertGreaterThan(brightness,0.75) }
            else { XCTAssertLessThan(brightness,0.35) }
        }
    }

    func testContextMenuPreservesMultipleSelectionAndProvidesEditingActions() throws {
        let suite="context-menu-\(UUID())"
        let defaults=try XCTUnwrap(UserDefaults(suiteName:suite))
        let image=try PNGExporter.image(elements:[],size:CGSize(width:500,height:350),background:.white)
        let fixture=NativeCanvasFixture(image:image,defaults:defaults)
        defer { fixture.close();defaults.removePersistentDomain(forName:suite) }
        let first=Element(tool:.triangle,frame:Rect(x:50,y:50,width:100,height:100))
        let second=Element(tool:.star,frame:Rect(x:200,y:50,width:100,height:100))
        fixture.session.document=try CanvasDocument(elements:[first,second])
        fixture.session.document.select(Set(fixture.session.document.elements.map(\.id)))
        let canvas=fixture.session.canvas
        let event=try XCTUnwrap(NSEvent.mouseEvent(with:.rightMouseDown,location:canvas.convert(CGPoint(x:100,y:100),to:nil),
            modifierFlags:[],timestamp:0,windowNumber:fixture.session.panel.windowNumber,context:nil,eventNumber:0,clickCount:1,pressure:0))
        let menu=try XCTUnwrap(canvas.menu(for:event))
        XCTAssertEqual(fixture.session.document.selection.count,2)
        for title in ["Properties","Duplicate","Group","Ungroup","Lock","Unlock","Bring to front","Send to back","Save to My shapes...","Delete selected"] {
            XCTAssertNotNil(menu.item(withTitle:title),title)
        }
        let group=try XCTUnwrap(menu.item(withTitle:"Group"))
        XCTAssertTrue(group.isEnabled)
        XCTAssertTrue(NSApp.sendAction(try XCTUnwrap(group.action),to:group.target,from:group))
        XCTAssertEqual(fixture.session.document.elements[0].groupID,fixture.session.document.elements[1].groupID)
        XCTAssertNotNil(fixture.session.document.elements[0].groupID)
        fixture.model.undo()
        XCTAssertTrue(fixture.session.document.elements.allSatisfy{$0.groupID==nil})
    }

    func testCommandLOpensTheLibraryWithoutHidingDrawing() throws {
        let suite="library-shortcut-\(UUID())"
        let defaults=try XCTUnwrap(UserDefaults(suiteName:suite))
        let model=WorkspaceModel(defaults:defaults)
        let panel=AnnotationPanel(contentRect:.zero,styleMask:[.borderless],backing:.buffered,defer:false)
        panel.imagePasteModel=model
        var opens=0
        model.onOpenToolLibrary={ _ in opens += 1 }
        defer {panel.orderOut(nil);model.closeAll();defaults.removePersistentDomain(forName:suite)}
        let event=try XCTUnwrap(NSEvent.keyEvent(with:.keyDown,location:.zero,modifierFlags:.command,timestamp:0,
            windowNumber:panel.windowNumber,context:nil,characters:"l",charactersIgnoringModifiers:"l",isARepeat:false,keyCode:37))
        XCTAssertTrue(panel.performKeyEquivalent(with:event))
        XCTAssertEqual(opens,1)
        XCTAssertFalse(model.workspaceHidden)
        model.setActivationShortcut(ActivationShortcut(keyCode:37,character:"l",command:true))
        XCTAssertNil(model.toolLibraryShortcut,"A user-assigned global shortcut must take priority.")
    }
}
