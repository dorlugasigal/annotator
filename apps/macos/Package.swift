// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "Annotator",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "AnnotationCore", targets: ["AnnotationCore"]),
        .library(name: "MotionCore", targets: ["MotionCore"]),
        .library(name: "PresentationRendering", targets: ["PresentationRendering"]),
        .library(name: "NativePlatform", targets: ["NativePlatform"]),
        .executable(name: "AnnotatorDiagnostics", targets: ["AnnotatorDiagnostics"]),
        .executable(name: "PerformanceBenchmarks", targets: ["PerformanceBenchmarks"]),
        .executable(name: "ConformanceExport", targets: ["ConformanceExport"])
    ],
    targets: [
        .target(name: "AnnotationCore"),
        .target(name: "MotionCore", dependencies: ["AnnotationCore"]),
        .target(name: "PresentationRendering", dependencies: ["AnnotationCore"]),
        .target(name: "NativePlatform", dependencies: ["AnnotationCore"]),
        .executableTarget(name: "AnnotatorDiagnostics",
                          dependencies: ["AnnotationCore", "NativePlatform", "PresentationRendering"],
                          path: "Tools/Diagnostics"),
        .executableTarget(name: "ConformanceExport",
                          dependencies: ["AnnotationCore", "MotionCore", "PresentationRendering"],
                          path: "Tools/Conformance",
                          exclude: ["README.md"]),
        .executableTarget(name: "PerformanceBenchmarks",
                          dependencies: ["AnnotationCore", "PresentationRendering"],
                          exclude: ["README.md"]),
        .testTarget(name: "AnnotationCoreTests", dependencies: ["AnnotationCore"]),
        .testTarget(name: "MotionCoreTests", dependencies: ["MotionCore", "AnnotationCore"]),
        .testTarget(name: "RenderingTests", dependencies: ["AnnotationCore", "PresentationRendering"], resources: [.process("Fixtures")]),
        .testTarget(name: "CaptureTests", dependencies: ["AnnotationCore", "PresentationRendering", "NativePlatform"])
    ],
    swiftLanguageModes: [.v6]
)
