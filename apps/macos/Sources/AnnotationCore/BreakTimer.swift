import Foundation

/// Saved choices for the break screen (specification 014). A missing key decodes to its default, so
/// profiles from older versions and partial profiles keep working.
public struct BreakTimerSettings: Codable, Equatable, Sendable {
    public enum Background: String, Codable, CaseIterable, Sendable {
        case moving, blur, plain

        public var label: String {
            switch self {
            case .moving: "Moving colors"
            case .blur: "Blurred desktop"
            case .plain: "Plain"
            }
        }
    }

    public enum Palette: String, Codable, CaseIterable, Sendable {
        case forest, ocean, sunset, berry, graphite

        public var label: String { rawValue.capitalized }
    }

    public static let minuteRange = 1...180
    public static let titleLimit = 60
    public static let quickMinutes = [5, 10, 15, 30]
    public static let defaultTitle = "Coffee break"

    public var minutes = 10
    public var title = BreakTimerSettings.defaultTitle
    public var background = Background.moving
    public var palette = Palette.forest
    public var showEndTime = true
    public var chime = true
    public var countOver = true
    public var allDisplays = true
    public var lockWhenTouched = true

    public init() {}

    private enum CodingKeys: String, CodingKey {
        case minutes, title, background, palette, showEndTime, chime, countOver, allDisplays, lockWhenTouched
    }

    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        let defaults = BreakTimerSettings()
        minutes = try values.decodeIfPresent(Int.self, forKey: .minutes) ?? defaults.minutes
        title = try values.decodeIfPresent(String.self, forKey: .title) ?? defaults.title
        background = try values.decodeIfPresent(Background.self, forKey: .background) ?? defaults.background
        palette = try values.decodeIfPresent(Palette.self, forKey: .palette) ?? defaults.palette
        showEndTime = try values.decodeIfPresent(Bool.self, forKey: .showEndTime) ?? defaults.showEndTime
        chime = try values.decodeIfPresent(Bool.self, forKey: .chime) ?? defaults.chime
        countOver = try values.decodeIfPresent(Bool.self, forKey: .countOver) ?? defaults.countOver
        allDisplays = try values.decodeIfPresent(Bool.self, forKey: .allDisplays) ?? defaults.allDisplays
        lockWhenTouched = try values.decodeIfPresent(Bool.self, forKey: .lockWhenTouched) ?? defaults.lockWhenTouched
    }

    public func validate() throws {
        guard Self.minuteRange.contains(minutes) else {
            throw CanvasError.invalidDocument("A break lasts 1 to 180 minutes.")
        }
        guard Self.isValidTitle(title) else {
            throw CanvasError.invalidDocument("The break title can have up to 60 characters on one line.")
        }
    }

    public static func isValidTitle(_ title: String) -> Bool {
        title.count <= titleLimit && !title.unicodeScalars.contains(where: isLineBreakOrControl)
    }

    /// Keeps what can be kept of typed text: one line, no control characters, at most 60 characters.
    public static func cleanTitle(_ text: String) -> String {
        let cleaned = text.map { character -> String in
            if character.unicodeScalars.contains(where: { CharacterSet.newlines.contains($0) }) { return " " }
            return character.unicodeScalars.contains(where: isLineBreakOrControl) ? "" : String(character)
        }.joined()
        return String(cleaned.prefix(titleLimit))
    }

    /// Emoji joiners are format characters, not control characters, so they stay allowed.
    private static func isLineBreakOrControl(_ scalar: Unicode.Scalar) -> Bool {
        CharacterSet.newlines.contains(scalar) || scalar.properties.generalCategory == .control
    }
}

/// The time left in a break. It follows the wall clock, so sleep and slow frames never make it drift.
public struct BreakCountdown: Equatable, Sendable {
    public static let maximumSeconds: TimeInterval = TimeInterval(BreakTimerSettings.minuteRange.upperBound * 60)

    /// The length the ring measures against. It grows when time is added.
    public private(set) var total: TimeInterval
    private var end: Date
    private var pausedRemaining: TimeInterval?

