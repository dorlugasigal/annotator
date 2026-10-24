import Foundation

/// The hands-on tutorial's steps in order. Raw values are stable identifiers shared with Windows (`TutorialStep`).
public enum TutorialStep: String, CaseIterable, Codable, Sendable {
    case draw, rectangle, shape, color, arrow, pen, move, undo, useApps, drawAgain, hide

    /// Every step in order.
    public static var all: [TutorialStep] { allCases }

    /// The goals, in the order the step asks for them.
    public var goals: [TutorialGoal] {
        switch self {
        case .draw: [.drawing]
        case .rectangle: [.rectangleTool]
        case .shape: [.shapeDrawn]
        case .color: [.colorChanged]
        case .arrow: [.arrowTool, .curvedStyle, .curvedArrowDrawn]
        case .pen: [.penTool, .penDrawn]
        case .move: [.selectTool, .moved]
        case .undo: [.undone]
        case .useApps: [.usingApps]
        case .drawAgain: [.drawing]
        case .hide: [.hidden, .shown]
        }
    }

    /// The step's position from 0.
    public var index: Int { Self.allCases.firstIndex(of: self) ?? 0 }
}

/// One thing a step waits for. A step has one to three goals, done in order. Every goal reads the workspace's state,
/// never how the person got there, so a key and a click both count.
public enum TutorialGoal: String, CaseIterable, Codable, Sendable {
    /// Drawing is on.
    case drawing
    /// The Rectangle is the tool.
    case rectangleTool
    /// A finished shape that was not there when the goal started.
    case shapeDrawn
    /// The stroke color differs from the goal's start, with the same tool.
    case colorChanged
    /// The Arrow is the tool.
    case arrowTool
    /// The Arrow is the tool and draws curved.
    case curvedStyle
    /// A new curved arrow with at least three points.
    case curvedArrowDrawn
    /// The Pen or Highlighter is the tool.
    case penTool
    /// A new freehand drawing, or a Smart Draw shape made while the Pen is the tool.
    case penDrawn
    /// Select is the tool.
    case selectTool
    /// A drawing that was there moved, resized or turned, while Select is the tool.
    case moved
    /// Undo ran.
    case undone
    /// Use apps: drawings stay visible and apps get the clicks.
    case usingApps
    /// Drawings and tools are hidden (or hiding).
    case hidden
    /// Drawings and tools are shown (or showing) again.
    case shown
}

/// The workspace mode as the tutorial sees it: Off, Interact (Use apps) and Draw.
public enum TutorialMode: String, Sendable {
    case off, useApps, draw
}

/// What the tutorial needs to know about one drawing.
public struct TutorialElement: Equatable, Sendable {
    public var id: UUID
    public var tool: Tool
    public var curved: Bool
    public var pointCount: Int
    public var frame: Rect
    public var rotation: Double

    public init(id: UUID, tool: Tool, curved: Bool, pointCount: Int, frame: Rect, rotation: Double) {
        self.id = id; self.tool = tool; self.curved = curved
        self.pointCount = pointCount; self.frame = frame; self.rotation = rotation
    }

    public init(_ element: Element) {
        self.init(id: element.id, tool: element.tool, curved: element.style.curved, pointCount: element.points.count,
                  frame: element.frame, rotation: element.rotation)
    }

    /// Whether two looks at a drawing found it in the same place: the same frame and turn.
    public func samePlace(_ other: TutorialElement) -> Bool { frame == other.frame && rotation == other.rotation }
}

/// One look at the workspace: the mode, whether drawings and tools are presented, the tool with its stroke color and
/// route, every drawing on every display, how many times Undo ran and whether an edit is in progress.
public struct TutorialObservation: Equatable, Sendable {
    public var mode: TutorialMode
    public var presented: Bool
    public var tool: Tool
    public var strokeColor: RGBA
    public var curved: Bool
    public var elements: [TutorialElement]
    public var undoSerial: UInt64
    public var editing: Bool

    public init(mode: TutorialMode, presented: Bool, tool: Tool, strokeColor: RGBA, curved: Bool,
                elements: [TutorialElement], undoSerial: UInt64, editing: Bool) {
        self.mode = mode; self.presented = presented; self.tool = tool; self.strokeColor = strokeColor
        self.curved = curved; self.elements = elements; self.undoSerial = undoSerial; self.editing = editing
    }
}

/// What one observation changed.
public enum TutorialChange: Sendable {
    /// Nothing new.
    case none
    /// A goal of the current step is done; the step goes on.
    case goal
    /// A step is done and the next one started. Several steps can finish in one observation.
    case step
    /// The last step is done.
    case finished
}

