import AppKit
import NativePlatform
import UniformTypeIdentifiers

extension WorkspaceModel {
    var supportInfo: String {
        effectiveLoginItemController?.refresh()
        return """
        Annotator support info
        Version: \(versionLabel)
        Running build: \(buildLabel)
        Edition: free
        Updates configured: \(updatesConfigured)
        Automatic update checks: \(automaticallyCheckForUpdates)
        Launch at login: \(effectiveLoginItemController?.state.rawValue ?? "not checked")
        App: \(Bundle.main.bundleIdentifier ?? "Unknown")
        Automated test process: \(ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil)
        macOS: \(ProcessInfo.processInfo.operatingSystemVersionString)
        Screens: \(NSScreen.screens.count)
        Toolbar: \(preferences.toolbarOrientation?.rawValue ?? "horizontal")
        Mode: \(mode.rawValue)
        Workspace: live desktop
        Drawings hidden: \(workspaceHidden)
        Screen access allowed: \(DisplayCapture.hasPermission)
        Screen capture has worked: \(captureVerified)
        Screen capture error present: \(captureFailureMessage != nil)
        Zoom mode: \(active?.document.presentation.mode.rawValue ?? "off")
        Global zoom shortcut registered: \(zoomHotkeyRegistered)
        Screen zoom level: \(magnification)
        Scroll shortcut: Option only
        Scroll permission allowed: \(ZoomGestureMonitor.hasPermission)
        Global scroll monitor active: \(zoomGesturesAvailable)
        Scroll error present: \(zoomGestureMessage != nil)
        No drawings, screenshots or document names are included.
        """
    }

    func copySupportInfo(to pasteboard: NSPasteboard = .general) {
        pasteboard.clearContents()
        guard pasteboard.setString(supportInfo, forType: .string) else {
            report(
                NSError(
                    domain: "Annotator", code: 1,
                    userInfo: [NSLocalizedDescriptionKey: "Could not copy support info. Try again."]))
            return
        }
        status = "Support info copied. It does not include your drawings or images."
    }

    func saveSupportInfo() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "Annotator support.txt"
        panel.allowedContentTypes = [.plainText]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try supportInfo.write(to:url,atomically:true,encoding:.utf8)
            status = "Support info saved. No drawing content or screen images were included."
        } catch { report(error) }
    }
}
