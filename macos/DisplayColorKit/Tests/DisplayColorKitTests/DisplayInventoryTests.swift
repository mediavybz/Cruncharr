import Foundation
import XCTest
@testable import DisplayColorKit

private actor InventoryProfileAdapter: ColorProfileSystem {
    let states: [DisplayIdentity: ProfileState]
    init(states: [DisplayIdentity: ProfileState]) { self.states = states }

    func profileState(for display: DisplayIdentity) async throws -> ProfileState {
        guard let state = states[display] else { throw DisplayColorError.currentProfileMissing(display) }
        return state
    }

    func setCustomDefaultProfile(_ url: URL?, for display: DisplayIdentity) async throws {
        throw DisplayColorError.system("Inventory test adapter is read-only")
    }
}

final class DisplayInventoryTests: XCTestCase {
    func testZeroDisplaysReturnsEmptyInventory() async throws {
        let hardware = FakeDisplaySystem(records: [])
        let profiles = InventoryProfileAdapter(states: [:])
        let inventory = DisplayInventory(displays: hardware, profiles: profiles)
        let result = try await inventory.snapshot()
        XCTAssertEqual(result, [])
    }

    func testMultipleDisplaysKeepUUIDIdentityAndReceiveOwnProfiles() async throws {
        let first = try XCTUnwrap(DisplayIdentity(rawValue: "11111111-1111-1111-1111-111111111111"))
        let second = try XCTUnwrap(DisplayIdentity(rawValue: "22222222-2222-2222-2222-222222222222"))
        let firstURL = URL(fileURLWithPath: "/profiles/first.icc")
        let secondURL = URL(fileURLWithPath: "/profiles/second.icc")
        let firstProfile = ProfileRecord(url: firstURL, profileIdentifier: "one", description: nil, isCurrent: true, isDeviceDefault: true, isCustomAssignment: false)
        let secondProfile = ProfileRecord(url: secondURL, profileIdentifier: "two", description: nil, isCurrent: true, isDeviceDefault: true, isCustomAssignment: false)
        let hardware = FakeDisplaySystem(records: [
            makeDisplay(identity: second, id: 91),
            makeDisplay(identity: first, id: 17)
        ])
        let profiles = InventoryProfileAdapter(states: [
            first: ProfileState(profiles: [firstProfile], current: firstProfile, customDefaultURL: nil),
            second: ProfileState(profiles: [secondProfile], current: secondProfile, customDefaultURL: nil)
        ])
        let inventory = DisplayInventory(displays: hardware, profiles: profiles)

        let records = try await inventory.snapshot()
        XCTAssertEqual(records.map(\.identity), [second, first])
        XCTAssertEqual(records.map(\.displayID), [91, 17])
        XCTAssertEqual(records.map(\.currentProfileURL), [secondURL, firstURL])
    }

    func testDuplicateProductNamesDoNotReplaceStableIdentity() async throws {
        let first = try XCTUnwrap(DisplayIdentity(rawValue: "33333333-3333-3333-3333-333333333333"))
        let second = try XCTUnwrap(DisplayIdentity(rawValue: "44444444-4444-4444-4444-444444444444"))
        var firstDisplay = makeDisplay(identity: first, id: 7)
        var secondDisplay = makeDisplay(identity: second, id: 8)
        firstDisplay.localizedName = "Studio Display"
        secondDisplay.localizedName = "Studio Display"
        let url = URL(fileURLWithPath: "/profiles/shared.icc")
        let profile = ProfileRecord(url: url, profileIdentifier: nil, description: nil, isCurrent: true, isDeviceDefault: true, isCustomAssignment: false)
        let hardware = FakeDisplaySystem(records: [firstDisplay, secondDisplay])
        let profiles = InventoryProfileAdapter(states: [
            first: ProfileState(profiles: [profile], current: profile, customDefaultURL: nil),
            second: ProfileState(profiles: [profile], current: profile, customDefaultURL: nil)
        ])

        let records = try await DisplayInventory(displays: hardware, profiles: profiles).snapshot()
        XCTAssertEqual(Set(records.map(\.identity)), Set([first, second]))
    }
}
