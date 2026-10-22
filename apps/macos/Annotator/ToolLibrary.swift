import SwiftUI
import AnnotationCore

enum ToolLibrarySection: String, CaseIterable {
    case draw = "Draw", shapes = "Shapes", focus = "Focus", images = "Extras", myShapes = "My shapes"

    var tools: [Tool] {
        switch self {
        case .draw: [.select, .pen, .highlighter, .text, .arrow, .line, .eraser]
        case .shapes: [.rectangle, .ellipse, .diamond, .triangle, .hexagon, .star, .person]
            + Tool.systemDesignShapes + [.database, .queue, .grid, .axes]
        case .focus: [.cover]
        case .images: [.image, .ruler, .counter]
        case .myShapes: []
        }
    }

    static func containing(_ tool: Tool) -> Self {
        if tool.isProtection { return .focus }
        return allCases.first { $0.tools.contains(tool) } ?? .draw
    }
}

struct ToolLibrary: View {
    @ObservedObject var model: WorkspaceModel
    @Binding var section: ToolLibrarySection
    let dismiss: () -> Void
    var viewportHeight: CGFloat = 192
    var viewportWidth: CGFloat = 336
    private let columns = [GridItem(.adaptive(minimum: 68, maximum: 96), spacing: 8)]
    private var gridHeight: CGFloat {
        let extra = section == .focus ? 5 : section == .images ? 1 : 0
        let count = max(1,Int((viewportWidth-24)/76))
        let rows = (section.tools.count + extra + count - 1) / count
        return min(viewportHeight, max(160, CGFloat(rows * 76 + max(0,rows-1) * 8)))
    }

    var body: some View {
        VStack(spacing: 12) {
            Picker("Tool category", selection: $section) {
                ForEach(ToolLibrarySection.allCases, id: \.self) { value in
                    Text(value.rawValue).tag(value)
                }
            }.pickerStyle(.segmented).labelsHidden()
            if section == .myShapes {
                ScrollView(.vertical) {
                    MyShapesLibrary(model: model, onInsert: dismiss)
                }.frame(height: viewportHeight).clipped()
            } else {
            ScrollView(.vertical) {
            LazyVGrid(columns: columns, spacing: 8) {
                ForEach(section.tools, id: \.self) { tool in
                    tile(tool == .image ? "Paste image" : tool == .cover ? "Effects" : tool.label, symbol: tool.toolbarSymbol,
                         shortcut: model.shortcut(for: .tool(tool))?.label,
                         selected: model.tool == tool || (tool == .cover && model.tool.isProtection), drawingTool: tool) {
                        dismiss()
                        if tool == .cover { model.chooseCover() } else { model.choose(tool) }
                    }
                }
                if section == .images {
                    tile("Insert image", symbol: "folder") { dismiss(); model.importScreenshot() }
                }
                if section == .focus && !model.hasReferenceImage {
                    tile("Pointer lens", symbol: "plus.magnifyingglass", shortcut: model.shortcut(for: .action(.pointerLens))?.label, selected: model.magnifierEnabled) {
                        dismiss(); model.toggleMagnifier()
                    }
                    tile("Screen zoom", symbol: "arrow.up.left.and.arrow.down.right", shortcut: (model.preferences.zoomShortcut ?? .liveZoom).label, selected: model.screenZoomEnabled) {
                        dismiss(); model.toggleScreenZoom()
                    }
                    tile("Spotlight", symbol: "light.beacon.max", shortcut: model.shortcut(for: .action(.spotlight))?.label, selected: model.spotlight) {
                        model.spotlight.toggle()
                    }
                    tile("Cursor halo", symbol: "cursorarrow.rays", shortcut: model.shortcut(for: .action(.cursorHalo))?.label, selected: model.cursorHalo) {
                        model.cursorHalo.toggle()
                    }
                    tile("Share region", symbol: "rectangle.on.rectangle",
                         shortcut: model.shortcut(for: .action(.shareRegion))?.label,
                         selected: model.regionSharingState.isCapturing) {
                        dismiss(); model.openRegionSharing()
                    }
                }
            }
            .padding(.trailing, 16)
            }
            .scrollIndicators(.visible)
            .frame(height: gridHeight)
            .clipped()
            .accessibilityIdentifier("tool-library-scroll")
            if section == .focus {
                HStack(spacing: 10) {
                    Text("Spotlight size").font(.caption)
                    Slider(value: $model.spotlightRadius, in: 50...350).accessibilityLabel("Spotlight size")
                    Text("\(Int(model.spotlightRadius))").font(.caption.monospacedDigit()).frame(width: 30)
                    Button("Pointer settings") {
                        model.settingsTab = .pointer
                        model.onOpenPreferences?()
                    }.font(.caption).buttonStyle(.link)
                }
            }
            }
        }
        .frame(width: viewportWidth)
    }

