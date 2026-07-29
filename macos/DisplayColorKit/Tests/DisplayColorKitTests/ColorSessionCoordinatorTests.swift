import Foundation
import XCTest
@testable import DisplayColorKit

private struct CoordinatorFixture {
    let identity: DisplayIdentity
    let baselineURL: URL
    let stagedURL: URL
    let display: FakeDisplaySystem
    let profiles: FakeProfileSystem
    let store: FakeProfileStore
    let transfer: FakeTransferSystem
    let journals: MemoryJournalStore
    let clock: TestClock
    let baselineTable: TransferTable
    let requestedTable: TransferTable
    let coordinator: ColorSessionCoordinator
}

private func makeCoordinatorFixture(brightness: (any BrightnessControlling)? = nil) throws -> CoordinatorFixture {
    let identity = try XCTUnwrap(DisplayIdentity(rawValue: "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"))
    let baselineURL = URL(fileURLWithPath: "/Library/ColorSync/Profiles/Baseline.icc")
    let stagedURL = URL(fileURLWithPath: "/Users/test/Library/ColorSync/Profiles/New.icc")
    let baselineTable = try TransferTable.linear(sampleCount: 4)
    let requestedTable = try TransferTable.gammaGain(gamma: 2, gain: ChannelGain(red: 1, green: 0.9, blue: 0.8))
    let display = FakeDisplaySystem(records: [makeDisplay(identity: identity)])
    let profiles = FakeProfileSystem(display: identity, baselineURL: baselineURL)
    let staged = StagedProfile(url: stagedURL, digest: "abc123", description: "New", createdByComponent: true)
    let store = FakeProfileStore(staged: staged)
    let transfer = FakeTransferSystem(captured: baselineTable)
    let journals = MemoryJournalStore()
    let clock = TestClock()
    let coordinator = ColorSessionCoordinator(
        displays: display,
        profiles: profiles,
        profileStore: store,
        transferTables: transfer,
        brightness: brightness,
        journals: journals,
        clock: clock,
        verificationTimeout: 1,
        verificationPollInterval: 0.25
    )
    return CoordinatorFixture(
        identity: identity,
        baselineURL: baselineURL,
        stagedURL: stagedURL,
        display: display,
        profiles: profiles,
        store: store,
        transfer: transfer,
        journals: journals,
        clock: clock,
        baselineTable: baselineTable,
        requestedTable: requestedTable,
        coordinator: coordinator
    )
}

final class ColorSessionCoordinatorTests: XCTestCase {
    func testSuccessfulSessionRetainsJournalUntilVerifiedDeactivation() async throws {
        let fixture = try makeCoordinatorFixture()
        let request = CalibrationRequest(
            display: fixture.identity,
            profileURL: URL(fileURLWithPath: "/input/New.icc"),
            transferTable: fixture.requestedTable
        )

        let session = try await fixture.coordinator.activate(request)
        XCTAssertEqual(session.display, fixture.identity)
        await XCTAssertEqualAsync(await fixture.journals.count(), 1)
        await XCTAssertEqualAsync(await fixture.transfer.applied(), [fixture.requestedTable])
        await XCTAssertEqualAsync(await fixture.profiles.assignments(), [fixture.stagedURL])
        let stages = await fixture.journals.stages()
        XCTAssertTrue(stages.contains(.prepared))
        XCTAssertTrue(stages.contains(.assigningProfile))
        XCTAssertTrue(stages.contains(.verifyingProfile))
        XCTAssertTrue(stages.contains(.applyingTransferTable))
        XCTAssertEqual(stages.last, .active)

        try await fixture.coordinator.deactivate(sessionID: session.id)
        await XCTAssertEqualAsync(await fixture.journals.count(), 0)
        await XCTAssertEqualAsync(await fixture.profiles.assignments(), [fixture.stagedURL, nil])
        await XCTAssertEqualAsync(await fixture.transfer.applied(), [fixture.requestedTable, fixture.baselineTable])
        await XCTAssertEqualAsync(await fixture.store.removalCount(), 1)
    }

    func testSetterFailureRollsBackSnapshotAndClearsJournal() async throws {
        let fixture = try makeCoordinatorFixture()
        await fixture.profiles.configure(setterFailures: [1])
        let request = CalibrationRequest(display: fixture.identity, profileURL: URL(fileURLWithPath: "/input/New.icc"))

        do {
            _ = try await fixture.coordinator.activate(request)
            XCTFail("Expected assignment failure")
        } catch let error as DisplayColorError {
            XCTAssertEqual(error, .customProfileAssignmentRejected(fixture.identity))
        }
        await XCTAssertEqualAsync(await fixture.profiles.assignments(), [fixture.stagedURL, nil])
        await XCTAssertEqualAsync(await fixture.transfer.applied(), [fixture.baselineTable])
        await XCTAssertEqualAsync(await fixture.journals.count(), 0)
        await XCTAssertEqualAsync(await fixture.store.removalCount(), 1)
    }

