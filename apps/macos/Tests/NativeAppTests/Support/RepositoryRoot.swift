import Foundation

/// The checkout's root folder: the nearest folder above this source file that holds `apps/macos/project.yml`.
enum RepositoryRoot {
    static let url: URL = {
        var directory = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        while !FileManager.default.fileExists(atPath: directory.appendingPathComponent("apps/macos/project.yml").path) {
            let parent = directory.deletingLastPathComponent()
            precondition(parent.path != directory.path, "No apps/macos/project.yml above \(#filePath).")
            directory = parent
        }
        return directory
    }()
}
