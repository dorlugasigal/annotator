import AppKit
import QuartzCore
import XCTest
import AnnotationCore
@testable import AnnotatorApp

@MainActor
final class CornerRoutesTests: XCTestCase {
    func testNearestAssignmentsAreInsetAndRemainOnEachDisplay() throws {
        for count in [0, 1, 2, 3, 4, 5, 9, 63, 64] {
            var routes = CornerRoutes()
            let screens = [CGRect(x: -1400, y: 80, width: 1400, height: 900),
                           CGRect(x: 0, y: -200, width: 1200, height: 1000)]
            let items = screens.enumerated().flatMap { display, frame in
                (0..<count).map {
                    CornerRoutes.Item(id: "\(display)-\($0)", displayID: UInt32(display), visibleFrame: frame,
                                      center: CornerRoutes.point(corner: $0 % 4, in: frame))
                }
            }
            routes.update(items)
            XCTAssertEqual(routes.destinations.count, count * screens.count)
            for (display, frame) in screens.enumerated() {
                for (index, item) in items.filter({ $0.displayID == UInt32(display) }).enumerated() {
                    let destination = try XCTUnwrap(routes.destinations[item.id])
                    XCTAssertTrue(frame.contains(destination.point))
                    XCTAssertGreaterThan(destination.point.x, frame.minX)
                    XCTAssertLessThan(destination.point.y, frame.maxY)
                    XCTAssertEqual(destination.corner, index % 4)
                }
            }
        }
    }

    func testPairReusesAssignmentsButNewMembershipOrLayoutRebuilds() {
        var routes = CornerRoutes()
        let frame = CGRect(x: 0, y: 0, width: 1200, height: 900)
        let items = (0..<6).map {
            CornerRoutes.Item(id: "\($0)", displayID: 1, visibleFrame: frame, center: CGPoint(x: 50 + $0 * 5, y: 850))
        }
        routes.update(items)
        let original = routes.destinations.mapValues(\.point)
        XCTAssertTrue(routes.destinations.values.allSatisfy { $0.corner == 0 }, "Nearby shapes may share the nearest corner.")
        routes.update(Array(items.reversed()))
        XCTAssertEqual(routes.destinations.mapValues(\.point), original)
        routes.update(Array(items.dropLast()))
        XCTAssertNil(routes.destinations["5"])
        let moved = items.map {
            CornerRoutes.Item(id: $0.id, displayID: 1, visibleFrame: frame.offsetBy(dx: -1300, dy: 0),
                              center: CGPoint(x: $0.center.x - 1300, y: $0.center.y))
        }
        routes.update(moved)
        XCTAssertTrue(routes.destinations.values.allSatisfy { $0.point.x < 0 })
        routes.update([CornerRoutes.Item(id: "moved", displayID: 1, visibleFrame: frame, center: CGPoint(x: 1150, y: 50))])
        XCTAssertEqual(routes.destinations["moved"]?.corner, 3)
        routes.update([CornerRoutes.Item(id: "tie", displayID: 1, visibleFrame: frame,
                                        center: CGPoint(x: frame.midX, y: frame.midY))])
        XCTAssertEqual(routes.destinations["tie"]?.corner, 0)
    }

    func testSemiDirectArcRetracesTheSameRouteWithoutBanking() throws {
        let layer = CALayer()
        layer.bounds = CGRect(x: 0, y: 0, width: 120, height: 80)
        var hidden = CATransform3DMakeScale(0.1, 0.1, 1)
        hidden.m41 = -600; hidden.m42 = 400
        func frames(appearing: Bool) throws -> [CATransform3D] {
            let motion = WorkspaceLayerMotion(layer: layer, style: .corners, appearing: appearing, flipped: false,
                duration: 0.4, hiddenTransform: hidden, curveSign: appearing ? -1 : 1)
            defer { motion.cancel() }
            let group = try XCTUnwrap(layer.animation(forKey: WorkspaceLayerMotion.animationKey) as? CAAnimationGroup)
            let path = try XCTUnwrap(group.animations?.first as? CAKeyframeAnimation)
            return try XCTUnwrap(path.values as? [NSValue]).map(\.caTransform3DValue)
        }
        let hiding = try frames(appearing: false), showing = try frames(appearing: true)
        for (out, back) in zip(hiding, showing.reversed()) {
            XCTAssertEqual(out.m41, back.m41, accuracy: 0.000001)
            XCTAssertEqual(out.m42, back.m42, accuracy: 0.000001)
            XCTAssertEqual(out.m11, out.m22)
            XCTAssertEqual(out.m12, 0)
        }
        XCTAssertTrue(CATransform3DIsIdentity(hiding[0]))
        XCTAssertEqual(hiding.last?.m41, hidden.m41)
    }

    func testCornerLifecycleReusesTargetsAndRebuildsChangedContent() async throws {
        let suite = "corners-lifecycle-\(UUID())"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        let model = WorkspaceModel(defaults: defaults)
        model.beginDrawing()
        let session = try XCTUnwrap(model.active)
        for index in 0..<5 {
            try session.document.add(Element(tool: .rectangle,
                frame: Rect(x: 60 + Double(index) * 15, y: 100 + Double(index) * 15, width: 40, height: 40)))
        }
        let original = session.document.elements
        let toolbar = NSPanel(contentRect: CGRect(x: 100, y: 100, width: 600, height: 120),
                              styleMask: [.borderless], backing: .buffered, defer: false)
        let animator = WorkspaceVisibilityAnimator(reduceMotion: { false }, shuffled: { Array($0.reversed()) })
        defer {
            animator.cancel(); toolbar.orderOut(nil); model.closeAll()
            defaults.removePersistentDomain(forName: suite)
        }
        model.setAnimationStyle(.corners)
        animator.hide(toolbar: toolbar, model: model) {}
        let destinations = animator.transitionCornerTargets
        XCTAssertEqual(destinations.count, 5)
        XCTAssertEqual(Set(destinations.values.map { "\($0.x),\($0.y)" }).count, 1)
        try await Task.sleep(for: .seconds(1.1))
        animator.show(toolbar: toolbar, model: model) {}
        XCTAssertEqual(animator.transitionCornerTargets, destinations)
        XCTAssertEqual(animator.magicEffectLayerCount, 0)
        try await Task.sleep(for: .seconds(1.2))
        XCTAssertEqual(animator.transientPanelCount, 0)
        XCTAssertEqual(session.document.elements, original)
        animator.hide(toolbar: toolbar, model: model) {}
        let added = Element(tool: .ellipse, frame: Rect(x: 300, y: 400, width: 60, height: 60))
        try session.document.add(added)
        animator.show(toolbar: toolbar, model: model) {}
        XCTAssertEqual(animator.transitionCornerTargets.count, 6)
        XCTAssertNotNil(animator.transitionCornerTargets[added.id], "A stale snapshot must not hide newly added work.")
    }
}
