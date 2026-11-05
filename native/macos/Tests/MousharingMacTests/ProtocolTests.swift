import XCTest
import Foundation
@testable import MousharingMac
final class ProtocolTests:XCTestCase {
    func testSharedCryptographicFixtures() throws { let fallback=URL(fileURLWithPath:#filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("shared/fixtures.json").path;try ProtocolSelfTest.run(path:ProcessInfo.processInfo.environment["MOUSHARING_FIXTURES"] ?? fallback) }
    func testScreenEntryAndReturnPreserveFraction() { let a=ScreenRect(x:0,y:0,width:1440,height:900),b=ScreenRect(x:0,y:0,width:2560,height:1440);for side in ScreenSide.allCases { let p=b.point(side.opposite,0.25);XCTAssertEqual(a.ratio(side,a.point(side,b.ratio(side.opposite,p))),0.25,accuracy:0.0001) } }
    func testMalformedCanonicalBase64Rejected() { XCTAssertThrowsError(try decoded("Zg"));XCTAssertThrowsError(try decoded("Zh=="));XCTAssertEqual(try decoded("Zg=="),Data([102])) }
}