    public init(minutes: Int, start: Date) throws {
        guard BreakTimerSettings.minuteRange.contains(minutes), start.timeIntervalSinceReferenceDate.isFinite else {
            throw CanvasError.invalidDocument("A break lasts 1 to 180 minutes.")
        }
        total = TimeInterval(minutes * 60)
        end = start.addingTimeInterval(total)
    }

    public var isPaused: Bool { pausedRemaining != nil }

    /// Seconds left; negative once time is up.
    public func remaining(at now: Date) -> TimeInterval {
        pausedRemaining ?? end.timeIntervalSince(now)
    }

    public func isTimeUp(at now: Date) -> Bool { remaining(at: now) <= 0 }

    /// The part of the ring still filled, from 1 at the start to 0 at time up.
    public func fraction(at now: Date) -> Double {
        guard total > 0 else { return 0 }
        return min(max(remaining(at: now) / total, 0), 1)
    }

    /// When the break ends if it runs from now on. A paused break moves its end later as time passes.
    public func endDate(at now: Date) -> Date {
        now.addingTimeInterval(remaining(at: now))
    }

    public mutating func pause(at now: Date) {
        guard pausedRemaining == nil, remaining(at: now) > 0 else { return }
        pausedRemaining = remaining(at: now)
    }

    public mutating func resume(at now: Date) {
        guard let left = pausedRemaining else { return }
        end = now.addingTimeInterval(left)
        pausedRemaining = nil
    }

    public mutating func togglePause(at now: Date) {
        if isPaused { resume(at: now) } else { pause(at: now) }
    }

    /// Adds or removes whole minutes. Removing stops at zero, which counts as time up. Adding after time
    /// is up starts a fresh countdown, so the ring fills again.
    public mutating func adjust(minutes delta: Int, at now: Date) {
        // Once time is up there is nothing to remove, and the time over must keep counting.
        guard delta > 0 || remaining(at: now) > 0 else { return }
        let current = max(remaining(at: now), 0)
        let next = min(max(current + TimeInterval(delta * 60), 0), Self.maximumSeconds)
        if current == 0 && next > 0 { total = next }
        else if delta > 0 { total = min(max(total + TimeInterval(delta * 60), next), Self.maximumSeconds) }
        else { total = max(total, next) }
        if pausedRemaining != nil {
            pausedRemaining = next
            if next == 0 { pausedRemaining = nil; end = now }
        } else {
            end = now.addingTimeInterval(next)
        }
    }

    /// The countdown text: `9:59`, `1:05:00`, or the time over as `+1:23` when `countOver` is on.
    public func label(at now: Date, countOver: Bool) -> String {
        let left = remaining(at: now)
        guard left.isFinite else { return "0:00" }
        if left > 0 { return Self.clock(seconds: Int(left.rounded(.up))) }
        let over = Int((-left).rounded(.down))
        guard countOver, over > 0 else { return "0:00" }
        return "+" + Self.clock(seconds: over)
    }

    /// Seconds until `label(at:countOver:)` shows a different value.
    public func secondsUntilLabelChanges(at now: Date) -> TimeInterval {
        let left = remaining(at: now)
        guard left.isFinite, !isPaused else { return 1 }
        let step = left > 0 ? left - left.rounded(.down) : (-left).rounded(.up) - (-left)
        return step > 0.000_1 ? step : 1
    }

    public static func clock(seconds: Int) -> String {
        let value = max(seconds, 0)
        let hours = value / 3_600, minutes = (value % 3_600) / 60, rest = value % 60
        return hours > 0 ? String(format: "%d:%02d:%02d", hours, minutes, rest) : String(format: "%d:%02d", minutes, rest)
    }

    /// Words for VoiceOver, such as "1 hour 5 minutes" or "45 seconds".
    public static func spoken(seconds: Int) -> String {
        let value = max(seconds, 0)
        let hours = value / 3_600, minutes = (value % 3_600) / 60, rest = value % 60
        func unit(_ count: Int, _ name: String) -> String? { count > 0 ? "\(count) \(name)\(count == 1 ? "" : "s")" : nil }
        let parts = [unit(hours, "hour"), unit(minutes, "minute"), unit(rest, "second")].compactMap { $0 }
        return parts.isEmpty ? "0 seconds" : parts.joined(separator: " ")
    }
}
