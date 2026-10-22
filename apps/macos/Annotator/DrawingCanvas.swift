import AppKit
import AnnotationCore
import PresentationRendering
import MotionCore

@MainActor
final class DrawingCanvas: NSView, NSTextViewDelegate {
    weak var model: WorkspaceModel?
    weak var session: OverlaySession?
    let renderer = SceneRenderer()
    private enum Gesture {
        case create(UUID, Point)
        case move(Point, [UUID: Element])
        case resize(UUID, Element, Point)
        case resizeSelection(Point, Point, [UUID: Element])
        case rotate(UUID, Point, Double, Double)
        case rotateSelection(Point, Double, [UUID: Element])
        case vertex(UUID, Int, Element)
        case marquee(Point)
        case erase
    }
    private var gesture: Gesture?
    private var marquee: Rect?
    private var eraseIDs: Set<UUID> = []
    private var textEditor: NSTextView?
    private var textElement: UUID?
    private var initialTextElement: Element?
    private(set) var editingShapeLabelID: UUID?
    private var initialShapeLabel: ShapeLabel?
    var editingShapeLabelStyle: Style? {
        guard let id = editingShapeLabelID else { return nil }
        guard let owner = session?.document.elements.first(where: { $0.id == id }) else { return nil }
        return owner.label?.resolvedStyle(for: owner.style)
    }
    private var textViewport: Viewport?
    var hasTextEditor: Bool { textEditor != nil }
    private var recognitionTask: Task<Void, Never>?
    private var previewTask: Task<Void, Never>?
    private var previewWorker = UUID()
    private var previewRevision: UInt64 = 0
    private var previewInput: Element?
    private let previewRenderer = SceneRenderer()
    private(set) var smartPreview: Element?
    private struct SnapMorph {
        let element: Element
        let ink: Element
        let from: [Point]
        let to: [Point]?
        let preview: Element?
        let generation: UInt64
        let selection: Set<UUID>
        let tool: Tool
        let dirty: CGRect
    }
    private struct EraseFade {
        let items: [(element: Element, renderer: SceneRenderer)]
        let generation: UInt64
        let scales: Bool
        let dirty: CGRect
    }
    static let snapMorphDuration: CFTimeInterval = MotionTiming.snapMorphDuration
    static let eraseFadeDuration: CFTimeInterval = MotionTiming.eraseFadeDuration
    /// The dashed candidate stays on screen from release until recognition finishes.
    private var releasePreview: Element?
    private var snapMorph: SnapMorph?
    private var snapMorphStart: CFTimeInterval?
    private(set) var snapMorphProgress = 0.0
    private var eraseFade: EraseFade?
    private var eraseFadeStart: CFTimeInterval?
    private(set) var eraseFadeProgress = 0.0
    private let morphTicker = MotionTicker()
    private let eraseTicker = MotionTicker()
    private let morphRenderer = SceneRenderer()
    private let morphShapeRenderer = SceneRenderer()
    var isSnapMorphing: Bool { snapMorph != nil }
    var isEraseFading: Bool { eraseFade != nil }
    var transitionTickersRunning: Bool { morphTicker.isRunning || eraseTicker.isRunning }
    var eraserPreviewIDs: Set<UUID> { eraseIDs }
    /// Shape points an arrow or line end will attach to if released now.
    private(set) var attachHints: [Anchor] = []
    private var linearID: UUID?
    private var linearPreview: Point?
    private var regionRequest: ((Rect) -> Void)?
    private var snapshotImage: CGImage?
    private var snapshotEffects: [Element] = []
    private var failedSnapshotGeneration: UInt64?
    var outputRegion: Rect?
    private var toolTracking: NSTrackingArea?
    private(set) var currentToolCursor = NSCursor.arrow
    private var lastAccessibilitySummary = ""
    private(set) var renderPassCount: UInt64 = 0
    private(set) var lastDrawnRect = CGRect.null
    private struct ElementDisplayKey: Equatable {
        let id: UUID
        let geometry: UUID
        let group: UUID?
        let locked: Bool
    }
    // Keep only small invalidation keys, not stroke samples or image buffers.
    private struct DisplayKey: Equatable {
        let document: UUID
        let generation: UInt64
        let elements: [ElementDisplayKey]
        let selection: Set<UUID>
        let mode: WorkspaceModel.Mode
        let board: WorkspaceModel.Board
        let bounds: CGRect
        let scale: CGFloat
        let appearance: NSAppearance.Name
        let outputRegion: Rect?
    }
    private var lastDisplayKey: DisplayKey?

    private func displayKey() -> DisplayKey? {
        guard let session, let model, session.sourceImage == nil,
              !session.document.isEditing, !hasActiveEdit, smartPreview == nil, textElement == nil, linearPreview == nil,
              eraseIDs.isEmpty, marquee == nil, attachHints.isEmpty, !model.spotlight, !model.spotlightFade.isVisible,
              !model.cursorHalo, releasePreview == nil, snapMorph == nil, eraseFade == nil,
              session.document.presentation.mode == .off,
              !session.document.elements.contains(where: { $0.tool == .image || $0.tool.isPixelEffect }) else { return nil }
        return DisplayKey(document: session.document.identity, generation: session.document.generation,
            elements: session.document.elements.map {
                ElementDisplayKey(id: $0.id, geometry: $0.geometryToken, group: $0.groupID, locked: $0.locked)
            }, selection: session.document.selection, mode: model.mode, board: session.board,
            bounds: bounds, scale: window?.backingScaleFactor ?? 1, appearance: effectiveAppearance.name,
            outputRegion: outputRegion)
    }

    var contentNeedsRedraw: Bool {
        guard let key = displayKey() else { return true }
        return lastDisplayKey != key
    }

