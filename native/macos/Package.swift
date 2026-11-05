// swift-tools-version: 5.9
import PackageDescription
let package = Package(name: "MousharingMac", platforms: [.macOS(.v14)], products: [.executable(name: "MousharingMac", targets: ["MousharingMac"])], targets: [.executableTarget(name: "MousharingMac"), .testTarget(name: "MousharingMacTests", dependencies: ["MousharingMac"])])
