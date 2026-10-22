import CryptoKit
import Foundation
import Metal

let usage = """
    Usage: swift run --package-path apps/macos ConformanceExport [--output DIR] [--check]

    Run from the repository root. Writes the macOS conformance corpus to DIR
    (default reference/conformance) with a manifest. With --check, exports in memory,
    compares the result with DIR and exits with 1 when any generated file differs.
    """

@MainActor func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(1)
}

var outputPath = "reference/conformance"
var checkOnly = false
var arguments = Array(CommandLine.arguments.dropFirst())
while !arguments.isEmpty {
    let argument = arguments.removeFirst()
    switch argument {
    case "--output":
        guard !arguments.isEmpty else { fail(usage) }
        outputPath = arguments.removeFirst()
    case "--check": checkOnly = true
    case "--help", "-h":
        print(usage)
        exit(0)
    default: fail("Unknown option \(argument).\n\n\(usage)")
    }
}

let fileManager = FileManager.default
guard fileManager.fileExists(atPath: "apps/macos/Package.swift"), fileManager.fileExists(atPath: "apps/macos/Sources/AnnotationCore") else {
    fail("Run ConformanceExport from the repository root.")
}

/// Everything whose content can change the corpus.
let sourcePaths = ["apps/macos/Package.swift", "apps/macos/Sources/AnnotationCore", "apps/macos/Sources/MotionCore", "apps/macos/Sources/PresentationRendering",
                   "apps/macos/Tools/Conformance"]

@MainActor func sourceFingerprint() throws -> String {
    var files: [String] = []
    for path in sourcePaths {
        var isDirectory: ObjCBool = false
        guard fileManager.fileExists(atPath: path, isDirectory: &isDirectory) else { continue }
        if !isDirectory.boolValue { files.append(path); continue }
        guard let enumerator = fileManager.enumerator(atPath: path) else { continue }
        for case let relative as String in enumerator where !relative.hasPrefix(".") && !relative.contains("/.") {
            var nested: ObjCBool = false
            let full = path + "/" + relative
            if fileManager.fileExists(atPath: full, isDirectory: &nested), !nested.boolValue { files.append(full) }
        }
    }
    var hasher = SHA256()
    for file in files.sorted() {
        let content = try Data(contentsOf: URL(fileURLWithPath: file))
        hasher.update(data: Data((file + "\n" + Corpus.sha256(content) + "\n").utf8))
    }
    return hasher.finalize().map { String(format: "%02x", $0) }.joined()
}

@MainActor func git(_ arguments: [String]) -> String? {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/git")
    process.arguments = arguments
    let pipe = Pipe()
    process.standardOutput = pipe
    process.standardError = FileHandle.nullDevice
    do { try process.run() } catch { return nil }
    let data = pipe.fileHandleForReading.readDataToEndOfFile()
    process.waitUntilExit()
    guard process.terminationStatus == 0 else { return nil }
    return String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines)
}

@MainActor func sysctlString(_ name: String) -> String {
    var size = 0
    guard sysctlbyname(name, nil, &size, nil, 0) == 0, size > 0 else { return "unknown" }
    var buffer = [CChar](repeating: 0, count: size)
    guard sysctlbyname(name, &buffer, &size, nil, 0) == 0 else { return "unknown" }
    return String(decoding: buffer.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }, as: UTF8.self)
}

let lastSourceCommit = git(["log", "-1", "--format=%H%n%cs", "--"] + sourcePaths)?.split(separator: "\n").map(String.init)
let dirty = !(git(["status", "--porcelain", "--"] + sourcePaths) ?? "").isEmpty
let version = ProcessInfo.processInfo.operatingSystemVersion
let source: J = [
    "commit": .string(lastSourceCommit?.first ?? "unknown"),
    "date": .string(lastSourceCommit?.dropFirst().first ?? "unknown"),
    "uncommittedChanges": .bool(dirty),
    "fingerprint": .string(try sourceFingerprint()),
    "paths": .array(sourcePaths.map { .string($0) })
]
let environment: J = [
    "macOS": .string("\(version.majorVersion).\(version.minorVersion).\(version.patchVersion)"),
    "build": .string(sysctlString("kern.osversion")),
    "machine": .string(sysctlString("hw.machine")),
    "cpu": .string(sysctlString("machdep.cpu.brand_string")),
    "gpu": .string(MTLCreateSystemDefaultDevice()?.name ?? "none")
]