/// The tutorial's progress: the current step and goal, and what each goal compares with. A goal that waits for something
/// new (a drawing, a color, an Undo or a move) compares with the observation where it became current; a goal about state
/// (the mode or the tool) is done as soon as the state holds. When a goal is done, the next goal is checked against the
/// same observation, so a state that already holds counts at once, while anything new must come later.
public struct TutorialProgress: Sendable {
    public private(set) var step = TutorialStep.draw
    /// The current goal's position in the step's goals; equal to the goal count once the tutorial is finished.
    public private(set) var goalIndex = 0
    public private(set) var isFinished = false
    /// Steps finished so far, in order (skipped steps excluded).
    public private(set) var completed: [TutorialStep] = []
    /// Steps skipped so far, in order.
    public private(set) var skipped: [TutorialStep] = []
    private var baseline: TutorialObservation
    private var latest: TutorialObservation
    private var baselineIDs: Set<UUID> = []
    private var known: [UUID: TutorialElement] = [:]

    public init(_ start: TutorialObservation) {
        baseline = start
        latest = start
        rebase(start)
        settle(start)
    }

    /// The goal the step waits for, or nil once finished.
    public var currentGoal: TutorialGoal? { isFinished ? nil : step.goals[goalIndex] }

    /// Whether the current step's goal at `index` is done.
    public func isGoalDone(_ index: Int) -> Bool { isFinished || index < goalIndex }

    /// Takes a new look at the workspace and reports the furthest change it caused.
    @discardableResult
    public mutating func observe(_ observation: TutorialObservation) -> TutorialChange {
        if isFinished { return .finished }
        latest = observation
        let before = step
        let goalBefore = goalIndex
        if step.goals[goalIndex] == .colorChanged && observation.tool != baseline.tool {
            // Another tool shows its own color: compare with that one from now on.
            rebase(observation)
        }
        settle(observation)
        for element in observation.elements where known[element.id] == nil {
            known[element.id] = element
        }
        if isFinished { return .finished }
        if step != before { return .step }
        return goalIndex != goalBefore ? .goal : .none
    }

    /// Skips the current step; the next one starts from the latest observation.
    @discardableResult
    public mutating func skip() -> TutorialChange {
        if isFinished { return .finished }
        skipped.append(step)
        advance(latest)
        settle(latest)
        return isFinished ? .finished : .step
    }

    /// Checks goals against `observation` until one is not done yet.
    private mutating func settle(_ observation: TutorialObservation) {
        while !isFinished && isMet(step.goals[goalIndex], observation) {
            goalIndex += 1
            if goalIndex < step.goals.count {
                rebase(observation)
                continue
            }
            completed.append(step)
            advance(observation)
        }
    }

    private mutating func advance(_ observation: TutorialObservation) {
        let next = step.index + 1
        guard next < TutorialStep.allCases.count else {
            isFinished = true
            goalIndex = step.goals.count
            return
        }
        step = TutorialStep.allCases[next]
        goalIndex = 0
        rebase(observation)
    }

    private mutating func rebase(_ observation: TutorialObservation) {
        baseline = observation
        baselineIDs = Set(observation.elements.map(\.id))
        known = [:]
        for element in observation.elements { known[element.id] = element }
    }

    private func isMet(_ goal: TutorialGoal, _ now: TutorialObservation) -> Bool {
        switch goal {
        case .drawing: now.mode == .draw && now.presented
        case .rectangleTool: now.tool == .rectangle
        case .shapeDrawn: isNew(now, where: { $0.tool.isShape })
        case .colorChanged: now.tool == baseline.tool && !Self.sameColor(now.strokeColor, baseline.strokeColor)
        case .arrowTool: now.tool == .arrow
        case .curvedStyle: now.tool == .arrow && now.curved
        case .curvedArrowDrawn: isNew(now, where: { $0.tool == .arrow && $0.curved && $0.pointCount >= 3 })
        case .penTool: now.tool.isFreehand
        case .penDrawn: isNew(now, where: { $0.tool.isFreehand || (now.tool.isFreehand && $0.tool.isShape) })
        case .selectTool: now.tool == .select
        case .moved: now.tool == .select && !now.editing && hasMoved(now)
        case .undone: now.undoSerial > baseline.undoSerial
        case .usingApps: now.mode == .useApps && now.presented
        case .hidden: !now.presented
        case .shown: now.presented
        }
    }

    private func hasMoved(_ now: TutorialObservation) -> Bool {
        now.elements.contains { element in
            guard let before = known[element.id] else { return false }
            return !element.samePlace(before)
        }
    }

    /// A finished drawing that the goal's start did not have.
    private func isNew(_ now: TutorialObservation, where matches: (TutorialElement) -> Bool) -> Bool {
        !now.editing && now.elements.contains { !baselineIDs.contains($0.id) && matches($0) }
    }

    private static func sameColor(_ left: RGBA, _ right: RGBA) -> Bool {
        abs(left.red - right.red) < 0.002 && abs(left.green - right.green) < 0.002 &&
            abs(left.blue - right.blue) < 0.002 && abs(left.alpha - right.alpha) < 0.002
    }
}