    private func tile(_ title: String, symbol: String, shortcut: String? = nil, help: String? = nil,
                      selected: Bool = false, drawingTool: Tool? = nil, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(spacing: 4) {
                Group {
                    if let drawingTool { ToolGlyph(tool: drawingTool) }
                    else { Image(systemName: symbol).font(.system(size: 21)) }
                }.frame(height: 26)
                Text(title).font(.system(size: 10, weight: .medium)).lineLimit(2)
                    .multilineTextAlignment(.center).frame(height: 26)
                Text(shortcut?.uppercased() ?? " ").font(.system(size: 9)).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, minHeight: 76, maxHeight: 76)
            .background(selected ? Color.accentColor.opacity(0.16) : Color.secondary.opacity(0.07),
                        in: RoundedRectangle(cornerRadius: 9))
            .contentShape(RoundedRectangle(cornerRadius: 9))
        }
        .buttonStyle(.plain).help(help ?? title).accessibilityLabel(title)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

struct SelectionCommands: View {
    @ObservedObject var model: WorkspaceModel
    var includesDeletion = true
    var body: some View {
        Group {
            Button("Duplicate") { model.perform { try $0.duplicateSelection() } }
            Button("Group") { model.perform { try $0.groupSelection() } }
            Button("Ungroup") { model.perform { try $0.ungroupSelection() } }
            Button("Lock") { model.perform { try $0.setSelectionLocked(true) } }
            Button("Unlock") { model.perform { try $0.setSelectionLocked(false) } }
            Divider()
            Button("Bring to front") { model.perform { try $0.arrangeSelection(toFront: true) } }
            Button("Send to back") { model.perform { try $0.arrangeSelection(toFront: false) } }
            if includesDeletion { Button("Delete selected") { model.deleteSelected() } }
        }
    }
}

struct ToolLibraryPanel: View {
    @ObservedObject var model: WorkspaceModel
    @Binding var section: ToolLibrarySection
    let dismiss: () -> Void
    var maximumHeight: CGFloat = 420
    var width: CGFloat = 368

    var body: some View {
        VStack(spacing: 12) {
            HStack {
                Text("Tools").font(.headline)
                Spacer()
                Button("Paste image", systemImage: "photo.on.rectangle") { dismiss(); model.pasteImage() }
                    .controlSize(.small)
            }
            ToolLibrary(model: model, section: $section, dismiss: dismiss,
                        viewportHeight: max(76,maximumHeight-168-(section == .focus ? 28 : 0)),
                        viewportWidth: max(336,width-32))
            Spacer(minLength: 0)
            Divider()
            HStack(spacing: 8) {
                Menu("Selection") {
                    Button("Save selection to My shapes...") { section = .myShapes }
                    Divider()
                    SelectionCommands(model: model, includesDeletion: false)
                }.disabled(model.selectionCount == 0)
                Menu("File") {
                    Button("Open board...") { dismiss(); model.openBoard() }
                    Button("Save board...") { dismiss(); model.saveBoard() }
                    Button("Export PNG...") { dismiss(); model.exportPNG() }
                }
                Menu("Workspace") {
                    ForEach(WorkspaceModel.Board.allCases, id: \.self) { value in Button(value.rawValue) { model.setBoard(value) } }
                    Divider()
                    Button("Help") { dismiss(); model.openPreferences(tab: .help) }
                }
            }.font(.caption).menuStyle(.borderlessButton)
                .accessibilityElement(children: .contain)
                .accessibilityIdentifier("tool-library-footer")
        }.padding(16).frame(width: width)
    }

}

struct MyShapesLibrary: View {
    @ObservedObject var model: WorkspaceModel
    @ObservedObject var library: ShapeLibraryModel
    @State private var name = ""
    @State private var deletion: SavedShape?
    @State private var actionError: String?
    @State private var message: String?
    @State private var editing = false
    @State private var selectedID: UUID?
    @State private var renaming = false
    @State private var renameText = ""
    let onInsert: () -> Void
    private var selectedShape: SavedShape? { library.shapes.first { $0.id == selectedID } }