    func testSetterSuccessWithoutReadbackTimesOutAndRollsBack() async throws {
        let fixture = try makeCoordinatorFixture()
        await fixture.profiles.configure(ignoredAssignments: [1])
        let request = CalibrationRequest(display: fixture.identity, profileURL: URL(fileURLWithPath: "/input/New.icc"))

        do {
            _ = try await fixture.coordinator.activate(request)
            XCTFail("Expected verification timeout")
        } catch let error as DisplayColorError {
            XCTAssertEqual(error, .profileVerificationTimedOut(fixture.identity))
        }
        await XCTAssertGreaterThanAsync(await fixture.clock.sleeps(), 0)
        await XCTAssertEqualAsync(await fixture.profiles.assignments(), [fixture.stagedURL, nil])
        await XCTAssertEqualAsync(await fixture.journals.count(), 0)
    }

    func testDelayedReadbackSucceedsUsingInjectedClock() async throws {
        let fixture = try makeCoordinatorFixture()
        await fixture.profiles.configure(delayReads: 2)
        let request = CalibrationRequest(display: fixture.identity, profileURL: URL(fileURLWithPath: "/input/New.icc"))

        let session = try await fixture.coordinator.activate(request)
        XCTAssertEqual(session.display, fixture.identity)
        await XCTAssertEqualAsync(await fixture.clock.sleeps(), 2)
    }

    func testLUTFailureRestoresBothProfileAndExactTable() async throws {
        let fixture = try makeCoordinatorFixture()
        await fixture.transfer.configure(failureCalls: [1])
        let request = CalibrationRequest(
            display: fixture.identity,
            profileURL: URL(fileURLWithPath: "/input/New.icc"),
            transferTable: fixture.requestedTable
        )

        do {
            _ = try await fixture.coordinator.activate(request)
            XCTFail("Expected LUT failure")
        } catch let error as DisplayColorError {
            XCTAssertEqual(error, .transferTableWrite(fixture.identity, code: -1))
        }
        await XCTAssertEqualAsync(await fixture.profiles.assignments(), [fixture.stagedURL, nil])
        await XCTAssertEqualAsync(await fixture.transfer.applied(), [fixture.requestedTable, fixture.baselineTable])
        await XCTAssertEqualAsync(await fixture.journals.count(), 0)
    }

    func testPrimaryAndRollbackFailureKeepsRecoveryJournal() async throws {
        let fixture = try makeCoordinatorFixture()
        await fixture.transfer.configure(failureCalls: [1, 2])
        let request = CalibrationRequest(
            display: fixture.identity,
            profileURL: URL(fileURLWithPath: "/input/New.icc"),
            transferTable: fixture.requestedTable
        )

        do {
            _ = try await fixture.coordinator.activate(request)
            XCTFail("Expected composite failure")
        } catch let error as DisplayColorError {
            guard case .rollbackFailed(_, let failures) = error else { return XCTFail("Unexpected error: \(error)") }
            XCTAssertTrue(failures.contains { $0.contains("exact transfer-table restore failed") })
        }
        await XCTAssertEqualAsync(await fixture.transfer.fallbacks(), 1)
        await XCTAssertEqualAsync(await fixture.journals.count(), 1)
        await XCTAssertEqualAsync(await fixture.store.removalCount(), 0)
    }

    func testDisconnectedDisplayFailsBeforeJournalOrMutation() async throws {
        let fixture = try makeCoordinatorFixture()
        await fixture.display.disconnect(fixture.identity)
        let request = CalibrationRequest(display: fixture.identity, profileURL: URL(fileURLWithPath: "/input/New.icc"))

        await XCTAssertThrowsErrorAsync(try await fixture.coordinator.activate(request))
        await XCTAssertEqualAsync(await fixture.journals.count(), 0)
        await XCTAssertEqualAsync(await fixture.profiles.assignments(), [])
    }

