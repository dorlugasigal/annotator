import AppKit

enum SkillExportFailure: Error, LocalizedError {
    case missingResource
    case folderExists
    case invalidDestination

    var errorDescription: String? {
        switch self {
        case .missingResource: "The AI skill is missing from this app. Install the latest build and try again."
        case .folderExists: "An annotator folder already exists there. Choose another folder so your files are not replaced."
        case .invalidDestination: "Choose a folder on this Mac for the skill."
        }
    }
}

enum ComputerUseSkill {
    static func export(to directory: URL, bundle: Bundle = .main) throws -> URL {
        guard directory.isFileURL,
              try directory.resourceValues(forKeys: [.isDirectoryKey]).isDirectory == true else {
            throw SkillExportFailure.invalidDestination
        }
        guard let source = bundle.url(forResource: "SKILL", withExtension: "md", subdirectory: "annotator") else {
            throw SkillExportFailure.missingResource
        }
        let data = try Data(contentsOf: source)
        let destination = directory.appendingPathComponent("annotator", isDirectory: true)
        guard !FileManager.default.fileExists(atPath: destination.path) else { throw SkillExportFailure.folderExists }
        try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: false)
        try data.write(to: destination.appendingPathComponent("SKILL.md"), options: .withoutOverwriting)
        return destination
    }
}

extension WorkspaceModel {
    func exportComputerUseSkill() {
        let dialog = NSOpenPanel()
        dialog.title = "Save AI skill"
        dialog.message = "Choose where to save the annotator skill folder."
        dialog.prompt = "Save here"
        dialog.canChooseFiles = false
        dialog.canChooseDirectories = true
        dialog.canCreateDirectories = true
        dialog.allowsMultipleSelection = false
        guard dialog.runModal() == .OK, let directory = dialog.url else { return }
        do {
            let saved = try ComputerUseSkill.export(to: directory)
            status = "Saved the skill to \(saved.path). Open Setup guide under Help > Use an AI assistant."
        } catch { report(error) }
    }
}