    init(model: WorkspaceModel, library: ShapeLibraryModel? = nil, onInsert: @escaping () -> Void = {}) {
        self.model = model
        self.library = library ?? .scoped(to: model.preferenceDefaults)
        self.onInsert = onInsert
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(editing ? "Select a saved shape to manage it." : "Your saved drawings").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button(editing ? "Done" : "Edit") {
                    editing.toggle()
                    selectedID = nil
                }.disabled(library.isBusy || library.shapes.isEmpty)
                    .accessibilityLabel(editing ? "Done editing saved shapes" : "Edit saved shapes")
            }
            if editing {
                HStack {
                    Button("Rename...") {
                        renameText = selectedShape?.name ?? ""
                        renaming = true
                    }.disabled(selectedShape == nil || library.isBusy)
                    Button("Delete...", role: .destructive) { deletion = selectedShape }
                        .disabled(selectedShape == nil || library.isBusy)
                }
            } else {
            TextField("Shape name", text: $name)
                .textFieldStyle(.roundedBorder)
                .accessibilityLabel("Shape name")
                .accessibilityIdentifier("my-shapes-name")
                .onSubmit { saveSelection() }
            HStack {
                Button("Save selection") { saveSelection() }
                    .disabled(model.selectionCount == 0 || library.isBusy || !library.isLoaded)
                    .accessibilityIdentifier("my-shapes-save")
                Spacer()
                Button("Reload") { Task { await library.reload() } }.disabled(library.isBusy)
            }
            Text("Save drawings or text. Covers, pixel effects and images cannot be saved.")
                .font(.caption).foregroundStyle(.secondary)
            }
            if let error = actionError ?? library.errorMessage {
                Text(error).font(.caption).foregroundStyle(.red).textSelection(.enabled)
            } else if let message {
                Text(message).font(.caption).foregroundStyle(.secondary)
            }
            if library.isBusy { ProgressView().controlSize(.small).accessibilityLabel("Updating My shapes") }
            if library.shapes.isEmpty {
                Text(library.isLoaded ? "No saved shapes yet." : "Loading saved shapes...")
                    .font(.callout).padding(.vertical, 4)
            } else {
                ScrollView {
                    LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: 10) {
                        ForEach(library.shapes) { shape in
                            VStack(spacing: 4) {
                                Button {
                                    if editing { selectedID = shape.id; return }
                                    do {
                                        try model.insertSavedShape(shape)
                                        actionError = nil
                                        message = "Inserted \(shape.name) at the canvas center."
                                        onInsert()
                                    } catch { actionError = error.localizedDescription }
                                } label: {
                                    VStack(spacing: 4) {
                                        SavedShapeThumbnail(shape: shape).frame(height: 72)
                                            .background(Color.gray.opacity(0.18), in: RoundedRectangle(cornerRadius: 6))
                                            .accessibilityHidden(true)
                                        Text(shape.name).font(.caption).lineLimit(2).frame(height: 30)
                                    }.frame(maxWidth: .infinity).contentShape(Rectangle())
                                        .padding(4)
                                        .overlay {
                                            RoundedRectangle(cornerRadius: 8)
                                                .strokeBorder(editing && selectedID == shape.id ? Color.accentColor : .clear, lineWidth: 2)
                                        }
                                }
                                .buttonStyle(.plain)
                                .disabled(library.isBusy || !library.isLoaded || (!editing && model.active == nil))
                                .help(editing ? "Select \(shape.name) to rename or delete it" : "Insert \(shape.name) at the canvas center")
                                .accessibilityLabel(editing ? "Select saved shape \(shape.name)" : "Insert \(shape.name)")
                                .accessibilityAddTraits(editing && selectedID == shape.id ? .isSelected : [])
                            }
                        }
                    }.padding(2)
                }.frame(height: 230)
            }
            Text(library.shapes.isEmpty ?
                 "Select something in your drawing, enter a name, then click Save selection." :
                 editing ? "Rename or delete the selected saved shape. Existing copies stay unchanged." :
                    "Click to insert. Drawings saved together arrive as one group; Ungroup separates them.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .task { await library.reload() }
        .alert("Rename saved shape", isPresented: $renaming) {
            TextField("Shape name", text: $renameText)
            Button("Save") {
                guard let shape = selectedShape else { return }
                Task { await library.rename(shape, to: renameText) }
            }
            Button("Cancel", role: .cancel) {}
        }
        .alert("Delete saved shape?", isPresented: Binding(
            get: { deletion != nil }, set: { if !$0 { deletion = nil } }
        ), presenting: deletion) { shape in
            Button("Delete", role: .destructive) {
                actionError = nil
                message = nil
                Task { await library.delete(shape) }
            }
            Button("Cancel", role: .cancel) {}
        } message: { shape in
            Text("Delete \(shape.name) from My shapes? Copies on your canvas will stay.")
        }
    }

