// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "DisplayColorKit",
    platforms: [.macOS(.v12)],
    products: [
        .library(name: "DisplayColorKit", targets: ["DisplayColorKit"]),
        .executable(name: "display-colorctl", targets: ["DisplayColorCLI"]),
        .executable(name: "DisplayColorSample", targets: ["DisplayColorSampleApp"])
    ],
    targets: [
        .target(
            name: "DisplayColorKit",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("ColorSync"),
                .linkedFramework("CoreGraphics"),
                .linkedFramework("IOKit"),
                .linkedFramework("OSLog")
            ]
        ),
        .executableTarget(
            name: "DisplayColorCLI",
            dependencies: ["DisplayColorKit"]
        ),
        .executableTarget(
            name: "DisplayColorSampleApp",
            dependencies: ["DisplayColorKit"],
            linkerSettings: [.linkedFramework("AppKit")]
        ),
        .testTarget(
            name: "DisplayColorKitTests",
            dependencies: ["DisplayColorKit"]
        )
    ]
)
