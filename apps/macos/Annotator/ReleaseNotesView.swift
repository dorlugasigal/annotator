import Foundation
import OSLog
import SwiftUI

struct ReleaseNotesCatalog: Decodable {
    struct Entry: Decodable, Equatable {
        let version: String
        let build: Int
        let date: String
        let changes: [String: [String]]
        let knownIssues: [String]
    }

    static let categories = ["Added", "Changed", "Fixed", "Removed"]
    let schemaVersion: Int
    let releases: [Entry]

    static func current(data: Data, version: String, build: Int) throws -> Entry {
        let catalog = try JSONDecoder().decode(Self.self, from: data)
        guard catalog.schemaVersion == 1, !catalog.releases.isEmpty,
              Set(catalog.releases.map(\.version)).count == catalog.releases.count,
              Set(catalog.releases.map(\.build)).count == catalog.releases.count,
              catalog.releases.allSatisfy({ entry in
                  entry.build > 0 && !entry.version.isEmpty && !entry.date.isEmpty &&
                  !entry.changes.isEmpty && Set(entry.changes.keys).isSubset(of: Set(categories)) &&
                  entry.changes.values.allSatisfy { !$0.isEmpty && $0.allSatisfy { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty } }
              }) else { throw ReleaseNotesFailure.invalid }
        guard let entry = catalog.releases.first(where: { $0.version == version && $0.build == build }) else {
            throw ReleaseNotesFailure.missingVersion
        }
        return entry
    }

    static func load(bundle: Bundle = .main) throws -> Entry {
        guard let version = bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String,
              let buildString = bundle.object(forInfoDictionaryKey: "CFBundleVersion") as? String,
              let build = Int(buildString),
              let url = bundle.url(forResource: "notes", withExtension: "json") else {
            throw ReleaseNotesFailure.missingVersion
        }
        let suffix = bundle.object(forInfoDictionaryKey: "AnnotatorPrerelease") as? String ?? ""
        return try current(data: Data(contentsOf: url),
                           version: version + (suffix.isEmpty ? "" : "-\(suffix)"), build: build)
    }
}

enum ReleaseNotesFailure: Error, LocalizedError {
    case invalid, missingVersion
    var errorDescription: String? {
        switch self {
        case .invalid: "The release notes could not be read. Include the app version when reporting this problem."
        case .missingVersion: "Release notes are not available for this build. Include the app version when reporting this problem."
        }
    }
}

struct ReleaseNotesView: View {
    @State private var expanded = false
    @State private var entry: ReleaseNotesCatalog.Entry?
    @State private var failure: String?

    var body: some View {
        DisclosureGroup("What's changed", isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: 10) {
                Text("These notes came with this app. Opening them does not check for updates.")
                    .font(.caption).foregroundStyle(.secondary)
                if let entry {
                    Text("\(entry.version) (\(entry.build))").font(.headline)
                    Text(entry.date).font(.caption).foregroundStyle(.secondary)
                    ForEach(ReleaseNotesCatalog.categories, id: \.self) { category in
                        if let changes = entry.changes[category] {
                            Text(category).font(.subheadline.bold())
                            ForEach(Array(changes.enumerated()), id: \.offset) { _, item in
                                Text(item).fixedSize(horizontal: false, vertical: true)
                            }
                        }
                    }
                    if !entry.knownIssues.isEmpty {
                        Text("Known limits").font(.subheadline.bold())
                        ForEach(Array(entry.knownIssues.enumerated()), id: \.offset) { _, item in
                            Text(item).fixedSize(horizontal: false, vertical: true)
                        }
                    }
                } else if let failure {
                    Text(failure).foregroundStyle(.secondary)
                }
            }
            .font(.callout)
            .textSelection(.enabled)
            .padding(.vertical, 8)
        }
        .accessibilityIdentifier("release-notes")
        .onChange(of: expanded) { _, open in
            guard open, entry == nil else { return }
            do {
                entry = try ReleaseNotesCatalog.load()
                failure = nil
            } catch {
                Logger(subsystem: "com.dorlugasigal.annotator", category: "release-notes")
                    .error("Release notes failed: \(error.localizedDescription, privacy: .private)")
                failure = error is ReleaseNotesFailure ? error.localizedDescription :
                    ReleaseNotesFailure.invalid.localizedDescription
            }
        }
    }
}
