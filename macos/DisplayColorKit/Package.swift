// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "DisplayColorKit",
    platforms: [.macOS(.v12)],
    products: [
        .library(name: "DisplayColorKit", targets: ["DisplayColorKit"]),
        .executable(name: "display-colorctl", targets: ["DisplayColorCLI"])
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
        .testTarget(
            name: "DisplayColorKitTests",
            dependencies: ["DisplayColorKit"]
        )
    ]
)
