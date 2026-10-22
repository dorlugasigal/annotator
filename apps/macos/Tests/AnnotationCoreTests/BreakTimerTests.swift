import Foundation
import Testing
@testable import AnnotationCore

private let start = Date(timeIntervalSinceReferenceDate: 800_000_000)

@Test func breakSettingsDefaultAndDecodeMissingKeys() throws {
    let defaults = BreakTimerSettings()
    #expect(defaults.minutes == 10)
    #expect(defaults.title == "Coffee break")
    #expect(defaults.background == .moving)
    #expect(defaults.palette == .forest)
    #expect(defaults.showEndTime && defaults.chime && defaults.countOver && defaults.allDisplays && defaults.lockWhenTouched)
    let partial = try JSONDecoder().decode(BreakTimerSettings.self, from: Data(#"{"minutes":25,"palette":"ocean"}"#.utf8))
    #expect(partial.minutes == 25)
    #expect(partial.palette == .ocean)
    #expect(partial.title == "Coffee break")
    #expect(partial.lockWhenTouched)
    let empty = try JSONDecoder().decode(BreakTimerSettings.self, from: Data("{}".utf8))
    #expect(empty == defaults)
}

@Test func breakSettingsRefuseOutOfRangeLengthsAndMultilineTitles() {
    var settings = BreakTimerSettings()
    for minutes in [0, -5, 181] {
        settings.minutes = minutes
        #expect(throws: CanvasError.invalidDocument("A break lasts 1 to 180 minutes.")) { try settings.validate() }
    }
    settings.minutes = 180
    #expect(throws: Never.self) { try settings.validate() }
    for title in ["Back\nsoon", "Tab\there", String(repeating: "a", count: 61)] {
        settings.title = title
        #expect(throws: CanvasError.invalidDocument("The break title can have up to 60 characters on one line.")) {
            try settings.validate()
        }
    }
    for title in ["", "Lunch 🍕", "👩‍🏫 Class starts again", String(repeating: "a", count: 60)] {
        settings.title = title
        #expect(throws: Never.self) { try settings.validate() }
    }
}

@Test func breakTitleCleaningKeepsOneLineAndEmojiSequences() {
    #expect(BreakTimerSettings.cleanTitle("Back\nat two") == "Back at two")
    #expect(BreakTimerSettings.cleanTitle("Tab\tgone") == "Tabgone")
    #expect(BreakTimerSettings.cleanTitle("👩‍🏫 Class") == "👩‍🏫 Class")
    #expect(BreakTimerSettings.cleanTitle(String(repeating: "b", count: 80)).count == 60)
}

@Test func breakSettingsAndShortcutRoundTripInProfiles() throws {
    var preferences = WorkspacePreferences()
    var settings = BreakTimerSettings()
    settings.minutes = 45
    settings.title = "Lunch"
    settings.background = .blur
    settings.palette = .sunset
    settings.lockWhenTouched = false
    preferences.breakTimer = settings
    preferences.breakShortcut = ActivationShortcut(keyCode: 11, character: "b", control: true, option: true)
    let restored = try WorkspacePreferences.read(preferences.data())
    #expect(restored == preferences)
    #expect(restored.breakTimer == settings)
}

@Test func profilesWithoutBreakSettingsWriteNoBreakKeys() throws {
    let json = try #require(String(data: WorkspacePreferences().data(), encoding: .utf8))
    #expect(!json.contains("breakTimer"))
    #expect(!json.contains("breakShortcut"))
}

@Test func invalidBreakSettingsRejectTheProfile() throws {
    var preferences = WorkspacePreferences()
    var settings = BreakTimerSettings()
    settings.minutes = 500
    preferences.breakTimer = settings
    #expect(throws: CanvasError.invalidDocument("A break lasts 1 to 180 minutes.")) {
        _ = try WorkspacePreferences.read(JSONEncoder().encode(preferences))
    }
}

