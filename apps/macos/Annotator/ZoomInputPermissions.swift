import NativePlatform

@MainActor
struct ZoomInputPermissions {
    let isGranted: () -> Bool
    let request: () -> Bool

    static var system: Self {
        Self(isGranted: { ZoomGestureMonitor.hasPermission },
             request: { ZoomGestureMonitor.requestPermission() })
    }
}
