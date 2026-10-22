import Darwin
import Foundation

struct MemoryReading: Codable {
    let currentRSSBytes: UInt64
    let processPeakRSSBytes: UInt64
    let processCPUSeconds: Double

    static func read() throws -> Self {
        var info = mach_task_basic_info()
        var count = mach_msg_type_number_t(MemoryLayout<mach_task_basic_info>.size / MemoryLayout<integer_t>.size)
        let status = withUnsafeMutablePointer(to: &info) { pointer in
            pointer.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(MACH_TASK_BASIC_INFO), $0, &count)
            }
        }
        guard status == KERN_SUCCESS else { throw BenchmarkFailure.invalid("task_info failed: \(status)") }
        var usage = rusage()
        guard getrusage(RUSAGE_SELF, &usage) == 0, usage.ru_maxrss >= 0 else {
            throw BenchmarkFailure.invalid("getrusage failed: \(errno)")
        }
        func seconds(_ time: timeval) -> Double { Double(time.tv_sec) + Double(time.tv_usec) / 1_000_000 }
        return Self(currentRSSBytes: UInt64(info.resident_size),
                    processPeakRSSBytes: UInt64(usage.ru_maxrss),
                    processCPUSeconds: seconds(usage.ru_utime) + seconds(usage.ru_stime))
    }
}

struct Sample: Codable {
    let durationMS: Double
    let before: MemoryReading
    let after: MemoryReading
    let counters: [String: UInt64]
}

struct MemoryStage: Codable {
    let cycle: Int
    let stage: String
    let memory: MemoryReading
    var idleWallMS: Double?
    var idleCPUTimeMS: Double?
}

struct Result: Codable {
    let scenario: String
    let workload: [String: Int]
    let timingScope: String
    let warmupCount: Int
    let samples: [Sample]
    let p50MS: Double
    let p95MS: Double
    let memoryStages: [MemoryStage]
}

@MainActor
struct Job {
    let run: () throws -> Void
    let verify: () throws -> [String: UInt64]
}

@MainActor
func measure(_ name: String, workload: [String: Int], scope: String, samples: Int, warmup: Int,
             setup: () throws -> Job) throws -> Result {
    var results: [Sample] = []
    for index in 0..<(samples + warmup) {
        let sample = try autoreleasepool {
            let job = try setup()
            let before = try MemoryReading.read()
            let start = DispatchTime.now().uptimeNanoseconds
            try job.run()
            let end = DispatchTime.now().uptimeNanoseconds
            let after = try MemoryReading.read()
            let counters = try job.verify()
            return Sample(durationMS: Double(end - start) / 1_000_000, before: before, after: after, counters: counters)
        }
        if index >= warmup { results.append(sample) }
    }
    let sorted = results.map(\.durationMS).sorted()
    func percentile(_ p: Double) -> Double { sorted[Int(ceil(p * Double(sorted.count))) - 1] }
    return Result(scenario: name, workload: workload, timingScope: scope, warmupCount: warmup,
                  samples: results, p50MS: percentile(0.5), p95MS: percentile(0.95), memoryStages: [])
}