@Test func breakShortcutMustDifferFromOtherGlobalShortcuts() {
    var preferences = WorkspacePreferences()
    preferences.breakShortcut = .liveZoom
    #expect(throws: CanvasError.invalidDocument("Break timer needs its own shortcut. Choose keys that no other global shortcut uses.")) {
        try preferences.validate()
    }
    preferences.breakShortcut = ActivationShortcut(keyCode: 0, character: "a")
    #expect(throws: (any Error).self) { try preferences.validate() }
    preferences.breakShortcut = nil
    #expect(throws: Never.self) { try preferences.validate() }
    #expect(ActivationShortcut.breakTimer.label == "⌃3")
}

@Test func olderProfileUsingControlThreeMovesTheBreakTimerToAFreeKey() throws {
    var preferences = WorkspacePreferences()
    preferences.zoomShortcut = .breakTimer
    let first = try WorkspacePreferences.read(JSONEncoder().encode(preferences))
    #expect(first.zoomShortcut == .breakTimer)
    #expect(first.breakShortcut?.label == "⌃⌥3")
    preferences.hideShortcut = ActivationShortcut(keyCode: 20, character: "3", control: true, option: true)
    preferences.resumeShortcut = ActivationShortcut(keyCode: 20, character: "3", control: true, shift: true)
    preferences.activationShortcut = ActivationShortcut(keyCode: 20, character: "3", control: true, command: true)
    let crowded = try WorkspacePreferences.read(JSONEncoder().encode(preferences))
    #expect(crowded.breakShortcut?.label == "⌃⌥⇧3")
    #expect(try WorkspacePreferences.read(WorkspacePreferences().data()).breakShortcut == nil)
}

@Test func toolLibraryRuleStillAppliesToTheBreakShortcut() throws {
    var preferences = WorkspacePreferences()
    preferences.breakShortcut = ActivationShortcut(keyCode: 37, character: "l", command: true)
    #expect(throws: Never.self) { try preferences.validate() }
}

@Test func countdownRoundsUpAndShowsHoursOnlyWhenNeeded() throws {
    let countdown = try BreakCountdown(minutes: 10, start: start)
    #expect(countdown.label(at: start, countOver: true) == "10:00")
    #expect(countdown.label(at: start.addingTimeInterval(0.4), countOver: true) == "10:00")
    #expect(countdown.label(at: start.addingTimeInterval(1), countOver: true) == "9:59")
    #expect(countdown.label(at: start.addingTimeInterval(599.5), countOver: true) == "0:01")
    #expect(countdown.label(at: start.addingTimeInterval(600), countOver: true) == "0:00")
    let long = try BreakCountdown(minutes: 65, start: start)
    #expect(long.label(at: start, countOver: true) == "1:05:00")
    #expect(BreakCountdown.clock(seconds: 3_599) == "59:59")
    #expect(BreakCountdown.clock(seconds: -4) == "0:00")
}

@Test func countdownCountsTimeOverOnlyWhenAsked() throws {
    let countdown = try BreakCountdown(minutes: 1, start: start)
    let later = start.addingTimeInterval(60 + 83.7)
    #expect(countdown.isTimeUp(at: later))
    #expect(countdown.label(at: later, countOver: true) == "+1:23")
    #expect(countdown.label(at: later, countOver: false) == "0:00")
    #expect(countdown.label(at: start.addingTimeInterval(60.5), countOver: true) == "0:00")
    #expect(countdown.fraction(at: later) == 0)
}

@Test func countdownFollowsTheWallClockAcrossSleep() throws {
    let countdown = try BreakCountdown(minutes: 15, start: start)
    let afterSleep = start.addingTimeInterval(9 * 60 + 30)
    #expect(countdown.label(at: afterSleep, countOver: true) == "5:30")
    #expect(countdown.endDate(at: afterSleep) == start.addingTimeInterval(15 * 60))
    #expect(abs(countdown.fraction(at: afterSleep) - 330.0 / 900.0) < 1e-12)
}

