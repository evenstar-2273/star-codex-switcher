// swift-tools-version: 6.2

import PackageDescription

let package = Package(
    name: "CodexAccountSwitcher",
    products: [
        .library(name: "SwitcherCore", targets: ["SwitcherCore"]),
        .executable(name: "SwitcherHost", targets: ["SwitcherHost"]),
    ],
    targets: [
        .target(name: "SwitcherPlatform", linkerSettings: [
            .linkedLibrary("advapi32", .when(platforms: [.windows])),
            .linkedLibrary("ole32", .when(platforms: [.windows])),
            .linkedLibrary("bcrypt", .when(platforms: [.windows])),
        ]),
        .target(name: "SwitcherCore", dependencies: [
            .target(name: "SwitcherPlatform", condition: .when(platforms: [.windows])),
        ]),
        .executableTarget(name: "SwitcherHost", dependencies: ["SwitcherCore"]),
        .testTarget(name: "SwitcherCoreTests", dependencies: ["SwitcherCore"]),
    ]
)
