// swift-tools-version: 5.10
import PackageDescription

let package = Package(
    name: "ShowTell",
    platforms: [.macOS(.v14)],
    dependencies: [
        // Parakeet speech-to-text on Core ML (Apple Neural Engine). Pinned: its API changes between releases.
        .package(url: "https://github.com/FluidInference/FluidAudio.git", exact: "0.12.4"),
    ],
    targets: [
        .executableTarget(
            name: "ShowTell",
            dependencies: [.product(name: "FluidAudio", package: "FluidAudio")],
            path: "Sources/ShowTell"
        ),
    ]
)
