import CryptoKit
import Foundation

struct Configuration {
    var samples = 11
    var warmup = 2
    var output = "build/performance/baseline.json"
    var worker: Scenario?

    init(_ arguments: [String]) throws {
        var index = 0
        while index < arguments.count {
            let flag = arguments[index]
            try require(index + 1 < arguments.count, "Missing value for \(flag).")
            let value = arguments[index + 1]
            switch flag {
            case "--samples":
                guard let count = Int(value), (3...101).contains(count) else {
                    throw BenchmarkFailure.invalid("--samples must be in 3...101.")
                }
                samples = count
            case "--warmup":
                guard let count = Int(value), (1...20).contains(count) else {
                    throw BenchmarkFailure.invalid("--warmup must be in 1...20.")
                }
                warmup = count
            case "--output": output = value
            case "--worker":
                guard let scenario = Scenario(rawValue: value) else { throw BenchmarkFailure.invalid("Unknown scenario \(value).") }
                worker = scenario
            default: throw BenchmarkFailure.invalid("Unknown option \(flag).")
            }
            index += 2
        }
    }
}

struct Report: Codable {
    let schemaVersion: Int
    let startedAt: String
    let completedAt: String
    let environment: [String: String]
    let measurementContract: [String: String]
    let unmeasured: [String]
    let results: [Result]
}

@main
struct PerformanceBenchmarks {
    @MainActor
    static func main() {
        do { try execute() }
        catch {
            FileHandle.standardError.write(Data("Benchmark failed: \(error)\n".utf8))
            exit(1)
        }
    }

    static func command(_ executable: String, _ arguments: [String]) throws -> Data {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.standardError
        try process.run()
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        try require(process.terminationReason == .exit && process.terminationStatus == 0,
                    "\(executable) \(arguments.joined(separator: " ")) exited \(process.terminationStatus).")
        return data
    }

