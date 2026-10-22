import Foundation
import XCTest
@testable import AnnotatorApp

@MainActor
final class UsageMetricsTests: XCTestCase {
    private var suite = ""
    private var defaults: UserDefaults!

    override func setUp() {
        suite = "usage-metrics-\(UUID())"
        defaults = UserDefaults(suiteName: suite)!
    }

    override func tearDown() {
        defaults.removePersistentDomain(forName: suite)
        defaults = nil
    }

    func testMetricsAreOffUntilConsentAndOptOutRemovesLocalState() {
        let metrics = UsageMetrics(defaults: defaults, endpoint: nil)
        XCTAssertFalse(metrics.enabled)
        metrics.record(.drawing)
        XCTAssertNil(defaults.data(forKey: UsageMetrics.daysKey))

        metrics.setEnabled(true)
        XCTAssertTrue(metrics.enabled)
        XCTAssertNotNil(defaults.string(forKey: UsageMetrics.installationKey))
        XCTAssertNotNil(defaults.data(forKey: UsageMetrics.daysKey))

        metrics.setEnabled(false)
        XCTAssertFalse(metrics.enabled)
        XCTAssertNil(defaults.string(forKey: UsageMetrics.installationKey))
        XCTAssertNil(defaults.data(forKey: UsageMetrics.daysKey))
        XCTAssertNil(defaults.string(forKey: UsageMetrics.sinceKey))
    }

    func testBucketsDoNotExposeExactLargeCounts() {
        XCTAssertEqual(UsageMetrics.bucket(0), "0")
        XCTAssertEqual(UsageMetrics.bucket(1), "1")
        XCTAssertEqual(UsageMetrics.bucket(5), "2-5")
        XCTAssertEqual(UsageMetrics.bucket(20), "6-20")
        XCTAssertEqual(UsageMetrics.bucket(500), "21+")
    }

    func testOnlyOneRequestIsAttemptedPerDayAndPayloadIsCoarse() async throws {
        let endpoint = try XCTUnwrap(URL(string: "https://metrics.example.test/"))
        var requests: [URLRequest] = []
        let response = try XCTUnwrap(HTTPURLResponse(
            url: endpoint, statusCode: 202, httpVersion: nil,
            headerFields: ["Content-Type": "application/json"]))
        let metrics = UsageMetrics(defaults: defaults, endpoint: endpoint, sendsAutomatically: false, send: { request in
            requests.append(request)
            return (Data(), response)
        })
        metrics.setEnabled(true)
        metrics.record(.drawing)
        metrics.record(.drawing)
        metrics.record(.imageInsertion)

        await metrics.sendDailySummaryIfNeeded()
        await metrics.sendDailySummaryIfNeeded()

        XCTAssertEqual(requests.count, 1)
        let body = try XCTUnwrap(requests.first?.httpBody)
        let payload = try JSONDecoder().decode(UsageMetricPayload.self, from: body)
        XCTAssertEqual(payload.schema, 1)
        XCTAssertEqual(payload.since, payload.days[0].day)
        XCTAssertEqual(payload.days.count, 1)
        XCTAssertEqual(payload.days[0].features[UsageMetric.drawing.rawValue], "2-5")
        XCTAssertEqual(payload.days[0].features[UsageMetric.imageInsertion.rawValue], "1")
        let json = String(decoding: body, as: UTF8.self)
        for forbidden in ["screenshot", "filename", "textValue", "geometry", "clipboard"] {
            XCTAssertFalse(json.contains(forbidden))
        }
    }

    func testFirstActivityOfANewDaySendsOnceWithoutRelaunching() async throws {
        let endpoint = try XCTUnwrap(URL(string: "https://metrics.example.test/"))
        var day = try XCTUnwrap(ISO8601DateFormatter().date(from: "2026-09-23T10:00:00Z"))
        var requests = 0
        let response = try XCTUnwrap(HTTPURLResponse(url: endpoint, statusCode: 202, httpVersion: nil, headerFields: nil))
        let metrics = UsageMetrics(defaults: defaults, endpoint: endpoint, now: { day }, send: { _ in
            requests += 1
            return (Data(), response)
        })
        metrics.setEnabled(true)
        metrics.record(.drawing)
        try await Task.sleep(for: .milliseconds(50))
        XCTAssertEqual(requests, 1)
        metrics.record(.zoom)
        try await Task.sleep(for: .milliseconds(50))
        XCTAssertEqual(requests, 1)
        day = day.addingTimeInterval(86_400)
        metrics.record(.drawing)
        try await Task.sleep(for: .milliseconds(50))
        XCTAssertEqual(requests, 2)
    }

    func testReenablingOnTheSameDayDoesNotSendAgain() async throws {
        let endpoint = try XCTUnwrap(URL(string: "https://metrics.example.test/"))
        var requests = 0
        let response = try XCTUnwrap(HTTPURLResponse(url: endpoint, statusCode: 202, httpVersion: nil, headerFields: nil))
        let metrics = UsageMetrics(defaults: defaults, endpoint: endpoint, sendsAutomatically: false, send: { _ in
            requests += 1
            return (Data(), response)
        })
        metrics.setEnabled(true)
        await metrics.sendDailySummaryIfNeeded()
        metrics.setEnabled(false)
        metrics.setEnabled(true)
        await metrics.sendDailySummaryIfNeeded()
        XCTAssertEqual(requests, 1)
    }

