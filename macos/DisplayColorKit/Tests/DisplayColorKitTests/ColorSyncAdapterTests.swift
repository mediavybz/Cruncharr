import ColorSync
import Foundation
import XCTest
@testable import DisplayColorKit

final class ColorSyncAdapterTests: XCTestCase {
    func testMatchingEntryIsParsedWithTypedValues() throws {
        let uuid = try makeUUID("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE")
        let url = URL(fileURLWithPath: "/profiles/current.icc")
        let dictionary = try makeEntry(uuid: uuid, url: url, current: true)

        guard case .matching(let entry) = parseColorSyncProfileEntry(dictionary, requestedUUID: uuid) else {
            return XCTFail("Expected matching entry")
        }
        XCTAssertEqual(entry.url, url)
        XCTAssertTrue(entry.isCurrent)
        XCTAssertTrue(entry.isDeviceDefault)
    }

    func testOtherDeviceClassAndUUIDAreIgnored() throws {
        let requested = try makeUUID("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE")
        let other = try makeUUID("11111111-2222-3333-4444-555555555555")
        let classKey = try constant(kColorSyncDeviceClass) as String

        let otherClass = NSMutableDictionary(dictionary: try makeEntry(uuid: requested, url: URL(fileURLWithPath: "/a.icc")) as NSDictionary)
        otherClass[classKey] = "printer"
        XCTAssertEqual(parseColorSyncProfileEntry(otherClass as CFDictionary, requestedUUID: requested), .ignored)

        let otherUUID = try makeEntry(uuid: other, url: URL(fileURLWithPath: "/b.icc"))
        XCTAssertEqual(parseColorSyncProfileEntry(otherUUID, requestedUUID: requested), .ignored)
    }

    func testWrongTypesForEveryExpectedKeyAreMalformed() throws {
        let uuid = try makeUUID("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE")
        let valid = try makeEntry(uuid: uuid, url: URL(fileURLWithPath: "/valid.icc"))
        let keys = try [
            constant(kColorSyncDeviceClass),
            constant(kColorSyncDeviceID),
            constant(kColorSyncDeviceProfileURL),
            constant(kColorSyncDeviceProfileIsCurrent),
            constant(kColorSyncDeviceProfileID)
        ].map { $0 as String }

        for key in keys {
            let malformed = NSMutableDictionary(dictionary: valid as NSDictionary)
            malformed[key] = NSNumber(value: 42)
            guard case .malformed = parseColorSyncProfileEntry(malformed as CFDictionary, requestedUUID: uuid) else {
                return XCTFail("Expected malformed result for key \(key)")
            }
        }
    }

    func testStrictCurrentProfileRejectsMissingAndAmbiguousState() throws {
        let display = try XCTUnwrap(DisplayIdentity(rawValue: "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"))
        let first = ProfileRecord(
            url: URL(fileURLWithPath: "/one.icc"),
            profileIdentifier: "one",
            description: nil,
            isCurrent: false,
            isDeviceDefault: true,
            isCustomAssignment: false
        )
        XCTAssertThrowsError(try strictCurrentProfile(in: [first], display: display)) { error in
            XCTAssertEqual(error as? DisplayColorError, .currentProfileMissing(display))
        }
        let currentOne = ProfileRecord(
            url: first.url,
            profileIdentifier: first.profileIdentifier,
            description: nil,
            isCurrent: true,
            isDeviceDefault: true,
            isCustomAssignment: false
        )
        let currentTwo = ProfileRecord(
            url: URL(fileURLWithPath: "/two.icc"),
            profileIdentifier: "two",
            description: nil,
            isCurrent: true,
            isDeviceDefault: false,
            isCustomAssignment: true
        )
        XCTAssertThrowsError(try strictCurrentProfile(in: [currentOne, currentTwo], display: display)) { error in
            XCTAssertEqual(error as? DisplayColorError, .currentProfileAmbiguous(display, count: 2))
        }
        XCTAssertEqual(try strictCurrentProfile(in: [first, currentTwo], display: display), currentTwo)
    }

    func testFactorySelectionPrefersExplicitDefaultAndAnnotatesFallback() throws {
        let display = try XCTUnwrap(DisplayIdentity(rawValue: "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"))
        let explicit = ProfileRecord(
            url: URL(fileURLWithPath: "/factory.icc"),
            profileIdentifier: "default",
            description: nil,
            isCurrent: false,
            isDeviceDefault: true,
            isCustomAssignment: false
        )
        let fallback = ProfileRecord(
            url: URL(fileURLWithPath: "/associated.icc"),
            profileIdentifier: "associated",
            description: nil,
            isCurrent: true,
            isDeviceDefault: false,
            isCustomAssignment: false
        )
        XCTAssertEqual(try factoryProfileCandidate(in: [fallback, explicit], display: display), explicit)
        let inferred = try factoryProfileCandidate(in: [fallback], display: display)
        XCTAssertTrue(inferred.isFactoryCandidateInferred)
        XCTAssertEqual(inferred.url, fallback.url)

        let customOnly = ProfileRecord(
            url: URL(fileURLWithPath: "/custom.icc"),
            profileIdentifier: "custom",
            description: nil,
            isCurrent: true,
            isDeviceDefault: false,
            isCustomAssignment: true
        )
        XCTAssertThrowsError(try factoryProfileCandidate(in: [customOnly], display: display)) { error in
            XCTAssertEqual(error as? DisplayColorError, .factoryProfileUnavailable(display))
        }
    }

    func testAmbiguousExplicitFactoryProfilesAreRejected() throws {
        let display = try XCTUnwrap(DisplayIdentity(rawValue: "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"))
        let first = ProfileRecord(url: URL(fileURLWithPath: "/one.icc"), profileIdentifier: "one", description: nil, isCurrent: false, isDeviceDefault: true, isCustomAssignment: false)
        let second = ProfileRecord(url: URL(fileURLWithPath: "/two.icc"), profileIdentifier: "two", description: nil, isCurrent: false, isDeviceDefault: true, isCustomAssignment: false)
        XCTAssertThrowsError(try factoryProfileCandidate(in: [first, second], display: display)) { error in
            XCTAssertEqual(error as? DisplayColorError, .factoryProfileUnavailable(display))
        }
    }

    private func makeEntry(uuid: CFUUID, url: URL, current: Bool = true) throws -> CFDictionary {
        let dictionary = NSMutableDictionary()
        dictionary[try constant(kColorSyncDeviceClass) as String] = try constant(kColorSyncDisplayDeviceClass) as String
        dictionary[try constant(kColorSyncDeviceID) as String] = uuid
        dictionary[try constant(kColorSyncDeviceProfileURL) as String] = url as NSURL
        dictionary[try constant(kColorSyncDeviceProfileIsCurrent) as String] = current ? kCFBooleanTrue : kCFBooleanFalse
        dictionary[try constant(kColorSyncDeviceProfileID) as String] = try constant(kColorSyncDeviceDefaultProfileID) as String
        return dictionary as CFDictionary
    }

    private func makeUUID(_ value: String) throws -> CFUUID {
        try XCTUnwrap(CFUUIDCreateFromString(kCFAllocatorDefault, value as CFString))
    }

    private func constant(_ value: Unmanaged<CFString>?) throws -> CFString {
        try XCTUnwrap(value?.takeUnretainedValue())
    }
}
