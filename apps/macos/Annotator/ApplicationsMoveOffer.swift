import AppKit

/// Where this copy of Annotator runs from. Only a copy in an Applications folder can update itself:
/// Sparkle refuses to update a copy that macOS runs from a temporary read-only location (App
/// Translocation), and a disk image is read-only. macOS stops translocating a downloaded app only when
/// the person moves it with Finder, so the offer shows the copy and the Applications folder in Finder.
/// It never moves the app itself and never changes its quarantine attribute.
enum ApplicationsPlacement: Equatable {
    case applications
    case translocated(original: URL?)
    case readOnlyVolume(URL)
    case elsewhere(URL)
}

struct ApplicationsPlacementProbe {
    var applicationFolders: [URL]
    var originalPath: (URL) -> URL?
    var readOnlyVolume: (URL) -> URL?

    static var live: ApplicationsPlacementProbe {
        ApplicationsPlacementProbe(
            applicationFolders: FileManager.default.urls(for: .applicationDirectory, in: .allDomainsMask),
            originalPath: TranslocationLookup.originalPath(of:),
            readOnlyVolume: { url in
                guard let values = try? url.resourceValues(forKeys: [.volumeIsReadOnlyKey, .volumeURLKey]),
                      values.volumeIsReadOnly == true else { return nil }
                return values.volume
            })
    }

    func placement(of bundle: URL) -> ApplicationsPlacement {
        let path = bundle.resolvingSymlinksInPath().standardizedFileURL
        if path.pathComponents.contains("AppTranslocation") {
            return .translocated(original: originalPath(path))
        }
        let inFolder = applicationFolders.contains { folder in
            path.path.hasPrefix(folder.resolvingSymlinksInPath().standardizedFileURL.path + "/")
        }
        // Also accept an Applications folder on another volume, as Finder and Launchpad do.
        if inFolder || path.deletingLastPathComponent().pathComponents.contains("Applications") {
            return .applications
        }
        if let volume = readOnlyVolume(path) { return .readOnlyVolume(volume) }
        return .elsewhere(path)
    }
}

/// Finds where a translocated copy really is, using the documented `security translocate-original-path`
/// tool; the matching Security framework call is not public.
enum TranslocationLookup {
    static func originalPath(of url: URL) -> URL? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/security")
        process.arguments = ["translocate-original-path", url.path]
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { return nil }
        let deadline = Date().addingTimeInterval(2)
        while process.isRunning && Date() < deadline { usleep(20_000) }
        if process.isRunning {
            process.terminate()
            return nil
        }
        guard process.terminationStatus == 0 else { return nil }
        return parse(String(decoding: output.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self), for: url)
    }

    /// The tool prints a heading, then the original path; it repeats the input when nothing is translocated.
    static func parse(_ text: String, for url: URL) -> URL? {
        let lines = text.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }
        guard let line = lines.last(where: { $0.hasPrefix("/") }) else { return nil }
        let original = URL(fileURLWithPath: line).standardizedFileURL
        return original.path == url.standardizedFileURL.path ? nil : original
    }
}

@MainActor
protocol FinderPresenting {
    func open(_ url: URL)
    func reveal(_ url: URL)
}

@MainActor
struct WorkspaceFinder: FinderPresenting {
    func open(_ url: URL) { NSWorkspace.shared.open(url) }
    func reveal(_ url: URL) { NSWorkspace.shared.activateFileViewerSelecting([url]) }
}

/// Asks once at launch whether to show a copy outside Applications in Finder, so the person can move it.
@MainActor
final class ApplicationsMoveOffer {
    static let suppressedKey = "applications-move-offer-suppressed"
    /// Development and recording builds use their own identifier and never ask.
    static let releaseBundleIdentifier = "com.dorlugasigal.annotator"

    enum Choice: Equatable {
        case showInFinder
        case notNow
    }

    private let defaults: UserDefaults
    private let finder: FinderPresenting
    private let applicationsFolder: URL
    private let downloadsFolder: URL?

    init(defaults: UserDefaults, finder: FinderPresenting = WorkspaceFinder(),
         applicationsFolder: URL = URL(fileURLWithPath: "/Applications"),
         downloadsFolder: URL? = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first) {
        self.defaults = defaults
        self.finder = finder
        self.applicationsFolder = applicationsFolder
        self.downloadsFolder = downloadsFolder
    }

    var suppressed: Bool { defaults.bool(forKey: Self.suppressedKey) }

    func mayAsk(bundleIdentifier: String?, isLoginItem: Bool) -> Bool {
        bundleIdentifier == Self.releaseBundleIdentifier && !isLoginItem && !suppressed
    }

    func alert(for placement: ApplicationsPlacement) -> NSAlert {
        let alert = NSAlert()
        alert.alertStyle = .informational
        alert.messageText = "Move Annotator to Applications"
        if case .readOnlyVolume = placement {
            alert.informativeText = "Annotator is running from the disk image, so it can't update itself. "
                + "Drag it into Applications in the disk image window, then open it from Applications. "
                + "Annotator quits while you move it."
            alert.addButton(withTitle: "Show Disk Image")
        } else {
            alert.informativeText = "Annotator can update itself only from the Applications folder. "
                + "Choose Show in Finder, drag Annotator into Applications, then open it from there. "
                + "Annotator quits while you move it."
            alert.addButton(withTitle: "Show in Finder")
        }
        alert.addButton(withTitle: "Not Now").keyEquivalent = "\u{1b}"
        alert.showsSuppressionButton = true
        alert.suppressionButton?.title = "Don't ask again"
        return alert
    }

    /// Returns true when Annotator should quit so the person can move it.
    func handle(_ choice: Choice, dontAskAgain: Bool, for placement: ApplicationsPlacement) -> Bool {
        if dontAskAgain { defaults.set(true, forKey: Self.suppressedKey) }
        guard choice == .showInFinder else { return false }
        switch placement {
        case .applications:
            return false
        case .readOnlyVolume(let volume):
            // The disk image window shows Annotator next to its Applications link.
            finder.open(volume)
        case .translocated(let original):
            finder.open(applicationsFolder)
            if let original { finder.reveal(original) } else if let downloadsFolder { finder.open(downloadsFolder) }
        case .elsewhere(let bundle):
            finder.open(applicationsFolder)
            finder.reveal(bundle)
        }
        return true
    }

    /// Checks the running copy and asks when it is outside Applications. Returns true when Annotator should quit.
    func offerAtLaunch(bundleURL: URL = Bundle.main.bundleURL, bundleIdentifier: String? = Bundle.main.bundleIdentifier,
                       isLoginItem: Bool, probe: ApplicationsPlacementProbe = .live,
                       present: @MainActor (NSAlert) -> NSApplication.ModalResponse = ApplicationsMoveOffer.presentModally) -> Bool {
        guard mayAsk(bundleIdentifier: bundleIdentifier, isLoginItem: isLoginItem) else { return false }
        let placement = probe.placement(of: bundleURL)
        guard placement != .applications else { return false }
        let alert = alert(for: placement)
        let response = present(alert)
        return handle(response == .alertFirstButtonReturn ? .showInFinder : .notNow,
                      dontAskAgain: alert.suppressionButton?.state == .on, for: placement)
    }

    static func presentModally(_ alert: NSAlert) -> NSApplication.ModalResponse {
        // A menu bar app is not active at launch, so bring the question to the front.
        NSApp.activate()
        return alert.runModal()
    }
}