    static func textCommand(_ executable: String, _ arguments: [String]) throws -> String {
        String(decoding: try command(executable, arguments), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func sourceFingerprint() throws -> String {
        var files = ["apps/macos/Package.swift", "tools/macos/benchmark.sh"]
        for path in ["apps/macos/Sources/AnnotationCore", "apps/macos/Sources/PresentationRendering", "apps/macos/Sources/PerformanceBenchmarks"] {
            guard let enumerator = FileManager.default.enumerator(atPath: path) else {
                throw BenchmarkFailure.invalid("Run from the annotation repository root: missing \(path).")
            }
            for case let file as String in enumerator where file.hasSuffix(".swift") { files.append("\(path)/\(file)") }
        }
        var hash = SHA256()
        for path in files.sorted() {
            hash.update(data: Data("\(path)\0".utf8))
            hash.update(data: try Data(contentsOf: URL(fileURLWithPath: path)))
        }
        return hash.finalize().map { String(format: "%02x", $0) }.joined()
    }

    @MainActor
    static func execute() throws {
        #if DEBUG
        throw BenchmarkFailure.invalid("Benchmarks require swift build -c release.")
        #else
        let config = try Configuration(Array(CommandLine.arguments.dropFirst()))
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        if let scenario = config.worker {
            let result = try Workloads.run(scenario, samples: config.samples, warmup: config.warmup)
            FileHandle.standardOutput.write(try encoder.encode(result))
            return
        }
        let started = ISO8601DateFormatter().string(from: Date())
        let fingerprint = try sourceFingerprint()
        let environment: [String: String] = [
            "hardwareModel": try textCommand("/usr/sbin/sysctl", ["-n", "hw.model"]),
            "cpu": try textCommand("/usr/sbin/sysctl", ["-n", "machdep.cpu.brand_string"]),
            "architecture": try textCommand("/usr/bin/uname", ["-m"]),
            "logicalProcessors": "\(ProcessInfo.processInfo.processorCount)",
            "physicalMemoryBytes": "\(ProcessInfo.processInfo.physicalMemory)",
            "os": ProcessInfo.processInfo.operatingSystemVersionString,
            "osBuild": try textCommand("/usr/sbin/sysctl", ["-n", "kern.osversion"]),
            "toolchain": try textCommand("/usr/bin/xcrun", ["swift", "--version"]),
            "build": "release",
            "gitRevision": try textCommand("/usr/bin/git", ["rev-parse", "HEAD"]),
            "gitDirty": try textCommand("/usr/bin/git", ["status", "--porcelain"]).isEmpty ? "false" : "true",
            "measuredSourcesSHA256": fingerprint,
            "fixtureSeed": "\(Fixtures.seed)",
            "swiftDeterministicHashing": ProcessInfo.processInfo.environment["SWIFT_DETERMINISTIC_HASHING"] ?? "unset",
            "processIsolation": "One new worker per scenario; setup and warmup share that worker.",
            "machineIsolation": "Other processes, scheduling and thermal conditions uncontrolled.",
            "executable": URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL.path
        ]
        var results: [Result] = []
        for scenario in Scenario.allCases {
            let data = try command(environment["executable"]!, [
                "--worker", scenario.rawValue, "--samples", "\(config.samples)", "--warmup", "\(config.warmup)"
            ])
            let result = try JSONDecoder().decode(Result.self, from: data)
            try require(result.scenario == scenario.rawValue && result.samples.count == config.samples,
                        "Worker returned an invalid result contract.")
            for sample in result.samples {
                try require(sample.durationMS.isFinite && sample.durationMS > 0
                            && sample.after.currentRSSBytes > 0
                            && sample.after.processPeakRSSBytes >= sample.after.currentRSSBytes,
                            "Worker returned invalid duration/RSS values.")
            }
            if scenario.rawValue.hasSuffix(".cached"),
               let cold = results.first(where: { $0.scenario == scenario.rawValue.replacingOccurrences(of: ".cached", with: ".cold") }) {
                try require(cold.samples.first?.counters["pixelChecksumFNV1a"]
                            == result.samples.first?.counters["pixelChecksumFNV1a"],
                            "Cold/cached worker pixels differ.")
            }
            results.append(result)
            let peak = result.samples.map(\.after.processPeakRSSBytes).max() ?? 0
            let row = String(format: "%-40@ p50 %9.3f ms  p95 %9.3f ms  peak RSS %7.1f MiB\n",
                             scenario.rawValue as NSString, result.p50MS, result.p95MS, Double(peak) / 1_048_576)
            FileHandle.standardError.write(Data(row.utf8))
        }
        let finalFingerprint = try sourceFingerprint()
        try require(finalFingerprint == fingerprint, "Measured sources changed during the run; discard this baseline.")
        let report = Report(schemaVersion: 1, startedAt: started,
                            completedAt: ISO8601DateFormatter().string(from: Date()), environment: environment,
                            measurementContract: [
                                "clock": "DispatchTime uptimeNanoseconds (monotonic); milliseconds.",
                                "percentile": "Nearest rank: sorted[ceil(p*n)-1]; p95 with 11 samples is the maximum.",
                                "warmup": "\(config.warmup) discarded samples, \(config.samples) measured per scenario.",
                                "rssCurrent": "task_info MACH_TASK_BASIC_INFO resident_size, bytes.",
                                "rssPeak": "getrusage RUSAGE_SELF ru_maxrss, Darwin bytes; cumulative worker high-water including fixtures/warmup.",
                                "rssLimits": "Before/after RSS is not operation-local peak or allocated bytes; retention is not leak proof.",
                                "idle": "Harness-only process CPU and RSS after explicit owned cleanup and 250 ms sleep.",
                                "scope": "Synthetic app-owned fixtures, CPU bitmap renderer and model/export APIs; no desktop/app interaction."
                            ],
                            unmeasured: [
                                "GPU/compositor presentation and display-link app-frame work: require signed native app signposts and GPU trace.",
                                "Physical input-to-photon: require synchronized instrumented input and displayed-pixel high-speed observation.",
                                "FPS, live capture, receiver protection, native input/observers/timers, battery/power: require dedicated native/physical acceptance.",
                                "Spec 003 <=8 ms app-frame / <=33 ms input-visible gates are not assessed by CLI bitmap durations."
                            ], results: results)
        let output = URL(fileURLWithPath: config.output).standardizedFileURL
        try FileManager.default.createDirectory(at: output.deletingLastPathComponent(), withIntermediateDirectories: true)
        try encoder.encode(report).write(to: output, options: .atomic)
        FileHandle.standardError.write(Data("Saved \(output.path)\n".utf8))
        #endif
    }
}