    func testUnfinishedJournalIsRestoredOnNextLaunch() async throws {
        let fixture = try makeCoordinatorFixture()
        try await fixture.profiles.setCustomDefaultProfile(fixture.stagedURL, for: fixture.identity)
        let staged = StagedProfile(url: fixture.stagedURL, digest: "abc123", description: "New", createdByComponent: true)
        let journal = RecoveryJournal(
            stage: .active,
            snapshot: SessionSnapshot(
                display: fixture.identity,
                originalProfileURL: fixture.baselineURL,
                originalCustomDefaultURL: nil,
                originalTransferTable: fixture.baselineTable
            ),
            stagedProfile: staged,
            intendedTransferTable: fixture.requestedTable
        )
        await fixture.journals.insert(journal)

        let outcomes = try await fixture.coordinator.recoverUnfinishedSessions()
        XCTAssertEqual(outcomes[journal.id], "restored")
        await XCTAssertEqualAsync(await fixture.journals.count(), 0)
        await XCTAssertEqualAsync(await fixture.transfer.applied(), [fixture.baselineTable])
        await XCTAssertEqualAsync(await fixture.store.removalCount(), 1)
    }

    func testOptionalBrightnessFailureDoesNotInvalidateColorSession() async throws {
        let brightness = FakeBrightness()
        let fixture = try makeCoordinatorFixture(brightness: brightness)
        await brightness.configure(error: .brightnessUnsupported(fixture.identity))
        let request = CalibrationRequest(
            display: fixture.identity,
            profileURL: URL(fileURLWithPath: "/input/New.icc"),
            brightness: 0.5,
            brightnessIsRequired: false
        )

        let session = try await fixture.coordinator.activate(request)
        XCTAssertEqual(session.display, fixture.identity)
        await XCTAssertEqualAsync(await fixture.journals.count(), 1)
    }

    func testRequiredBrightnessFailureRollsBackColorSession() async throws {
        let brightness = FakeBrightness()
        let fixture = try makeCoordinatorFixture(brightness: brightness)
        await brightness.configure(error: .brightnessUnsupported(fixture.identity))
        let request = CalibrationRequest(
            display: fixture.identity,
            profileURL: URL(fileURLWithPath: "/input/New.icc"),
            brightness: 0.5,
            brightnessIsRequired: true
        )

        await XCTAssertThrowsErrorAsync(try await fixture.coordinator.activate(request))
        await XCTAssertEqualAsync(await fixture.profiles.assignments(), [fixture.stagedURL, nil])
        await XCTAssertEqualAsync(await fixture.journals.count(), 0)
    }

    func testCancellationDuringVerificationStillCompletesRollback() async throws {
        let identity = try XCTUnwrap(DisplayIdentity(rawValue: "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE"))
        let baseline = URL(fileURLWithPath: "/baseline.icc")
        let stagedURL = URL(fileURLWithPath: "/staged.icc")
        let table = try TransferTable.linear(sampleCount: 4)
        let display = FakeDisplaySystem(records: [makeDisplay(identity: identity)])
        let profiles = FakeProfileSystem(display: identity, baselineURL: baseline)
        await profiles.configure(ignoredAssignments: [1])
        let store = FakeProfileStore(staged: StagedProfile(url: stagedURL, digest: "x", description: nil, createdByComponent: true))
        let transfer = FakeTransferSystem(captured: table)
        let journals = MemoryJournalStore()
        let clock = CancellableVerificationClock()
        let coordinator = ColorSessionCoordinator(
            displays: display,
            profiles: profiles,
            profileStore: store,
            transferTables: transfer,
            journals: journals,
            clock: clock,
            verificationTimeout: 60,
            verificationPollInterval: 1
        )
        let task = Task {
            try await coordinator.activate(CalibrationRequest(display: identity, profileURL: URL(fileURLWithPath: "/input.icc")))
        }
        while await clock.sleeps() == 0 { await Task.yield() }
        task.cancel()

        do {
            _ = try await task.value
            XCTFail("Expected cancellation")
        } catch let error as DisplayColorError {
            XCTAssertEqual(error, .cancelled)
        }
        await XCTAssertEqualAsync(await profiles.assignments(), [stagedURL, nil])
        await XCTAssertEqualAsync(await transfer.applied(), [table])
        await XCTAssertEqualAsync(await journals.count(), 0)
    }
}

private actor CancellableVerificationClock: VerificationClock {
    private var count = 0
    func now() async -> Date { Date(timeIntervalSince1970: 1_000) }
    func sleep(for interval: TimeInterval) async throws {
        count += 1
        try await Task.sleep(nanoseconds: UInt64.max)
    }
    func sleeps() -> Int { count }
}