@Test func pauseHoldsTheTimeAndMovesTheEndLater() throws {
    var countdown = try BreakCountdown(minutes: 10, start: start)
    let pausedAt = start.addingTimeInterval(120)
    countdown.pause(at: pausedAt)
    #expect(countdown.isPaused)
    let muchLater = pausedAt.addingTimeInterval(300)
    #expect(countdown.label(at: muchLater, countOver: true) == "8:00")
    #expect(countdown.endDate(at: muchLater) == muchLater.addingTimeInterval(480))
    countdown.resume(at: muchLater)
    #expect(!countdown.isPaused)
    #expect(countdown.label(at: muchLater.addingTimeInterval(60), countOver: true) == "7:00")
    var finished = try BreakCountdown(minutes: 1, start: start)
    finished.pause(at: start.addingTimeInterval(70))
    #expect(!finished.isPaused)
}

@Test func addingAndRemovingMinutesStaysWithinLimits() throws {
    var countdown = try BreakCountdown(minutes: 10, start: start)
    let now = start.addingTimeInterval(480)
    countdown.adjust(minutes: 1, at: now)
    #expect(countdown.label(at: now, countOver: true) == "3:00")
    #expect(countdown.total == 660)
    countdown.adjust(minutes: -1, at: now)
    #expect(countdown.label(at: now, countOver: true) == "2:00")
    #expect(countdown.total == 660)
    countdown.adjust(minutes: -5, at: now)
    #expect(countdown.isTimeUp(at: now))
    #expect(countdown.label(at: now, countOver: true) == "0:00")
    countdown.adjust(minutes: 1, at: now.addingTimeInterval(200))
    #expect(countdown.label(at: now.addingTimeInterval(200), countOver: true) == "1:00")
    #expect(countdown.total == 60)
    #expect(countdown.fraction(at: now.addingTimeInterval(200)) == 1)
    var long = try BreakCountdown(minutes: 179, start: start)
    long.adjust(minutes: 5, at: start)
    #expect(long.remaining(at: start) == BreakCountdown.maximumSeconds)
    #expect(long.total == BreakCountdown.maximumSeconds)
}

@Test func removingTimeAfterTimeUpKeepsCountingOver() throws {
    var countdown = try BreakCountdown(minutes: 1, start: start)
    let late = start.addingTimeInterval(60 + 312)
    countdown.adjust(minutes: -1, at: late)
    #expect(countdown.label(at: late, countOver: true) == "+5:12")
    countdown.adjust(minutes: 2, at: late)
    #expect(countdown.label(at: late, countOver: true) == "2:00")
}

@Test func adjustingWhilePausedKeepsThePause() throws {
    var countdown = try BreakCountdown(minutes: 5, start: start)
    countdown.pause(at: start.addingTimeInterval(30))
    countdown.adjust(minutes: 2, at: start.addingTimeInterval(90))
    #expect(countdown.isPaused)
    #expect(countdown.label(at: start.addingTimeInterval(400), countOver: true) == "6:30")
    countdown.adjust(minutes: -10, at: start.addingTimeInterval(400))
    #expect(!countdown.isPaused)
    #expect(countdown.isTimeUp(at: start.addingTimeInterval(400)))
}

@Test func countdownRefusesInvalidLengths() {
    for minutes in [0, 181] {
        #expect(throws: (any Error).self) { _ = try BreakCountdown(minutes: minutes, start: start) }
    }
}

@Test func nextLabelChangeLinesUpWithTheDisplayedSecond() throws {
    let countdown = try BreakCountdown(minutes: 1, start: start)
    #expect(abs(countdown.secondsUntilLabelChanges(at: start.addingTimeInterval(0.25)) - 0.75) < 1e-6)
    #expect(countdown.secondsUntilLabelChanges(at: start) == 1)
    #expect(abs(countdown.secondsUntilLabelChanges(at: start.addingTimeInterval(61.4)) - 0.6) < 1e-6)
}

@Test func spokenTimeUsesPlainWords() {
    #expect(BreakCountdown.spoken(seconds: 0) == "0 seconds")
    #expect(BreakCountdown.spoken(seconds: 1) == "1 second")
    #expect(BreakCountdown.spoken(seconds: 600) == "10 minutes")
    #expect(BreakCountdown.spoken(seconds: 3_905) == "1 hour 5 minutes 5 seconds")
}