    private func saveSelection() {
        guard !library.isBusy, library.isLoaded else { return }
        guard !model.selectionSpansDisplays else {
            actionError = "Select drawings on one display before saving a reusable shape."
            return
        }
        guard let active = model.active else {
            actionError = "Open a drawing canvas first."
            return
        }
        guard !active.canvas.hasActiveEdit else {
            actionError = ShapeLibraryError.unfinishedEdit.localizedDescription
            return
        }
        let document = active.document
        let savedName = name
        actionError = nil
        message = nil
        Task {
            if await library.save(name: savedName, document: document) {
                name = ""
                message = "Saved \(savedName.trimmingCharacters(in: .whitespacesAndNewlines)) to My shapes."
            }
        }
    }
}

extension Tool {
    var toolbarSymbol: String {
        switch self {
        case .select: "cursorarrow"
        case .rectangle: "rectangle"
        case .diamond: "diamond"
        case .ellipse: "circle"
        case .triangle: "triangle"
        case .hexagon: "hexagon"
        case .star: "star"
        case .arrow: "arrow.up.right"
        case .line: "line.diagonal"
        case .pen: "pencil"
        case .text: "textformat"
        case .highlighter: "highlighter"
        case .eraser: "eraser"
        case .cover: "camera.filters"
        case .blur: "aqi.medium"
        case .pixelate: "square.grid.3x3.fill"
        case .distort: "tornado"
        case .ruler: "ruler"
        case .counter: "1.circle"
        case .image: "photo.on.rectangle"
        case .database: "cylinder"
        case .databaseStack: "cylinder.split.1x2"
        case .server: "server.rack"
        case .messaging: "bubble.left.and.bubble.right"
        case .web: "globe"
        case .mobile: "iphone"
        case .desktop: "desktopcomputer"
        case .laptop: "laptopcomputer"
        case .document: "doc.text"
        case .configuration: "gearshape"
        case .loadBalancer: "arrow.triangle.branch"
        case .apiGateway: "arrow.right.square"
        case .envelope: "envelope"
        case .cart: "cart"
        case .bell: "bell"
        case .queue: "rectangle.stack"
        case .person: "person"
        case .grid: "grid"
        case .axes: "chart.xyaxis.line"
        }
    }
}
