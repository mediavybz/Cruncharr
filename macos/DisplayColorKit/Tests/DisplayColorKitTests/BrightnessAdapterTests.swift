import Foundation
import XCTest
@testable import DisplayColorKit

private final class BrightnessAccessStub: BrightnessServiceAccess, @unchecked Sendable {
    private let lock = NSLock()
    var supported = true
    var injectedError: DisplayColorError?
    private var written: [Float] = []

    func supportsBrightness(for display: DisplayRecord) throws -> Bool {
        if let injectedError { throw injectedError }
        return supported
    }

    func writeBrightness(_ value: Float, for display: DisplayRecord) throws {
        if let injectedError { throw injectedError }
        lock.lock()
        written.append(value)
        lock.unlock()
    }

    func values() -> [Float] {
        lock.lock()
        defer { lock.unlock() }
        return written
    }
}

final class BrightnessAdapterTests: XCTestCase {
    func testNoServiceIsNormalUnsupportedCapability() async throws {
        let identity = try XCTUnwrap(DisplayIdentity(rawValue: "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"))
        let access = BrightnessAccessStub()
        access.supported = false
        let adapter = IOKitBrightnessAdapter(serviceAccess: access)

        XCTAssertFalse(try await adapter.supportsBrightness(for: makeDisplay(identity: identity)))
    }

    func testAmbiguousServiceErrorIsNotHidden() async throws {
        let identity = try XCTUnwrap(DisplayIdentity(rawValue: "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"))
        let access = BrightnessAccessStub()
        access.injectedError = .ambiguousDisplayService(identity, matches: 2)
        let adapter = IOKitBrightnessAdapter(serviceAccess: access)

        do {
            _ = try await adapter.supportsBrightness(for: makeDisplay(identity: identity))
            XCTFail("Expected ambiguity error")
        } catch let error as DisplayColorError {
            XCTAssertEqual(error, .ambiguousDisplayService(identity, matches: 2))
        }
    }

    func testExactServiceWritesClampedValues() async throws {
        let identity = try XCTUnwrap(DisplayIdentity(rawValue: "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"))
        let access = BrightnessAccessStub()
        let adapter = IOKitBrightnessAdapter(serviceAccess: access)
        let display = makeDisplay(identity: identity)

        try await adapter.setBrightness(-1, for: display)
        try await adapter.setBrightness(2, for: display)
        XCTAssertEqual(access.values(), [0, 1])
    }

    func testNonfiniteBrightnessIsRejectedBeforeIO() async throws {
        let identity = try XCTUnwrap(DisplayIdentity(rawValue: "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"))
        let access = BrightnessAccessStub()
        let adapter = IOKitBrightnessAdapter(serviceAccess: access)

        await XCTAssertThrowsErrorAsync(try await adapter.setBrightness(.nan, for: makeDisplay(identity: identity)))
        XCTAssertEqual(access.values(), [])
    }
}