    func testDaysOutsideTheServiceWindowAreDroppedBeforeSending() async throws {
        let endpoint = try XCTUnwrap(URL(string: "https://metrics.example.test/"))
        var now = try XCTUnwrap(ISO8601DateFormatter().date(from: "2026-08-01T10:00:00Z"))
        var bodies: [Data] = []
        let response = try XCTUnwrap(HTTPURLResponse(url: endpoint, statusCode: 202, httpVersion: nil, headerFields: nil))
        let metrics = UsageMetrics(defaults: defaults, endpoint: endpoint, now: { now }, sendsAutomatically: false, send: { request in
            bodies.append(request.httpBody ?? Data())
            return (Data(), response)
        })
        metrics.setEnabled(true)
        metrics.record(.drawing)
        now = try XCTUnwrap(ISO8601DateFormatter().date(from: "2026-09-23T10:00:00Z"))
        metrics.record(.zoom)
        await metrics.sendDailySummaryIfNeeded()
        let payload = try JSONDecoder().decode(UsageMetricPayload.self, from: try XCTUnwrap(bodies.first))
        XCTAssertEqual(payload.days.map(\.day), ["2026-09-23"])
        XCTAssertEqual(payload.since, "2026-08-01")
    }

    func testInvalidOrMissingEndpointDoesNotMarkTheDayAttempted() async {
        let metrics = UsageMetrics(defaults: defaults, endpoint: nil)
        metrics.setEnabled(true)
        await metrics.sendDailySummaryIfNeeded()
        XCTAssertNil(defaults.string(forKey: UsageMetrics.lastAttemptKey))
        XCTAssertTrue(metrics.status.contains("isn't set up to send counts"))
    }

    func testProductionEndpointMustBeAnHTTPSOrigin() {
        XCTAssertEqual(UsageMetrics.validatedEndpoint("https://metrics.example.test")?.host, "metrics.example.test")
        for value in ["", "http://example.test", "https://user:pass@example.test", "https://example.test:444",
                      "https://example.test/elsewhere", "https://example.test/?secret=1", "$(ANNOTATOR_METRICS_URL)"] {
            XCTAssertNil(UsageMetrics.validatedEndpoint(value), value)
        }
    }

    func testOptOutDoesNotBecomeSentWhenAnOldRequestCompletes() async throws {
        let endpoint = try XCTUnwrap(URL(string: "https://metrics.example.test/"))
        let response = try XCTUnwrap(HTTPURLResponse(url: endpoint, statusCode: 202, httpVersion: nil, headerFields: nil))
        var reply: CheckedContinuation<(Data, URLResponse), any Error>?
        let metrics = UsageMetrics(defaults: defaults, endpoint: endpoint, sendsAutomatically: false, send: { _ in
            try await withCheckedThrowingContinuation { reply = $0 }
        })
        metrics.setEnabled(true)
        let task = Task { await metrics.sendDailySummaryIfNeeded() }
        for _ in 0..<50 {
            if reply != nil { break }
            await Task.yield()
        }
        let continuation = try XCTUnwrap(reply)
        metrics.setEnabled(false)
        let status = metrics.status
        continuation.resume(returning: (Data(), response))
        await task.value
        XCTAssertEqual(metrics.status, status)
        XCTAssertNil(defaults.data(forKey: UsageMetrics.daysKey))
        XCTAssertNil(defaults.string(forKey: UsageMetrics.installationKey))
    }

    func testCorruptQueueIsReportedAndNotOverwritten() {
        let metrics = UsageMetrics(defaults: defaults, endpoint: nil)
        metrics.setEnabled(true)
        let invalid = Data("invalid".utf8)
        defaults.set(invalid, forKey: UsageMetrics.daysKey)
        metrics.record(.drawing)
        XCTAssertEqual(defaults.data(forKey: UsageMetrics.daysKey), invalid)
        XCTAssertTrue(metrics.status.contains("couldn't be read"))
    }

    func testOptOutCancelsTheOwnedAutomaticRequestAfterSeveralEvents() async throws {
        let endpoint = try XCTUnwrap(URL(string: "https://metrics.example.test/"))
        let response = try XCTUnwrap(HTTPURLResponse(url: endpoint, statusCode: 202, httpVersion: nil, headerFields: nil))
        var started = false, cancelled = false
        let metrics = UsageMetrics(defaults: defaults, endpoint: endpoint, send: { _ in
            started = true
            do { try await Task.sleep(for: .seconds(10)) }
            catch { cancelled = Task.isCancelled; throw error }
            return (Data(), response)
        })
        metrics.setEnabled(true)
        metrics.record(.drawing)
        metrics.record(.zoom)
        for _ in 0..<50 {
            if started { break }
            await Task.yield()
        }
        XCTAssertTrue(started)
        metrics.setEnabled(false)
        for _ in 0..<50 {
            if cancelled { break }
            await Task.yield()
        }
        XCTAssertTrue(cancelled)
        XCTAssertFalse(metrics.enabled)
    }
}
