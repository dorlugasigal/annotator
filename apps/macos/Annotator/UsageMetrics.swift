import Foundation
import Combine
import OSLog

enum UsageMetric: String, CaseIterable, Codable {
    case drawing
    case editing
    case textOrShape
    case coverOrEffect
    case zoom
    case imageInsertion
    case boardOpen
    case boardSave
    case pngExport
    case reusableShape
}

struct UsageMetricDay: Codable, Equatable {
    let day: String
    var launches: Int
    var counts: [String: Int]
}

struct UsageMetricPayload: Codable, Equatable {
    let schema: Int
    let installation: String
    let since: String
    let version: String
    let build: String
    let osMajor: Int
    let architecture: String
    let days: [UsageMetricPayloadDay]
}

struct UsageMetricPayloadDay: Codable, Equatable {
    let day: String
    let launches: String
    let features: [String: String]
}

@MainActor
final class UsageMetrics: ObservableObject {
    static let enabledKey = "usage-metrics-enabled-v1"
    static let installationKey = "usage-metrics-installation-v1"
    static let daysKey = "usage-metrics-days-v1"
    static let lastAttemptKey = "usage-metrics-last-attempt-v1"
    static let sinceKey = "usage-metrics-since-v1"

    @Published private(set) var enabled: Bool
    @Published private(set) var status: String

    private let defaults: UserDefaults
    private let endpoint: URL?
    private let bundle: Bundle
    private let calendar: Calendar
    private let now: () -> Date
    private let send: (URLRequest) async throws -> (Data, URLResponse)
    private let sendsAutomatically: Bool
    private var sending: Task<Void, Never>?
    private var consentGeneration = UUID()
    private static let logger = Logger(subsystem: "com.dorlugasigal.annotator", category: "usage-metrics")

    init(defaults: UserDefaults = .standard, endpoint: URL? = UsageMetrics.configuredEndpoint(),
         bundle: Bundle = .main, calendar: Calendar = UsageMetrics.utcCalendar,
         now: @escaping () -> Date = Date.init, sendsAutomatically: Bool = true,
         send: @escaping (URLRequest) async throws -> (Data, URLResponse) = {
             let session = URLSession(configuration: .ephemeral)
             defer { session.finishTasksAndInvalidate() }
             return try await session.data(for: $0)
         }) {
        self.defaults = defaults
        self.endpoint = endpoint
        self.bundle = bundle
        self.calendar = calendar
        self.now = now
        self.send = send
        self.sendsAutomatically = sendsAutomatically
        let isEnabled = defaults.bool(forKey: Self.enabledKey)
        enabled = isEnabled
        status = isEnabled ? "Usage counts are on. Drawing content stays private." : "Usage counts are off."
    }