    func invalidateContentIfNeeded() {
        if contentNeedsRedraw { needsDisplay = true }
    }

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        lastDisplayKey = nil
        needsDisplay = true
    }
    var constructingArrow: Bool { linearID != nil }
    var hasActiveEdit: Bool { gesture != nil || textEditor != nil || linearID != nil || regionRequest != nil }
    private lazy var interactButton: NSButton = {
        let button = HUDButton(title: "Use apps · Esc", target: self, action: #selector(releaseInput))
        button.frame = NSRect(x: 16, y: 14, width: 150, height: 28)
        button.bezelStyle = .rounded
        button.setAccessibilityLabel("Use apps")
        return button
    }()
    private lazy var interactFader: HUDFader = {
        let fader = HUDFader(interactButton)
        (interactButton as? HUDButton)?.fader = fader
        return fader
    }()
    private let shortcutHint = WorkspaceHintView(frame: CGRect(x:16,y:50,width:540,height:130))
    private lazy var hintFader = HUDFader(shortcutHint)
    /// Visibility the drawing HUD settles on; fades may still be running.
    var drawingHUDVisible: Bool { interactFader.targetVisible }
    var shortcutHintVisible: Bool { hintFader.targetVisible }
    var reducesMotion: () -> Bool = { Motion.systemReducesMotion } {
        didSet { interactFader.reducesMotion = reducesMotion; hintFader.reducesMotion = reducesMotion }
    }
    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        setAccessibilityRole(.group)
        setAccessibilityElement(true)
        setAccessibilityLabel("Annotation canvas")
        setAccessibilityCustomActions([
            NSAccessibilityCustomAction(name: "Focus drawing tools", handler: { [weak self] in
                MainActor.assumeIsolated { self?.model?.focusDrawingTools() ?? false }
            })
        ])
        if window == nil { finishTransitions() }
        if window != nil && interactButton.superview == nil { addSubview(interactButton) }
        if window != nil && shortcutHint.superview == nil { addSubview(shortcutHint) }
    }

    func setDrawingHUDVisible(_ visible: Bool) { interactFader.setVisible(visible); refreshHint() }
    private func refreshHint() {
        guard let model, let session else { hintFader.setVisible(false); return }
        hintFader.setVisible(!(model.preferences.showShortcutHints == false || model.workspaceHidden || model.active !== session ||
            session.sourceImage != nil || session.document.presentation.mode == .screen))
        shortcutHint.text = WorkspaceHint.text(model: model)
        shortcutHint.frame = CGRect(x:16,y:interactFader.targetVisible ? 50 : 14,
                                    width:min(540,max(80,bounds.width-32)),height:130)
    }
    override func layout() { super.layout(); refreshHint() }
    func refreshAccessibilitySummary() {
        guard let session, let model else { return }
        refreshHint()
        let count = session.document.elements.count
        let summary = "\(count) \(count == 1 ? "object" : "objects"), \(session.document.selection.count) selected. \(model.mode == .draw ? "Drawing" : "Using apps")."
        guard summary != lastAccessibilitySummary else { return }
        lastAccessibilitySummary = summary
        setAccessibilityValue(summary)
        setAccessibilityHelp("Select an object, then use arrow keys to move it. Hold Shift for ten-point steps. Escape releases drawing input.")
    }
    @objc private func releaseInput() { model?.interact() }

    func refreshToolCursor() {
        guard let model else { return }
        if model.mode != .draw || (!model.tool.isConnector && gesture == nil && linearID == nil) { setAttachHints([]) }
        let zoom = enclosingScrollView?.magnification ?? 1
        let screenZoom = session?.document.presentation.mode == .screen ? session?.document.presentation.zoom ?? 1 : 1
        currentToolCursor = model.mode == .draw
            ? ToolCursorFactory.cursor(tool: model.tool, width: model.style.width * zoom * screenZoom)
            : .arrow
        window?.invalidateCursorRects(for: self)
        if let window, !window.ignoresMouseEvents,
           model.pointerWindowNumber(at: NSEvent.mouseLocation) == window.windowNumber {
            let point = convert(window.convertPoint(fromScreen: NSEvent.mouseLocation), from: nil)
            hoverCursor(at: Point(point)).set()
        }

    }

    private var rotationHitRadius: Double {
        let zoom = (enclosingScrollView?.magnification ?? 1)
            * (session?.document.presentation.mode == .screen ? session?.document.presentation.zoom ?? 1 : 1)
        return 18 / max(1, zoom)
    }

    func isOverRotationHandle(_ point: Point) -> Bool {
        if let model, model.mode == .draw, model.tool == .select, let session,
           let group = SelectionGeometry(elements: session.document.elements, selection: session.document.selection) {
            return group.canRotate && point.distance(to: Point(group.bounds.center.x, group.bounds.y - 24)) <= rotationHitRadius
        }
        guard model?.mode == .draw, model?.tool == .select, let session, session.document.selection.count == 1,
              let element = session.document.elements.first(where: { session.document.selection.contains($0.id) }),
              !element.locked, !element.tool.isProtection, !element.tool.isConnector else { return false }
        let local = point.rotated(around: element.frame.center, by: -element.rotation)
        return local.distance(to: Point(element.frame.center.x, element.frame.y - 24)) <= rotationHitRadius
    }

    func hoverCursor(at point: Point) -> NSCursor {
        guard model?.mode == .draw else { return .arrow }
        if case .rotate = gesture { return ToolCursorFactory.rotationCursor }
        if case .rotateSelection = gesture { return ToolCursorFactory.rotationCursor }
        if let hit = hitTest(convert(point.cgPoint, to: superview)), hit !== self {
            if let textEditor, hit === textEditor || hit.isDescendant(of: textEditor) { return .iBeam }
            return .arrow
        }
        if isOverRotationHandle(point) { return ToolCursorFactory.rotationCursor }
        return currentToolCursor
    }

    private func updateHoverCursor(_ event: NSEvent) {
        hoverCursor(at: Point(convert(event.locationInWindow, from: nil))).set()
    }

    override func resetCursorRects() { addCursorRect(bounds, cursor: currentToolCursor) }
    override func updateTrackingAreas() {
        if let toolTracking { removeTrackingArea(toolTracking) }
        let tracking = NSTrackingArea(rect: .zero, options: [.inVisibleRect, .activeAlways, .cursorUpdate, .mouseEnteredAndExited, .mouseMoved],
                                      owner: self)
        addTrackingArea(tracking); toolTracking = tracking
        super.updateTrackingAreas()
    }
    override func cursorUpdate(with event: NSEvent) { updateHoverCursor(event) }
    override func mouseEntered(with event: NSEvent) { updateHoverCursor(event) }
    override func mouseExited(with event: NSEvent) {
        if gesture == nil && linearID == nil { setAttachHints([]) }
        if let model { model.refreshPointerCursor() } else { NSCursor.arrow.set() }
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        guard let session, let model else { return nil }
        guard model.activateDrawingSession(session) else { return nil }
        let menu = NSMenu()
        menu.autoenablesItems = false
        func item(_ title: String, _ action: Selector, enabled: Bool = true) {
            let entry = NSMenuItem(title: title, action: action, keyEquivalent: "")
            entry.target = self; entry.isEnabled = enabled; menu.addItem(entry)
        }
        if hasActiveEdit {
            if constructingArrow { item("Finish arrow", #selector(finishPathFromMenu)) }
            item("Cancel edit", #selector(cancelEditFromMenu))
            return menu
        }
        let point = Point(convert(event.locationInWindow, from: nil))
        let hit = ElementStacking.frontmost(at: point, in: session.document.elements)
        if hit.map({ !session.document.selection.contains($0.id) }) ?? true {
            model.useLocalSelection(in: session)
            session.document.select(at: point)
        }
        model.synchronize()
        let selected = session.document.elements.filter { session.document.selection.contains($0.id) }
        let editable = !selected.isEmpty && !selected.contains(where: \.locked)
        if !selected.isEmpty {
            item("Properties", #selector(propertiesFromMenu))
            item("Copy", #selector(copyFromMenu))
            item("Cut", #selector(cutFromMenu), enabled: editable)
            item("Duplicate", #selector(duplicateFromMenu), enabled: editable)
            item("Group", #selector(groupFromMenu), enabled: editable && selected.count > 1)
            item("Ungroup", #selector(ungroupFromMenu), enabled: editable && selected.contains { $0.groupID != nil })
            menu.addItem(.separator())
            item("Bring to front", #selector(frontFromMenu), enabled: editable)
            item("Send to back", #selector(backFromMenu), enabled: editable)
            item("Lock", #selector(lockFromMenu), enabled: selected.contains { !$0.locked })
            item("Unlock", #selector(unlockFromMenu), enabled: selected.contains(where: \.locked))
            menu.addItem(.separator())
            item("Save to My shapes...", #selector(saveShapeFromMenu),
                 enabled: selected.allSatisfy { !$0.tool.isProtection && $0.tool != .image })
            item(selected.contains { $0.tool.isProtection } ? "Delete selected censor region" : "Delete selected",
                 #selector(deleteSelectionFromMenu), enabled: !model.selectionIsLocked)
            menu.addItem(.separator())
        }
        item("Paste", #selector(pasteFromMenu))
        item("Undo", #selector(undoFromMenu), enabled: model.canUndo)
        item("Redo", #selector(redoFromMenu), enabled: model.canRedo)
        item("More shapes and tools", #selector(libraryFromMenu))
        return menu
    }
    @objc private func deleteSelectionFromMenu() { model?.deleteSelected() }
    @objc private func duplicateFromMenu() { model?.perform { try $0.duplicateSelection() } }
    @objc private func copyFromMenu() { model?.copySelection() }
    @objc private func cutFromMenu() { model?.copySelection(cut: true) }
    @objc private func pasteFromMenu() { model?.paste() }
    @objc private func groupFromMenu() { model?.perform { try $0.groupSelection() } }
    @objc private func ungroupFromMenu() { model?.perform { try $0.ungroupSelection() } }
    @objc private func frontFromMenu() { model?.perform { try $0.arrangeSelection(toFront: true) } }
    @objc private func backFromMenu() { model?.perform { try $0.arrangeSelection(toFront: false) } }
    @objc private func lockFromMenu() { model?.perform { try $0.setSelectionLocked(true) } }
    @objc private func unlockFromMenu() { model?.perform { try $0.setSelectionLocked(false) } }
    @objc private func undoFromMenu() { model?.undo() }
    @objc private func redoFromMenu() { model?.redo() }
    @objc private func libraryFromMenu() { model?.openToolLibrary() }
    @objc private func saveShapeFromMenu() { model?.openToolLibrary(.myShapes) }
    @objc private func propertiesFromMenu() { model?.setInspectorPinned(true); model?.setQuickPreferencesExpanded(true); model?.onShowToolbar?() }
    @objc private func finishPathFromMenu() { finishLinearPath() }
    @objc private func cancelEditFromMenu() { cancelGesture(); model?.synchronize() }

    override func draw(_ dirtyRect: NSRect) {
        guard let context = NSGraphicsContext.current?.cgContext, let session, let model else { return }
        defer { lastDisplayKey = displayKey() }
        renderPassCount &+= 1
        lastDrawnRect = context.boundingBoxOfClipPath.intersection(bounds)
        context.clear(bounds)
        let morph = snapMorph.flatMap { snapMorphValid($0) ? $0 : nil }
        var pixelEffectsReady = session.effectsReady
        var snapshotContainsDrawing = false
        if let source = session.sourceImage {
            let effects = session.document.elements
            if !session.document.isEditing && (snapshotImage == nil || snapshotEffects != effects) {
                do {
                    snapshotImage = try SnapshotExporter.image(source: source, elements: effects)
                    snapshotEffects = effects
                    failedSnapshotGeneration = nil
                } catch {
                    if failedSnapshotGeneration != session.document.generation {
                        failedSnapshotGeneration = session.document.generation
                        model.report(error)
                    }
                }
            }
            context.saveGState()
            context.translateBy(x: 0, y: bounds.height)
            context.scaleBy(x: 1, y: -1)
            snapshotContainsDrawing = !session.document.isEditing && snapshotImage != nil && snapshotEffects == effects
                && morph == nil
            context.draw(snapshotContainsDrawing ? snapshotImage ?? source : source, in: bounds)
            context.restoreGState()
            pixelEffectsReady = !session.document.isEditing && snapshotImage != nil && snapshotEffects == effects
        }
        if session.board != .transparent {
            context.setFillColor((session.board == .light ? NSColor.white : NSColor(calibratedWhite: 0.08, alpha: 1)).cgColor)
            context.fill(bounds)
        }
        context.saveGState()
        if let viewport = session.presentation?.screenViewport {
            context.scaleBy(x: viewport.output.width / viewport.source.width, y: viewport.output.height / viewport.source.height)
            context.translateBy(x: -viewport.source.x, y: -viewport.source.y)
            pixelEffectsReady = false
        }
        let displayElements = visibleElements(includingTextEditor: false)
        if snapshotContainsDrawing {
            renderer.selection(displayElements, ids: model.mode == .draw ? session.document.selection : [], in: context)
        } else {
            var faded = eraseIDs
            if model.smartDraw, let smartPreview { faded.insert(smartPreview.id) }
            if model.smartDraw, let releasePreview { faded.insert(releasePreview.id) }
            let sceneElements = morph.map { morph in displayElements.filter { $0.id != morph.element.id } } ?? displayElements
            renderer.draw(sceneElements, selection: model.mode == .draw ? session.document.selection : [], in: context,
                          pixelsAvailableForBlur: pixelEffectsReady, fadedIDs: faded,
                          hiddenLabelIDs: Set([editingShapeLabelID].compactMap { $0 }))
        }

        if model.smartDraw, var candidate = smartPreview ?? releasePreview {
            candidate.style.pattern = .dashed
            candidate.style.opacity = 0.75
            previewRenderer.draw([candidate], in: context)
        }
        if let morph { drawSnapMorph(morph, in: context) }
        if let eraseFade, session.document.generation == eraseFade.generation { drawEraseFade(eraseFade, in: context) }
        if let outputRegion {
            let path = CGMutablePath()
            path.addRect(bounds); path.addRect(outputRegion.cgRect)
            context.addPath(path); context.setFillColor(NSColor.black.withAlphaComponent(0.4).cgColor)
            context.drawPath(using: .eoFill)
        }
        if let marquee {
            context.setStrokeColor(NSColor.controlAccentColor.cgColor)
            context.setLineWidth(1)
            context.setLineDash(phase: 0, lengths: [4, 3])
            context.stroke(marquee.cgRect)
        }
        drawAttachHints(in: context, elements: displayElements)
        for element in displayElements where eraseIDs.contains(element.id) {
            context.saveGState()
            context.translateBy(x: element.frame.center.x, y: element.frame.center.y)
            context.rotate(by: element.rotation)
            context.translateBy(x: -element.frame.center.x, y: -element.frame.center.y)
            context.setStrokeColor(NSColor.controlAccentColor.withAlphaComponent(0.75).cgColor)
            context.setLineWidth(1)
            context.setLineDash(phase: 0, lengths: [3, 3])
            context.stroke(element.frame.cgRect.insetBy(dx: -4, dy: -4))
            context.restoreGState()
        }
        context.restoreGState()
        guard let window else { return }
        let pointer = convert(window.convertPoint(fromScreen: NSEvent.mouseLocation), from: nil)
        if model.spotlightFade.isVisible && session.presentation?.usesPointerOverlay != true {
            do {
                try SpotlightRenderer.draw(in: context, bounds: bounds, center: pointer,
                    radius: model.spotlightFade.radius(for: model.spotlightRadius), opacity: model.spotlightFade.intensity)
            } catch { model.report(error) }
        }
        if model.cursorHighlightVisible && session.presentation?.usesPointerOverlay != true {
            CursorHighlightRenderer.draw(model.cursorHighlightSettings, center: pointer,
                clickProgress: model.pointerClickAnimation.progress,
                clickCenter: model.cursorClickLocation.map { convert(window.convertPoint(fromScreen: $0), from: nil) },
                reduceMotion: NSWorkspace.shared.accessibilityDisplayShouldReduceMotion, in: context)
        }
    }

    var elementsForSharing: [Element] { visibleElements(includingTextEditor: true) }

    private func visibleElements(includingTextEditor: Bool) -> [Element] {
        guard let session else { return [] }
        let draft = constructionPreview
        return session.document.elements.compactMap {
            var element = $0
            if let draft, element.id == draft.id { element = draft }
            if element.id == textElement && editingShapeLabelID == nil {
                guard includingTextEditor, let editor = textEditor else { return nil }
                element.text = editor.string
                element.frame = sourceFrame(for: editor)
            }
            if element.id == editingShapeLabelID, let editor = textEditor, let style = element.label?.style {
                element.label = ShapeLabel(text: editor.string, style: style, followsStroke: element.label?.followsStroke)
            }
            return element
        }
    }

    override func mouseDown(with event: NSEvent) {
        guard let model, let session, model.mode == .draw else { return }
        guard model.activateDrawingSession(session) else { return }
        finishTransitions()
        model.finishStyleEdit()
        model.canvasInteraction.send()
        model.useLocalSelection(in: session)
        commitTextIfNeeded()
        window?.makeFirstResponder(self)
        let point = Point(convert(event.locationInWindow, from: nil))
        if regionRequest != nil {
            gesture = .marquee(point)
            marquee = Rect(from: point, to: point)
            needsDisplay = true
            return
        }
        do {
            recognitionTask?.cancel()
            let hitElement = ElementStacking.frontmost(at: point, in: session.document.elements)
            if model.tool == .counter {
                let maximum = session.document.elements.filter { $0.tool == .counter }.compactMap { Int($0.text) }.max() ?? 0
                let (next, overflow) = maximum.addingReportingOverflow(1)
                guard !overflow else { throw CanvasError.invalidDocument("The counter value is too large.") }
                try session.document.add(Element(tool: .counter, frame: Rect(x: point.x - 18, y: point.y - 18, width: 36, height: 36),
                                                 text: String(next), style: model.style))
                model.synchronize()
                return
            }
            if let linearID {
                if event.clickCount >= 2 { finishLinearPath(); return }
                try session.document.update(linearID) {
                    if $0.points.last != point { $0.points.append(point) }
                    $0.frame = Rect.enclosing($0.points)
                }
                needsDisplay = true
                return
            }
            if event.clickCount >= 2 && model.tool == .select,
               let connector = hitElement, connector.tool == .arrow, !connector.locked,
               session.document.selection.contains(connector.id) {
                if let index = connector.points.indices.dropFirst().dropLast().first(where: { connector.points[$0].distance(to: point) <= 8 }) {
                    try session.document.update(connector.id) { $0.points.remove(at: index); $0.frame = Rect.enclosing($0.points) }
                } else if connector.points.count >= 2,
                          let index = (0..<(connector.points.count - 1)).min(by: {
                              HitTesting.distance(point, segment: connector.points[$0], connector.points[$0 + 1])
                                  < HitTesting.distance(point, segment: connector.points[$1], connector.points[$1 + 1])
                          }) {
                    try session.document.update(connector.id) { $0.points.insert(point, at: index + 1); $0.frame = Rect.enclosing($0.points) }
                }
                model.synchronize()
                return
            }
            let editsText = event.clickCount == 2 && model.tool == .select
                && (hitElement == nil || hitElement?.tool == .text || hitElement?.tool.isShape == true)
            if model.tool == .text || editsText {
                startText(at: point)
                return
            }
            if model.tool == .select {
                let group = SelectionGeometry(elements: session.document.elements, selection: session.document.selection)
                if let group {
                    let originals = Dictionary(uniqueKeysWithValues: session.document.elements
                        .filter { session.document.selection.contains($0.id) && !$0.locked }.map { ($0.id, $0) })
                    if let index = group.corners.firstIndex(where: { $0.distance(to: point) <= 9 }) {
                        session.document.begin()
                        gesture = .resizeSelection(group.corners[(index + 2) % 4], group.corners[index], originals)
                        return
                    }
                    if isOverRotationHandle(point) {
                        session.document.begin()
                        let center = group.bounds.center
                        gesture = .rotateSelection(center, atan2(point.y - center.y, point.x - center.x), originals)
                        ToolCursorFactory.rotationCursor.set()
                        return
                    }
                }
                if let selected = session.document.elements.first(where: {
                    session.document.selection.count == 1 && session.document.selection.contains($0.id) && !$0.locked
                }) {
                    if selected.tool.isConnector,
                       let index = selected.points.firstIndex(where: { $0.distance(to: point) <= 8 }) {
                        session.document.begin(); gesture = .vertex(selected.id, index, selected); return
                    }
                    let unrotated = point.rotated(around: selected.frame.center, by: -selected.rotation)
                    let box = selected.frame
                    let corners = [box.origin, Point(box.maxX, box.y), Point(box.maxX, box.maxY), Point(box.x, box.maxY)]
                    if let index = corners.firstIndex(where: { unrotated.distance(to: $0) <= 9 }) {
                        session.document.begin()
                        gesture = .resize(selected.id, selected, corners[(index + 2) % 4])
                        return
                    }
                    let rotation = Point(selected.frame.center.x, selected.frame.y - 24)
                    if !selected.tool.isProtection && !selected.tool.isConnector && unrotated.distance(to: rotation) <= rotationHitRadius {
                        session.document.begin()
                        let center = selected.frame.center
                        gesture = .rotate(selected.id, center, atan2(point.y - center.y, point.x - center.x), selected.rotation)
                        ToolCursorFactory.rotationCursor.set()
                        return
                    }
                }
                let preservesSelection = !event.modifierFlags.contains(.shift) &&
                    (hitElement.map { session.document.selection.contains($0.id) } ??
                        (group?.bounds.contains(point) == true))
                if !preservesSelection {
                    session.document.select(at: point, extending: event.modifierFlags.contains(.shift))
                }
                session.document.begin()
                if session.document.selection.isEmpty {
                    gesture = .marquee(point)
                } else {
                    let originals = Dictionary(uniqueKeysWithValues: session.document.elements
                        .filter { session.document.selection.contains($0.id) }.map { ($0.id, $0) })
                    gesture = .move(point, originals)
                }
            } else if model.tool == .eraser {
                session.document.begin(); gesture = .erase; collectErase(at: point)
            } else {
                session.document.begin()
                var element = Element(tool: model.tool, frame: Rect(from: point, to: point), style: model.style)
                if model.tool.isConnector || model.tool == .ruler { element.points = [point, point] }
                try session.document.add(element)
                if model.tool.isFreehand {
                    try session.document.appendSample(to: element.id, point: point,
                                                       pressure: event.pressure > 0 ? Double(event.pressure) : 1)
                }
                gesture = .create(element.id, point)
            }
            model.synchronize()
        } catch { fail(error) }
    }

    func incrementalInkRect(for element: Element, to point: Point) -> CGRect? {
        guard let session, let model, session.sourceImage == nil, element.tool.isFreehand,
              element.rotation == 0, session.document.selection.isEmpty,
              element.style.roughness == 0 || (element.tool == .pen && element.style.pressure),
              !model.smartDraw, smartPreview == nil, !model.spotlight, !model.spotlightFade.isVisible, !model.cursorHalo,
              session.document.presentation.mode == .off,
              !session.document.elements.contains(where: { $0.tool.isPixelEffect }),
              let previous = element.points.last else { return nil }
        let padding = element.style.width * (element.tool == .highlighter ? 3 : 1)
            + element.style.roughness + 3
        let rect = Rect(from: previous, to: point).cgRect.insetBy(dx: -padding, dy: -padding)
            .integral.intersection(bounds)
        return rect.isNull ? nil : rect
    }

    override func mouseDragged(with event: NSEvent) {
        guard let session, let gesture else { return }
        let point = Point(convert(event.locationInWindow, from: nil))
        var inkRect: CGRect?
        do {
            switch gesture {
            case .create(let id, let start):
                guard let element = session.document.elements.first(where: { $0.id == id }) else { return }
                if element.tool == .highlighter && event.modifierFlags.contains(.shift) {
                    try updateStraightHighlight(in: session, id: id, start: start, point: point)
                } else if element.tool.isFreehand {
                    inkRect = incrementalInkRect(for: element, to: point)
                    try session.document.appendSample(to: id, point: point,
                                                       pressure: event.pressure > 0 ? Double(event.pressure) : 1)
                    requestSmartPreview(id: id)
                } else {
                    var finish = point
                    if event.modifierFlags.contains(.shift) {
                        if element.tool.isConnector || element.tool == .ruler {
                            finish = DragConstraint.snappedLineEnd(from: start, to: point)
                        } else {
                            finish = DragConstraint.squareEnd(from: start, to: point)
                        }
                    }
                    if let ratio = element.tool.systemShapeAspectRatio {
                        finish = DragConstraint.aspectEnd(from: start, to: finish, ratio: ratio)
                    }
                    try session.document.update(id) {
                        $0.frame = Rect(from: start, to: finish)
                        if $0.tool.isConnector || $0.tool == .ruler { $0.points = [start, finish] }
                        if $0.tool == .ruler { $0.text = "\(Int(start.distance(to: finish) * session.scale)) px" }
                    }
                }
            case .move(let origin, let originals):
                try session.document.editSelection { element in
                    guard let original = originals[element.id] else { return }
                    element = original
                    element.translate(by: point - origin)
                }
                try model?.previewDisplayDrag(from: session,
                    ids: Set(originals.values.filter { !$0.locked }.map(\.id)))
            case .resize(let id, let original, let anchor):
                var localPoint = point.rotated(around: original.frame.center, by: -original.rotation)
                if event.modifierFlags.contains(.shift) || original.tool.systemShapeAspectRatio != nil,
                   original.frame.width > 0, original.frame.height > 0 {
                    let delta = localPoint - anchor
                    let scale = max(abs(delta.x) / original.frame.width, abs(delta.y) / original.frame.height)
                    let xSign = delta.x == 0 ? original.frame.center.x - anchor.x : delta.x
                    let ySign = delta.y == 0 ? original.frame.center.y - anchor.y : delta.y
                    localPoint = anchor + Point((xSign < 0 ? -1 : 1) * original.frame.width * scale,
                                                (ySign < 0 ? -1 : 1) * original.frame.height * scale)
                }
                let newFrame = Rect(from: anchor, to: localPoint)
                let fixedWorldPoint = anchor.rotated(around: original.frame.center, by: original.rotation)
                let movedWorldPoint = anchor.rotated(around: newFrame.center, by: original.rotation)
                try session.document.update(id) {
                    $0 = original
                    $0.resize(to: newFrame)
                    $0.translate(by: fixedWorldPoint - movedWorldPoint)
                    if $0.tool == .ruler, let first = $0.points.first, let last = $0.points.last {
                        $0.text = "\(Int(first.distance(to: last) * session.scale)) px"
                    }
                }
            case .rotate(let id, let center, let initialAngle, let initialRotation):
                let rotation = initialRotation + atan2(point.y - center.y, point.x - center.x) - initialAngle
                try session.document.update(id) { $0.rotation = rotation }
                ToolCursorFactory.rotationCursor.set()
            case .resizeSelection(let anchor, let corner, let originals):
                let diagonal = corner - anchor
                let squared = diagonal.x * diagonal.x + diagonal.y * diagonal.y
                guard squared > 0 else { return }
                let delta = point - anchor
                let scale = max(0.01, (delta.x * diagonal.x + delta.y * diagonal.y) / squared)
                try session.document.editSelection { element in
                    guard let original = originals[element.id] else { return }
                    element = original
                    let origin = anchor + (original.frame.origin - anchor) * scale
                    element.resize(to: Rect(x: origin.x, y: origin.y,
                        width: original.frame.width * scale, height: original.frame.height * scale))
                    if element.tool == .text {
                        element.style.fontSize = min(1024, max(4, original.style.fontSize * scale))
                    }
                    if element.tool == .ruler, let first = element.points.first, let last = element.points.last {
                        element.text = "\(Int(first.distance(to: last) * session.scale)) px"
                    }
                }
            case .rotateSelection(let center, let initialAngle, let originals):
                let angle = atan2(point.y - center.y, point.x - center.x) - initialAngle
                try session.document.editSelection { element in
                    guard let original = originals[element.id] else { return }
                    element = original
                    if original.tool.isConnector {
                        element.points = original.points.map {
                            $0.rotated(around: original.frame.center, by: original.rotation)
                                .rotated(around: center, by: angle)
                        }
                        element.rotation = 0
                        element.frame = Rect.enclosing(element.points)
                    } else {
                        element.translate(by: original.frame.center.rotated(around: center, by: angle) - original.frame.center)
                        element.rotation = original.rotation + angle
                    }
                }
                ToolCursorFactory.rotationCursor.set()
            case .vertex(let id, let index, let original):
                if event.modifierFlags.contains(.shift), original.points.count >= 2,
                   index == 0 || index == original.points.count - 1 {
                    let anchor = original.points[index == 0 ? original.points.count - 1 : 0]
                    let direction = original.points[index] - anchor
                    let lengthSquared = direction.x * direction.x + direction.y * direction.y
                    if lengthSquared > 0 {
                        let local = point.rotated(around: original.frame.center, by: -original.rotation) - anchor
                        let scale = (local.x * direction.x + local.y * direction.y) / lengthSquared
                        let points = original.points.map { anchor + ($0 - anchor) * scale }
                        let frame = Rect.enclosing(points)
                        let translation = anchor.rotated(around: original.frame.center, by: original.rotation)
                            - anchor.rotated(around: frame.center, by: original.rotation)
                        try session.document.update(id) {
                            $0 = original
                            $0.points = points; $0.frame = frame
                            $0.translate(by: translation)
                            if index == 0 { $0.startAnchor = nil } else { $0.endAnchor = nil }
                        }
                        break
                    }
                }
                try session.document.update(id) {
                    $0.points[index] = point; $0.frame = Rect.enclosing($0.points)
                    if index == 0 { $0.startAnchor = nil }
                    if index == $0.points.count - 1 { $0.endAnchor = nil }
                }
            case .marquee(let origin): marquee = Rect(from: origin, to: point)
            case .erase: collectErase(at: point)
            }
            refreshAttachHints(hovering: point)
            if let inkRect { setNeedsDisplay(inkRect) } else { needsDisplay = true }
            if session.sourceImage == nil { session.presentation?.refresh() }
        } catch { fail(error) }
    }

    override func mouseUp(with event: NSEvent) {
        guard let session, let model, let gesture else { return }
        let heldPreview = model.smartDraw ? smartPreview : nil
        var erased: [Element] = []
        cancelSmartPreview()
        if let request = regionRequest {
            let selected = marquee ?? .zero
            regionRequest = nil; self.gesture = nil; marquee = nil
            needsDisplay = true
            request(selected)
            return
        }
        do {
            switch gesture {
            case .move(_, let originals):
                mouseDragged(with: event)
                guard self.gesture != nil else { return }
                let screenPoint = session.panel.convertPoint(toScreen: event.locationInWindow)
                if try model.finishDisplayDrag(from: session, at: screenPoint,
                    ids: Set(originals.values.filter { !$0.locked }.map(\.id))) {
                    self.gesture = nil
                    model.synchronize()
                    return
                }
            case .create(let id, let startPoint):
                if let element = session.document.elements.first(where: { $0.id == id }) {
                    let finalPoint = Point(convert(event.locationInWindow, from: nil))
                    if element.tool == .arrow && startPoint.distance(to: finalPoint) < 4 {
                        try session.document.update(id) { $0.points = [startPoint]; $0.frame = Rect(from: startPoint, to: startPoint) }
                        linearID = id
                        linearPreview = finalPoint
                        self.gesture = nil
                        model.status = "Click to add arrow points. Return or double-click finishes; Escape cancels."
                        needsDisplay = true
                        return
                    }
                    if element.tool == .highlighter && event.modifierFlags.contains(.shift) {
                        try updateStraightHighlight(in: session, id: id, start: startPoint, point: finalPoint)
                    } else if element.tool.isFreehand && element.points.last != finalPoint {
                        try session.document.appendSample(to: id, point: finalPoint,
                                                           pressure: event.pressure > 0 ? Double(event.pressure) : 1)
                    }
                    if element.tool.isConnector, let first = element.points.first, let last = element.points.last {
                        let start = session.document.nearestAnchor(to: first, excluding: id)
                        let end = session.document.nearestAnchor(to: last, excluding: id)
                        try session.document.update(id) { $0.startAnchor = start; $0.endAnchor = end }
                    }
                    if !element.tool.isFreehand { session.document.select([id]); model.tool = .select }
                }
            case .marquee:
                if let marquee { session.document.select(Set(session.document.elements.filter { marquee.contains($0.frame) }.map(\.id))) }
            case .erase:
                erased = session.document.elements.filter { eraseIDs.contains($0.id) && !$0.tool.isProtection }
                session.document.select(eraseIDs)
                try session.document.deleteSelection(includingProtection: true)
            case .vertex(let id, let index, let original):
                if let element = session.document.elements.first(where: { $0.id == id }), element.tool.isConnector,
                   index == 0 || index == element.points.count - 1,
                   original.points.indices.contains(index), element.points[index] != original.points[index] {
                    let anchor = session.document.nearestAnchor(to: element.points[index], excluding: id)
                    if anchor != (index == 0 ? element.startAnchor : element.endAnchor) {
                        try session.document.update(id) { if index == 0 { $0.startAnchor = anchor } else { $0.endAnchor = anchor } }
                    }
                }
            default: break
            }
            setAttachHints([])
            try session.document.commit()
            if case .create(let id, _) = gesture { scheduleRecognition(id: id, preview: heldPreview) }
            self.gesture = nil; marquee = nil; eraseIDs.removeAll()
            model.synchronize()
            beginEraseFade(erased)
        } catch { fail(error) }
    }

    private func updateStraightHighlight(in session: OverlaySession, id: UUID, start: Point, point: Point) throws {
        let finish = DragConstraint.snappedLineEnd(from: start, to: point)
        try session.document.update(id) {
            $0.points = finish == start ? [start] : [start, finish]
            $0.pressures = []
            $0.frame = Rect(from: start, to: finish)
        }
    }

    /// Mirrors the attach rules used on release so the highlight never promises a link that will not happen.
    func refreshAttachHints(hovering point: Point) {
        guard let session, let model, model.mode == .draw else { setAttachHints([]); return }
        let document = session.document
        func anchor(_ location: Point, excluding id: UUID?) -> Anchor? {
            document.nearestAnchor(to: location, excluding: id ?? UUID())
        }
        var hints: [Anchor?] = []
        switch gesture {
        case .create(let id, let start):
            if let element = document.elements.first(where: { $0.id == id }), element.tool.isConnector {
                hints = [anchor(start, excluding: id), anchor(element.points.last ?? point, excluding: id)]
            }
        case .vertex(let id, let index, let original):
            if original.tool.isConnector, index == 0 || index == original.points.count - 1,
               let element = document.elements.first(where: { $0.id == id }), element.points.indices.contains(index) {
                hints = [anchor(element.points[index], excluding: id)]
            }
        case nil:
            if let linearID, let element = document.elements.first(where: { $0.id == linearID }) {
                hints = [element.points.first.flatMap { anchor($0, excluding: linearID) }, anchor(point, excluding: linearID)]
            } else if model.tool.isConnector {
                hints = [anchor(point, excluding: nil)]
            }
        default: break
        }
        setAttachHints(hints.compactMap { $0 })
    }

    private func setAttachHints(_ hints: [Anchor]) {
        guard hints != attachHints else { return }
        attachHints = hints
        needsDisplay = true
    }

    private func drawAttachHints(in context: CGContext, elements: [Element]) {
        guard !attachHints.isEmpty else { return }
        let accent = NSColor.controlAccentColor
        for id in Set(attachHints.map(\.elementID)) {
            guard let shape = elements.first(where: { $0.id == id }) else { continue }
            let halo = CGPath(roundedRect: shape.symbolFrame.cgRect.insetBy(dx: -6, dy: -6), cornerWidth: 6, cornerHeight: 6,
                              transform: nil)
            context.saveGState()
            context.translateBy(x: shape.frame.center.x, y: shape.frame.center.y)
            context.rotate(by: shape.rotation)
            context.translateBy(x: -shape.frame.center.x, y: -shape.frame.center.y)
            context.addPath(halo)
            context.setFillColor(accent.withAlphaComponent(0.08).cgColor)
            context.fillPath()
            context.addPath(halo)
            context.setStrokeColor(accent.withAlphaComponent(0.9).cgColor)
            context.setLineWidth(2)
            context.strokePath()
            context.restoreGState()
            context.setLineWidth(1.5)
            for location in shape.anchorLocations {
                let point = shape.anchorPoint(location)
                let chosen = attachHints.contains(Anchor(elementID: id, relative: location))
                let radius: CGFloat = chosen ? 5.5 : 3.5
                let dot = CGRect(x: point.x - radius, y: point.y - radius, width: radius * 2, height: radius * 2)
                context.setFillColor(chosen ? accent.cgColor : NSColor.white.withAlphaComponent(0.9).cgColor)
                context.fillEllipse(in: dot)
                context.setStrokeColor(chosen ? NSColor.white.cgColor : accent.cgColor)
                context.strokeEllipse(in: dot)
            }
        }
    }

    func cancelGesture() {
        if case .move = gesture { model?.clearDisplayDragPreviews() }
        let changed = hasActiveEdit || marquee != nil || !eraseIDs.isEmpty || smartPreview != nil ||
            session?.document.isEditing == true
        cancelSmartPreview()
        recognitionTask?.cancel()
        recognitionTask = nil
        finishTransitions()
        session?.document.cancel()
        gesture = nil; marquee = nil; eraseIDs.removeAll()
        if let textEditor { textEditor.removeFromSuperview() }
        textEditor = nil; textElement = nil
        initialTextElement = nil
        editingShapeLabelID = nil; initialShapeLabel = nil
        textViewport = nil; session?.presentation?.releaseTextViewport()
        linearID = nil; linearPreview = nil
        regionRequest = nil
        setAttachHints([])
        if changed { needsDisplay = true }
    }

    func selectRegion(_ completion: @escaping (Rect) -> Void) {
        commitTextIfNeeded()
        cancelGesture()
        regionRequest = completion
        model?.status = "Drag a region. Escape cancels."
    }

    private func collectErase(at point: Point) {
        guard let session else { return }
        if let front = ElementStacking.frontmost(at: point, in: session.document.elements), front.tool.isProtection {
            if !front.locked { eraseIDs.insert(front.id) }
            return
        }
        eraseIDs.formUnion(session.document.elements.filter {
            !$0.tool.isProtection && !$0.locked && HitTesting.contains(point, element: $0, tolerance: model?.style.width ?? 12)
        }.map(\.id))
    }

    private func cancelSmartPreview() {
        previewWorker = UUID()
        previewRevision &+= 1
        previewTask?.cancel(); previewTask = nil
        previewInput = nil; smartPreview = nil
        previewRenderer.reset()
    }

    private func requestSmartPreview(id: UUID) {
        guard model?.smartDraw == true, let session,
              let stroke = session.document.elements.first(where: { $0.id == id }), stroke.tool == .pen else { return }
        previewRevision &+= 1
        previewInput = ShapeRecognition.previewInput(stroke)
        guard previewTask == nil else { return }
        let worker = UUID()
        previewWorker = worker
        previewTask = Task { [weak self] in
            guard let self else { return }
            defer { if previewWorker == worker { previewTask = nil } }
            while let input = previewInput, !Task.isCancelled, previewWorker == worker {
                previewInput = nil
                let revision = previewRevision
                let candidate = await Task.detached(priority: .userInitiated) { ShapeRecognition.recognize(input) }.value
                guard !Task.isCancelled, previewWorker == worker else { return }
                guard model?.smartDraw == true else { smartPreview = nil; needsDisplay = true; return }
                guard let gesture, case .create(let activeID, _) = gesture, activeID == input.id else { return }
                if revision == previewRevision {
                    smartPreview = candidate
                    needsDisplay = true
                }
            }
        }
    }

    @objc func paste(_ sender: Any?) {
        model?.paste()
    }

    @objc func copy(_ sender: Any?) {
        model?.copySelection()
    }

    @objc func cut(_ sender: Any?) {
        model?.copySelection(cut: true)
    }

    override func keyDown(with event: NSEvent) {
        guard let model, let session else { return }
        if model.mode == .draw, !model.activateDrawingSession(session) { return }
        if event.keyCode == 53 {
            if hasTextEditor { commitTextIfNeeded() }
            else if hasActiveEdit { cancelGesture(); model.synchronize() }
            else if model.screenZoomEnabled || model.magnifierEnabled { model.handleZoomEscape() }
            else { model.interact() }
            return
        }
        let key = event.charactersIgnoringModifiers?.lowercased() ?? ""
        let modifiers = event.modifierFlags.intersection([.control, .command, .option, .shift])
        if modifiers == .command, key == "l", model.toolLibraryShortcut != nil {
            model.openToolLibrary()
            return
        }
        if !hasActiveEdit, model.selectionCount > 0, modifiers.isSubset(of: [.shift]),
           let direction = Self.nudgeDirection(event.keyCode) {
            guard !model.selectionIsLocked else {
                model.status = "The selection is locked. Unlock it before moving."
                return
            }
            let distance = modifiers.contains(.shift) ? 10.0 : 1.0
            model.perform { document in try document.editSelection { $0.translate(by: direction * distance) } }
            return
        }
        if modifiers == .control && key == "w" {
            model.board = model.board == .light ? .transparent : .light
            return
        }
        if modifiers == .control && key == "b" {
            model.board = model.board == .dark ? .transparent : .dark
            return
        }
        if event.keyCode == 36, linearID != nil { finishLinearPath(); return }
        if event.modifierFlags.contains(.command) {
            switch key {
            case "z":
                if event.modifierFlags.contains(.shift) { model.redo() } else { model.undo() }
            case "a": model.selectAll()
            case "d": model.perform { try $0.duplicateSelection() }
            case "g":
                model.perform { if event.modifierFlags.contains(.shift) { try $0.ungroupSelection() } else { try $0.groupSelection() } }
            case "s": model.saveBoard()
            case "o": model.openBoard()
            case "c": model.copySelection()
            case "x": model.copySelection(cut: true)
            case "v": model.paste()
            default: super.keyDown(with: event)
            }
            model.synchronize()
            return
        }
        if event.keyCode == 51 || event.keyCode == 117 { model.deleteCurrent(); return }
        if modifiers.intersection([.control, .command, .option]).isEmpty,
           let ink = InkShortcut.choices[key] {
            if model.effectiveTool.isProtection || model.effectiveTool == .image {
                model.status = "Color keys apply to drawing and text tools."
            } else {
                model.applyStyle { $0.setPaintColor(ink.color, fill: false) }
                model.status = "\(ink.name) ink"
            }
            return
        }
        if modifiers.intersection([.control, .command, .option]).isEmpty,
           let pressed = LocalShortcut(key: key, shift: modifiers.contains(.shift)) ?? LocalShortcut(key: key),
           let command = model.preferences.command(forPressed: pressed) {
            switch command {
            case .tool(let tool): if tool == .cover { model.chooseCover() } else { model.choose(tool) }
            case .action(let action): model.performShortcutAction(action)
            }
            return
        }
        super.keyDown(with: event)
    }

    var constructionPreview: Element? {
        guard let linearID, let point = linearPreview,
              var element = session?.document.elements.first(where: { $0.id == linearID }) else { return nil }
        if element.points.last != point { element.points.append(point) }
        guard element.points.count >= 2 else { return nil }
        element.frame = Rect.enclosing(element.points)
        element.invalidateGeometry()
        return element
    }

    private static func nudgeDirection(_ keyCode: UInt16) -> Point? {
        switch keyCode {
        case 123: Point(-1, 0)
        case 124: Point(1, 0)
        case 125: Point(0, 1)
        case 126: Point(0, -1)
        default: nil
        }
    }

    override func mouseMoved(with event: NSEvent) {
        updateHoverCursor(event)
        let point = Point(convert(event.locationInWindow, from: nil))
        if linearID != nil {
            linearPreview = point
            needsDisplay = true
        }
        refreshAttachHints(hovering: point)
    }

    func finishLinearPath() {
        guard let id = linearID, let session, let model else { return }
        do {
            guard let element = session.document.elements.first(where: { $0.id == id }),
                  element.points.count >= 2, let first = element.points.first, let last = element.points.last else {
                cancelGesture(); model.synchronize(); return
            }
            let start = session.document.nearestAnchor(to: first, excluding: id)
            let end = session.document.nearestAnchor(to: last, excluding: id)
            try session.document.update(id) { $0.startAnchor = start; $0.endAnchor = end }
            try session.document.commit()
            session.document.select([id])
            linearID = nil; linearPreview = nil
            setAttachHints([])
            model.tool = .select
            model.synchronize()
        } catch { fail(error) }
    }

    private func scheduleRecognition(id: UUID, preview: Element?) {
        guard let model, model.smartDraw, let session,
              let source = session.document.elements.first(where: { $0.id == id }), source.tool == .pen else { return }
        let generation = session.document.generation
        releasePreview = preview
        recognitionTask = Task { [weak self, weak session, weak model] in
            let recognized = await Task.detached(priority: .userInitiated) { ShapeRecognition.recognize(source) }.value
            guard !Task.isCancelled else { return }
            guard let recognized, let session else { self?.clearReleasePreview(); return }
            do {
                if try session.document.applyRecognition(recognized, expectedGeneration: generation,
                                                         expectedGeometryToken: source.geometryToken) {
                    self?.renderer.reset()
                    model?.synchronize()
                    self?.beginSnapMorph(from: source, to: recognized.id, preview: preview)
                } else { self?.clearReleasePreview() }
            } catch {
                self?.clearReleasePreview()
                model?.report(error)
            }
        }
    }

    // MARK: Transitions

    /// Jumps every drawing transition to its end state. Document content is never changed here.
    func finishTransitions() {
        clearReleasePreview()
        finishSnapMorph()
        finishEraseFade()
    }

    private func clearReleasePreview() {
        guard let preview = releasePreview else { return }
        releasePreview = nil
        invalidateTransition(previewRenderer.drawingBounds(for: preview))
    }

    private func invalidateTransition(_ rect: CGRect) {
        if session?.presentation?.screenViewport != nil || rect.isNull || rect.isInfinite { needsDisplay = true }
        else { setNeedsDisplay(rect.insetBy(dx: -2, dy: -2).integral.intersection(bounds)) }
    }

    private func beginSnapMorph(from ink: Element, to id: UUID, preview: Element?) {
        finishSnapMorph()
        guard let session, let model, !reducesMotion(), window?.isVisible == true, !model.workspaceHidden,
              let shape = session.document.elements.first(where: { $0.id == id }) else { clearReleasePreview(); return }
        let from = Self.resample(ink.points, count: 96)
        guard from.count == 96 else { clearReleasePreview(); return }
        let to = Self.snapOutline(of: shape, count: 96).map { Self.align($0, to: from, closed: shape.tool != .arrow) }
        var dirty = morphShapeRenderer.drawingBounds(for: shape).union(morphRenderer.drawingBounds(for: ink))
        if let preview { dirty = dirty.union(previewRenderer.drawingBounds(for: preview)) }
        if let to {
            let pad = shape.style.width * 2 + 4
            dirty = dirty.union(Rect.enclosing(to).cgRect.insetBy(dx: -pad, dy: -pad))
        }
        releasePreview = nil
        snapMorph = SnapMorph(element: shape, ink: ink, from: from, to: to, preview: preview,
            generation: session.document.generation, selection: session.document.selection, tool: model.tool, dirty: dirty)
        snapMorphStart = nil
        snapMorphProgress = 0
        invalidateTransition(dirty)
        morphTicker.start(view: self) { [weak self] now in self?.stepSnapMorph(now) ?? false }
    }

    private func stepSnapMorph(_ now: CFTimeInterval) -> Bool {
        guard let morph = snapMorph else { return false }
        guard snapMorphValid(morph) else { finishSnapMorph(); return false }
        let start = snapMorphStart ?? now
        snapMorphStart = start
        snapMorphProgress = min(1, max(0, (now - start) / Self.snapMorphDuration))
        invalidateTransition(morph.dirty)
        if snapMorphProgress >= 1 { finishSnapMorph(); return false }
        return true
    }

    private func snapMorphValid(_ morph: SnapMorph) -> Bool {
        guard let session, let model, gesture == nil, model.smartDraw, !model.workspaceHidden,
              window?.isVisible == true else { return false }
        return session.document.generation == morph.generation && session.document.selection == morph.selection
            && model.tool == morph.tool
    }

    private func finishSnapMorph() {
        morphTicker.stop()
        guard let morph = snapMorph else { return }
        snapMorph = nil
        snapMorphStart = nil
        snapMorphProgress = 0
        invalidateTransition(morph.dirty)
    }

    private func drawSnapMorph(_ morph: SnapMorph, in context: CGContext) {
        let time = snapMorphProgress
        let progress = Motion.progress(Motion.enter, at: time)
        if var dashed = morph.preview, progress < 1 {
            dashed.style.pattern = .dashed
            dashed.style.opacity = 0.75 * (1 - progress)
            previewRenderer.draw([dashed], in: context)
        }
        guard let to = morph.to else {
            drawTransition(morph.ink, alpha: 1 - progress, renderer: morphRenderer, in: context)
            drawTransition(morph.element, alpha: progress, renderer: morphShapeRenderer, in: context)
            return
        }
        // The real shape fades in over the last part so rough strokes, fills and arrowheads land without a pop.
        let reveal = Motion.smooth((time - 0.55) / 0.45)
        var line = Element(id: morph.ink.id, tool: .pen,
                           points: zip(morph.from, to).map { $0 + ($1 - $0) * progress }, style: morph.element.style)
        line.style.pattern = .solid
        line.style.roughness = 0
        line.style.pressure = false
        line.style.fill = nil
        line.style.opacity = morph.element.style.opacity * (0.75 + 0.25 * progress) * (1 - reveal)
        if line.style.opacity > 0.001 { morphRenderer.draw([line], in: context) }
        drawTransition(morph.element, alpha: reveal, renderer: morphShapeRenderer, in: context)
    }

    private func drawTransition(_ element: Element, alpha: Double, renderer: SceneRenderer, in context: CGContext,
                                scale: Double = 1) {
        guard alpha > 0.001 else { return }
        let center = element.frame.center
        context.saveGState()
        if scale != 1 {
            context.translateBy(x: center.x, y: center.y)
            context.scaleBy(x: scale, y: scale)
            context.translateBy(x: -center.x, y: -center.y)
        }
        context.setAlpha(min(1, alpha))
        context.beginTransparencyLayer(in: renderer.drawingBounds(for: element), auxiliaryInfo: nil)
        renderer.draw([element], in: context)
        context.endTransparencyLayer()
        context.restoreGState()
    }

    private func beginEraseFade(_ elements: [Element]) {
        finishEraseFade()
        guard !elements.isEmpty, let session, let model, window?.isVisible == true, !model.workspaceHidden else { return }
        let items = elements.map { (element: $0, renderer: SceneRenderer()) }
        let dirty = items.reduce(CGRect.null) { $0.union($1.renderer.drawingBounds(for: $1.element)) }
        eraseFade = EraseFade(items: items, generation: session.document.generation, scales: !reducesMotion(), dirty: dirty)
        eraseFadeStart = nil
        eraseFadeProgress = 0
        invalidateTransition(dirty)
        eraseTicker.start(view: self) { [weak self] now in self?.stepEraseFade(now) ?? false }
    }

    private func stepEraseFade(_ now: CFTimeInterval) -> Bool {
        guard let fade = eraseFade else { return false }
        guard let session, let model, gesture == nil, session.document.generation == fade.generation,
              !model.workspaceHidden, window?.isVisible == true else { finishEraseFade(); return false }
        let start = eraseFadeStart ?? now
        eraseFadeStart = start
        eraseFadeProgress = min(1, max(0, (now - start) / Self.eraseFadeDuration))
        invalidateTransition(fade.dirty)
        if eraseFadeProgress >= 1 { finishEraseFade(); return false }
        return true
    }

    private func finishEraseFade() {
        eraseTicker.stop()
        guard let fade = eraseFade else { return }
        eraseFade = nil
        eraseFadeStart = nil
        eraseFadeProgress = 0
        invalidateTransition(fade.dirty)
    }

    private func drawEraseFade(_ fade: EraseFade, in context: CGContext) {
        // Erased objects were already shown faded, so the fade continues from that strength.
        let alpha = 0.22 * (1 - Motion.progress(Motion.fade, at: eraseFadeProgress))
        let scale = fade.scales ? 1 - 0.03 * Motion.progress(Motion.exit, at: eraseFadeProgress) : 1
        for item in fade.items {
            drawTransition(item.element, alpha: alpha, renderer: item.renderer, in: context, scale: scale)
        }
    }

    static func snapOutline(of element: Element, count: Int) -> [Point]? {
        let box = element.frame
        var points: [Point]
        switch element.tool {
        case .rectangle:
            points = [box.origin, Point(box.maxX, box.y), Point(box.maxX, box.maxY), Point(box.x, box.maxY), box.origin]
        case .diamond:
            points = [Point(box.center.x, box.y), Point(box.maxX, box.center.y), Point(box.center.x, box.maxY),
                      Point(box.x, box.center.y), Point(box.center.x, box.y)]
        case .ellipse:
            points = (0...64).map {
                let angle = Double($0) / 64 * 2 * Double.pi
                return Point(box.center.x + box.width / 2 * cos(angle), box.center.y + box.height / 2 * sin(angle))
            }
        case .arrow:
            guard element.points.count >= 2 else { return nil }
            points = element.points
        default:
            return nil
        }
        if element.rotation != 0 { points = points.map { $0.rotated(around: box.center, by: element.rotation) } }
        let result = resample(points, count: count)
        return result.count == count ? result : nil
    }

    /// Starts a closed outline at the point nearest the ink's first sample and runs it in the same direction.
    static func align(_ outline: [Point], to ink: [Point], closed: Bool) -> [Point] {
        guard let first = ink.first, let last = ink.last, outline.count >= 3 else { return outline }
        guard closed else {
            return first.distance(to: outline[0]) + last.distance(to: outline[outline.count - 1])
                <= first.distance(to: outline[outline.count - 1]) + last.distance(to: outline[0])
                ? outline : outline.reversed()
        }
        func area(_ points: [Point]) -> Double {
            zip(points, points.dropFirst() + [points[0]]).reduce(0) { $0 + $1.0.x * $1.1.y - $1.1.x * $1.0.y }
        }
        var ring = Array(outline.dropLast())
        if (area(ring) < 0) != (area(ink) < 0) { ring.reverse() }
        guard let start = ring.indices.min(by: { ring[$0].distance(to: first) < ring[$1].distance(to: first) }) else {
            return outline
        }
        ring = Array(ring[start...] + ring[..<start])
        return ring + [ring[0]]
    }

    static func resample(_ points: [Point], count: Int) -> [Point] {
        guard let first = points.first, let last = points.last, count >= 2, points.count >= 2 else { return [] }
        var distances = [0.0]
        for index in 1..<points.count { distances.append(distances[index - 1] + points[index - 1].distance(to: points[index])) }
        guard let total = distances.last, total > 0, total.isFinite else { return [] }
        var index = 1
        var output = [first]
        for sample in 1..<(count - 1) {
            let position = total * Double(sample) / Double(count - 1)
            while index < points.count - 1 && distances[index] < position { index += 1 }
            let segment = distances[index] - distances[index - 1]
            let fraction = segment > 0 ? (position - distances[index - 1]) / segment : 0
            output.append(points[index - 1] + (points[index] - points[index - 1]) * fraction)
        }
        output.append(last)
        return output
    }

    private func startText(at point: Point) {
        guard let session, let model else { return }
        let target = ElementStacking.frontmost(at: point, in: session.document.elements)
        if let target, target.tool.isShape || target.tool == .text, target.locked {
            model.status = "Unlock this object to edit its text."
            return
        }
        if let target, target.tool.isShape {
            startShapeLabel(target)
            return
        }
        session.document.begin()
        let existing = session.document.elements.last { $0.tool == .text && !$0.locked && HitTesting.contains(point, element: $0) }
        initialTextElement = existing
        let style = model.preferences.toolStyles[Tool.text.rawValue] ?? .defaults(for: .text)
        let element = existing ?? Element(tool: .text, frame: Rect(x: point.x, y: point.y, width: 32, height: style.fontSize * 1.5), style: style)
        do { if existing == nil { try session.document.add(element) } } catch { fail(error); return }
        session.document.select([element.id])
        installTextEditor(element)
        model.synchronize()
    }

    private func startShapeLabel(_ owner: Element) {
        guard let session, let model else { return }
        var style = model.preferences.toolStyles[Tool.text.rawValue] ?? .defaults(for: .text)
        style.alignment = .center
        let label = owner.label ?? ShapeLabel(style: style, followsStroke: true)
        session.document.begin()
        initialShapeLabel = owner.label
        editingShapeLabelID = owner.id
        do {
            if owner.label == nil { try session.document.update(owner.id) { $0.label = label } }
            session.document.select([owner.id])
            guard let text = ShapeLabelLayout.textElement(for: owner, label: label) else {
                throw CanvasError.invalidGeometry
            }
            installTextEditor(text)
            model.synchronize()
        } catch { fail(error) }
    }

    private func installTextEditor(_ element: Element) {
        guard let session else { return }
        textViewport = session.presentation?.freezeViewportForText()
        let frame: Rect
        if let viewport = textViewport {
            frame = Rect(from: viewport.outputPoint(for: element.frame.origin),
                         to: viewport.outputPoint(for: Point(element.frame.maxX, element.frame.maxY)))
        } else { frame = element.frame }
        let scale = textViewport.map { $0.output.width / $0.source.width } ?? 1
        let storage = NSTextStorage()
        let layout = PaintTextLayoutManager()
        layout.backgroundLayoutEnabled = false
        let container = NSTextContainer(containerSize: NSSize(width: frame.width, height: CGFloat.greatestFiniteMagnitude))
        storage.addLayoutManager(layout)
        layout.addTextContainer(container)
        let editor = NSTextView(frame: frame.cgRect, textContainer: container)
        editor.isRichText = false; editor.drawsBackground = false
        editor.backgroundColor = .clear
        editor.textColor = element.style.color.nsColor.withAlphaComponent(element.style.color.alpha * element.style.opacity)
        editor.insertionPointColor = element.style.color.nsColor
        editor.font = NativeTextFont.resolve(element.style, scale: scale)
        editor.alignment = element.style.alignment == .center ? .center : element.style.alignment == .right ? .right : .left
        editor.string = element.text
        layout.gradient = element.style.strokeGradient
        editor.delegate = self
        editor.isVerticallyResizable = true
        editor.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        editor.minSize = .zero
        editor.textContainerInset = .zero
        editor.textContainer?.lineFragmentPadding = 0
        editor.autoresizingMask = []
        editor.textContainer?.widthTracksTextView = true
        editor.setAccessibilityLabel(editingShapeLabelID == nil
            ? "Annotation text. Escape or Command Return finishes editing."
            : "Shape label. Escape or Command Return finishes editing.")
        textEditor = editor; textElement = element.id
        resizeTextEditor()
        addSubview(editor); window?.makeFirstResponder(editor)
        needsDisplay = true
    }

    func textView(_ textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        if commandSelector == #selector(NSResponder.cancelOperation(_:)) {
            commitTextIfNeeded(); return true
        }
        if commandSelector == #selector(NSResponder.insertNewline(_:)),
           NSApp.currentEvent?.modifierFlags.contains(.command) == true {
            commitTextIfNeeded(); return true
        }
        return false
    }

    func commitTextIfNeeded() {
        guard let editor = textEditor, let id = textElement, let session else { return }
        resizeTextEditor()
        do {
            if let shapeID = editingShapeLabelID,
               let owner = session.document.elements.first(where: { $0.id == shapeID }),
               let labelStyle = owner.label?.style {
                let label = editor.string.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                    ? nil : ShapeLabel(text: editor.string, style: labelStyle, followsStroke: owner.label?.followsStroke)
                if label == initialShapeLabel {
                    session.document.cancel()
                    session.document.select([shapeID])
                } else {
                    try session.document.update(shapeID) {
                        if let label { $0.frame = ShapeLabelLayout.fittedOwnerFrame($0, label: label) }
                        $0.label = label
                    }
                    try session.document.commit()
                }
                finishTextEditor(editor)
                return
            }
            let emptyNewText = initialTextElement == nil &&
                editor.string.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            let unchangedExistingText = initialTextElement.map { original in
                editor.string == original.text && session.document.elements.first { $0.id == id } == original
            } ?? false
            if emptyNewText || unchangedExistingText {
                session.document.cancel()
                if unchangedExistingText { session.document.select([id]) }
                finishTextEditor(editor)
                return
            }
            let frame = sourceFrame(for: editor)
            try session.document.update(id) { $0.text = editor.string; $0.frame = frame }
            try session.document.commit()
            finishTextEditor(editor)
        } catch { fail(error) }
    }

    private func finishTextEditor(_ editor: NSTextView) {
        editor.removeFromSuperview()
        textEditor = nil; textElement = nil
        initialTextElement = nil
        editingShapeLabelID = nil; initialShapeLabel = nil
        textViewport = nil; session?.presentation?.releaseTextViewport()
        window?.makeFirstResponder(self)
        model?.synchronize()
    }

    func applyTextStyle(_ style: Style) {
        guard let editor = textEditor else { return }
        var style = style
        if let id = editingShapeLabelID, let owner = session?.document.elements.first(where: { $0.id == id }) {
            style.opacity *= owner.style.opacity
        }
        let scale = textViewport.map { $0.output.width / $0.source.width } ?? 1
        editor.font = NativeTextFont.resolve(style, scale: scale)
        editor.textColor = style.color.nsColor.withAlphaComponent(style.color.alpha * style.opacity)
        (editor.layoutManager as? PaintTextLayoutManager)?.gradient = style.strokeGradient
        editor.insertionPointColor = style.color.nsColor
        editor.alignment = style.alignment == .center ? .center : style.alignment == .right ? .right : .left
        resizeTextEditor()
    }

    func textDidChange(_ notification: Notification) {
        guard let editor = notification.object as? NSTextView, editor === textEditor else { return }
        resizeTextEditor()
        session?.presentation?.refreshSharedRegion()
    }

    private func sourceFrame(for editor: NSTextView) -> Rect {
        let frame = Rect(editor.frame)
        guard let viewport = textViewport else { return frame }
        return Rect(from: viewport.sourcePoint(for: frame.origin),
                    to: viewport.sourcePoint(for: Point(frame.maxX, frame.maxY)))
    }

    private func resizeTextEditor() {
        guard let editor = textEditor, let font = editor.font else { return }
        if let id = editingShapeLabelID,
           let owner = session?.document.elements.first(where: { $0.id == id }),
           let style = owner.label?.style,
           let label = ShapeLabelLayout.textElement(for: owner, label: ShapeLabel(
                text: editor.string, style: style, followsStroke: owner.label?.followsStroke)) {
            var frame = label.frame
            var paintFrame = owner.label?.followsStroke == true ? owner.frame : label.frame
            if let viewport = textViewport {
                frame = Rect(from: viewport.outputPoint(for: frame.origin),
                             to: viewport.outputPoint(for: Point(frame.maxX, frame.maxY)))
                paintFrame = Rect(from: viewport.outputPoint(for: paintFrame.origin),
                                  to: viewport.outputPoint(for: Point(paintFrame.maxX, paintFrame.maxY)))
            }
            editor.frameCenterRotation = 0
            editor.frame = frame.cgRect
            editor.frameCenterRotation = CGFloat(owner.rotation * 180 / .pi)
            if let layout = editor.layoutManager as? PaintTextLayoutManager {
                layout.gradient = label.style.strokeGradient
                layout.paintBounds = paintFrame.cgRect.offsetBy(dx: -frame.x, dy: -frame.y)
            }
            editor.needsDisplay = true
            needsDisplay = true
            return
        }
        let size = NativeTextFont.contentSize(editor.string, font: font)
        guard size.width.isFinite, size.height.isFinite else {
            model?.report(CanvasError.invalidGeometry)
            return
        }
        editor.setFrameSize(size)
        (editor.layoutManager as? PaintTextLayoutManager)?.paintBounds = editor.bounds
        editor.needsDisplay = true
    }

    private func fail(_ error: Error) {
        cancelGesture()
        model?.report(error)
        model?.synchronize()
    }
}

/// The "Use apps" button ignores clicks while it fades out.
@MainActor
private final class HUDButton: NSButton {
    weak var fader: HUDFader?
    override func hitTest(_ point: NSPoint) -> NSView? {
        fader?.acceptsInput == false ? nil : super.hitTest(point)
    }
}
