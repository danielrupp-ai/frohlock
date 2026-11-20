// swift-tools-version:5.9
// Kernlogik von FrohLock iOS als eigenes Paket – damit sie am Mac mit `swift test` geprüft werden kann.
// Die App-Ziele (App, Monitor, Shield) binden dieselben Quelldateien direkt ein (project.yml).
import PackageDescription

let package = Package(
    name: "FrohLockCore",
    platforms: [.iOS(.v16), .macOS(.v13)],
    products: [.library(name: "FrohLockCore", targets: ["FrohLockCore"])],
    targets: [
        .target(name: "FrohLockCore", path: "Sources/FrohLockCore"),
        .testTarget(name: "FrohLockCoreTests", dependencies: ["FrohLockCore"], path: "Tests/FrohLockCoreTests",
                    resources: [.copy("Resources/xlang.json")]),
    ]
)
