// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "UsageBar",
    platforms: [
        .macOS(.v14),
    ],
    targets: [
        .executableTarget(
            name: "UsageBar",
            path: "Sources/UsageBar",
            resources: [
                .process("Resources"),
            ])
    ]
)