let corpus = Corpus()
do {
    try PlatformExport.run(corpus)
    try BoardsExport.run(corpus)
    try ProfilesExport.run(corpus)
    try ShortcutsExport.run(corpus)
    try await LibraryExport.run(corpus)
    try EditingExport.run(corpus)
    try GeometryExport.run(corpus)
    try RecognitionExport.run(corpus)
    try HitTestExport.run(corpus)
    try MotionExport.run(corpus)
    try RenderExport.run(corpus)
    try EffectsExport.run(corpus)
} catch {
    fail("Export failed: \(error)")
}
let manifest = try corpus.manifest(source: source, environment: environment)
let root = URL(fileURLWithPath: outputPath, isDirectory: true)
/// Files in the output folder that people maintain by hand.
let handWritten: Set<String> = ["README.md"]

@MainActor func filesOnDisk() -> Set<String> {
    guard let enumerator = fileManager.enumerator(atPath: root.path) else { return [] }
    var result = Set<String>()
    for case let relative as String in enumerator {
        var isDirectory: ObjCBool = false
        if fileManager.fileExists(atPath: root.appendingPathComponent(relative).path, isDirectory: &isDirectory),
           !isDirectory.boolValue, !relative.hasSuffix(".DS_Store") { result.insert(relative) }
    }
    return result
}

if checkOnly {
    var problems: [String] = []
    let existing = filesOnDisk()
    for path in corpus.files.keys.sorted() {
        guard let data = fileManager.contents(atPath: root.appendingPathComponent(path).path) else {
            problems.append("missing  \(path)"); continue
        }
        if data != corpus.files[path] { problems.append("changed  \(path)") }
    }
    for path in existing.subtracting(corpus.files.keys).subtracting(handWritten).subtracting(["manifest.json"]).sorted() {
        problems.append("stale    \(path)")
    }
    @MainActor func comparable(_ data: Data?) -> NSDictionary? {
        guard let data, var object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        object["source"] = nil
        object["environment"] = nil
        return object as NSDictionary
    }
    let savedManifest = fileManager.contents(atPath: root.appendingPathComponent("manifest.json").path)
    if comparable(savedManifest) != comparable(manifest) { problems.append("changed  manifest.json") }
    else if savedManifest != manifest {
        print("note     manifest.json differs only in its source or environment fields")
    }
    if problems.isEmpty {
        print("Conformance corpus matches: \(corpus.files.count) files in \(root.path).")
        exit(0)
    }
    problems.forEach { print($0) }
    print("\(problems.count) difference(s). Regenerate with: make conformance")
    exit(1)
}

try fileManager.createDirectory(at: root, withIntermediateDirectories: true)
for item in try fileManager.contentsOfDirectory(atPath: root.path) where !handWritten.contains(item) {
    try fileManager.removeItem(at: root.appendingPathComponent(item))
}
for (path, data) in corpus.files.sorted(by: { $0.key < $1.key }) {
    let url = root.appendingPathComponent(path)
    try fileManager.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    try data.write(to: url)
}
try manifest.write(to: root.appendingPathComponent("manifest.json"))
let total = corpus.files.values.reduce(0) { $0 + $1.count } + manifest.count
print("Wrote \(corpus.files.count + 1) files (\(total / 1_024) KB) to \(root.path).")
if dirty { print("Note: the corpus sources have uncommitted changes; commit them and export again before committing.") }
