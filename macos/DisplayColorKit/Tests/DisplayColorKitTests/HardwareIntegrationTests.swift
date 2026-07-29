import Foundation
import XCTest
@testable import DisplayColorKit

final class HardwareIntegrationTests: XCTestCase {
    private func selectedIdentity() throws -> DisplayIdentity {
        let environment = ProcessInfo.processInfo.environment
        try XCTSkipUnless(environment["DISPLAY_COLOR_HARDWARE_TESTS"] == "1", "Set DISPLAY_COLOR_HARDWARE_TESTS=1 to enable visible display mutations.")
        guard let raw = environment["DISPLAY_COLOR_UUID"], let identity = DisplayIdentity(rawValue: raw) else {
            throw XCTSkip("Set DISPLAY_COLOR_UUID to the exact display UUID that may be modified.")
        }
        return identity
    }

    func testEnumerateSelectedDisplayAndRoundTripNearLinearTable() async throws {
        let identity = try selectedIdentity()
        let hardware = CoreGraphicsDisplaySystem()
        let records = try await hardware.activeDisplays()
        let selected = try XCTUnwrap(records.first(where: { $0.identity == identity }))
        print("Opt-in hardware test display: \(selected.localizedName) UUID=\(selected.identity.rawValue)")
        let transfer = CoreGraphicsTransferTableAdapter(displays: hardware)
        let original = try await transfer.capture(for: identity)
        let nearLinear = try TransferTable.gammaGain(gamma: 1.01, gain: ChannelGain(red: 0.99, green: 0.99, blue: 0.99))

        do {
            _ = try await transfer.apply(nearLinear, to: identity)
            _ = try await transfer.apply(original, to: identity)
        } catch {
            let primary = error
            do { _ = try await transfer.apply(original, to: identity) }
            catch { XCTFail("Rollback after hardware-test failure also failed: \(error.localizedDescription)") }
            throw primary
        }
    }

    func testAssignKnownICCToOnlySelectedDisplayAndRestore() async throws {
        let identity = try selectedIdentity()
        guard let profilePath = ProcessInfo.processInfo.environment["DISPLAY_COLOR_TEST_ICC"] else {
            throw XCTSkip("Set DISPLAY_COLOR_TEST_ICC to a known display ICC profile for assignment testing.")
        }
        let hardware = CoreGraphicsDisplaySystem()
        let profileSystem = SystemColorProfileAdapter()
        let profileStore = UserColorSyncProfileStore()
        let allDisplays = try await hardware.activeDisplays()
        let selected = try XCTUnwrap(allDisplays.first(where: { $0.identity == identity }))
        print("Opt-in ICC assignment display: \(selected.localizedName) UUID=\(selected.identity.rawValue)")

        var before: [DisplayIdentity: ProfileState] = [:]
        for display in allDisplays {
            before[display.identity] = try await profileSystem.profileState(for: display.identity)
        }
        let selectedBefore = try XCTUnwrap(before[identity])
        let staged = try await profileStore.stageProfile(from: URL(fileURLWithPath: profilePath))

        do {
            try await profileSystem.setCustomDefaultProfile(staged.url, for: identity)
            try await waitForProfile(staged.url, display: identity, profileSystem: profileSystem)
            for display in allDisplays where display.identity != identity {
                let unchanged = try await profileSystem.profileState(for: display.identity)
                XCTAssertEqual(unchanged.current.url.standardizedFileURL, before[display.identity]?.current.url.standardizedFileURL)
            }
            try await profileSystem.setCustomDefaultProfile(selectedBefore.customDefaultURL, for: identity)
            try await waitForProfile(selectedBefore.current.url, display: identity, profileSystem: profileSystem)
            try await profileStore.removeIfOwned(staged)
        } catch {
            let primary = error
            do {
                try await profileSystem.setCustomDefaultProfile(selectedBefore.customDefaultURL, for: identity)
                try await waitForProfile(selectedBefore.current.url, display: identity, profileSystem: profileSystem)
                try await profileStore.removeIfOwned(staged)
            } catch {
                XCTFail("ICC integration-test rollback failed: \(error.localizedDescription)")
            }
            throw primary
        }
    }

    private func waitForProfile(_ expected: URL, display: DisplayIdentity, profileSystem: SystemColorProfileAdapter) async throws {
        for _ in 0..<20 {
            let state = try await profileSystem.profileState(for: display)
            if state.current.url.standardizedFileURL == expected.standardizedFileURL { return }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        throw DisplayColorError.profileVerificationTimedOut(display)
    }
}