    static var utcCalendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        return calendar
    }

    static func configuredEndpoint(bundle: Bundle = .main) -> URL? {
        validatedEndpoint(bundle.object(forInfoDictionaryKey: "AnnotatorMetricsURL") as? String ?? "")
    }

    static func validatedEndpoint(_ input: String) -> URL? {
        let value = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !value.isEmpty, !value.contains("$("), let url = URL(string: value),
              url.scheme == "https", url.host?.isEmpty == false,
              url.user == nil, url.password == nil, url.port == nil,
              url.query == nil, url.fragment == nil, url.path.isEmpty || url.path == "/" else { return nil }
        return url
    }

    func setEnabled(_ value: Bool) {
        guard enabled != value else { return }
        consentGeneration = UUID()
        sending?.cancel()
        sending = nil
        enabled = value
        defaults.set(value, forKey: Self.enabledKey)
        if value {
            if defaults.string(forKey: Self.installationKey) == nil {
                defaults.set(UUID().uuidString.lowercased(), forKey: Self.installationKey)
                defaults.set(dayString(now()), forKey: Self.sinceKey)
            }
            recordLaunch()
            status = endpoint == nil
                ? "Sharing is on, but this build isn't set up to send counts, so nothing is sent."
                : "Usage counts are on. Drawing content stays private."
        } else {
            defaults.removeObject(forKey: Self.installationKey)
            defaults.removeObject(forKey: Self.daysKey)
            defaults.removeObject(forKey: Self.sinceKey)
            status = "Usage counts are off. The local ID and unsent counts were deleted."
        }
    }

    func recordLaunch() {
        guard enabled else { return }
        updateToday { $0.launches = min(21, $0.launches + 1) }
    }

    func record(_ metric: UsageMetric) {
        guard enabled else { return }
        updateToday { $0.counts[metric.rawValue] = min(21, $0.counts[metric.rawValue, default: 0] + 1) }
    }

    func sendDailySummaryIfNeeded() async {
        guard enabled, let endpoint else {
            if enabled { status = "Sharing is on, but this build isn't set up to send counts, so nothing is sent." }
            return
        }
        let today = dayString(now())
        let generation = consentGeneration
        guard defaults.string(forKey: Self.lastAttemptKey) != today else { return }
        guard let installation = defaults.string(forKey: Self.installationKey) else {
            status = "Usage counts need a new ID. Turn sharing off and on again."
            return
        }
        guard let savedDays = loadDays() else { return }
        let days = recentDays(savedDays, today: today)
        guard !days.isEmpty else { return }
        guard let since = defaults.string(forKey: Self.sinceKey) else {
            status = "The usage-count start date is missing. Turn sharing off and on again."
            return
        }
        defaults.set(today, forKey: Self.lastAttemptKey)
        let numeric = bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "unknown"
        let suffix = bundle.object(forInfoDictionaryKey: "AnnotatorPrerelease") as? String ?? ""
        let payload = UsageMetricPayload(
            schema: 1,
            installation: installation,
            since: since,
            version: numeric + (suffix.isEmpty ? "" : "-\(suffix)"),
            build: bundle.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "unknown",
            osMajor: ProcessInfo.processInfo.operatingSystemVersion.majorVersion,
            architecture: Self.architecture,
            days: days.map {
                UsageMetricPayloadDay(day: $0.day, launches: Self.bucket($0.launches),
                                      features: $0.counts.mapValues(Self.bucket))
            })
        do {
            var request = URLRequest(url: endpoint.appending(path: "api/metrics/app"))
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.timeoutInterval = 8
            request.httpBody = try JSONEncoder().encode(payload)
            try Task.checkCancellation()
            let (_, response) = try await send(request)
            guard enabled, generation == consentGeneration, !Task.isCancelled else { return }
            guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
                throw URLError(.badServerResponse)
            }
            status = "Usage counts were sent today."
        } catch {
            guard enabled, generation == consentGeneration, !Task.isCancelled else { return }
            Self.logger.error("Usage-count request failed: \(error.localizedDescription, privacy: .private)")
            status = "Counts couldn't be sent. We'll try on another day."
        }
    }

    private func updateToday(_ update: (inout UsageMetricDay) -> Void) {
        let today = dayString(now())
        guard var days = loadDays() else { return }
        if let index = days.firstIndex(where: { $0.day == today }) {
            update(&days[index])
        } else {
            var day = UsageMetricDay(day: today, launches: 0, counts: [:])
            update(&day)
            days.append(day)
        }
        days = recentDays(days, today: today)
        do {
            defaults.set(try JSONEncoder().encode(days), forKey: Self.daysKey)
        } catch {
            Self.logger.error("Usage counts could not be saved: \(error.localizedDescription, privacy: .private)")
            status = "Counts couldn't be saved. Turn sharing off and on again."
            return
        }
        // The app can stay open for days, so the first activity of a new day triggers that day's single send.
        if sendsAutomatically, sending == nil, endpoint != nil, defaults.string(forKey: Self.lastAttemptKey) != today {
            let generation = consentGeneration
            sending = Task { [weak self] in
                guard let self else { return }
                await sendDailySummaryIfNeeded()
                if consentGeneration == generation { sending = nil }
            }
        }
    }

    /// Keeps only days the service still accepts (today and the previous seven), so an old queue never blocks a send.
    private func recentDays(_ days: [UsageMetricDay], today: String) -> [UsageMetricDay] {
        let date = now().addingTimeInterval(-7 * 86_400)
        let earliest = dayString(date)
        return days.filter { $0.day >= earliest && $0.day <= today }.sorted { $0.day < $1.day }
    }

    private func loadDays() -> [UsageMetricDay]? {
        guard let data = defaults.data(forKey: Self.daysKey) else { return [] }
        do {
            return try JSONDecoder().decode([UsageMetricDay].self, from: data)
        } catch {
            Self.logger.error("Saved usage counts could not be read: \(error.localizedDescription, privacy: .private)")
            status = "Saved counts couldn't be read. Turn sharing off and on again."
            return nil
        }
    }

    private func dayString(_ date: Date) -> String {
        let components = calendar.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", components.year ?? 0, components.month ?? 0, components.day ?? 0)
    }

    static func bucket(_ value: Int) -> String {
        switch value {
        case ...0: "0"
        case 1: "1"
        case 2...5: "2-5"
        case 6...20: "6-20"
        default: "21+"
        }
    }

    static var architecture: String {
        #if arch(arm64)
        "arm64"
        #elseif arch(x86_64)
        "x86_64"
        #else
        "other"
        #endif
    }
}
