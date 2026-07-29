import Foundation
@testable import DisplayColorKit

actor FakeDisplaySystem: DisplayHardwareSystem {
    var records: [DisplayRecord]
    var disconnected: Set<DisplayIdentity> = []
    var resolveCount = 0

    init(records: [DisplayRecord]) {
        self.records = records
    }

    func activeDisplays() async throws -> [DisplayRecord] { records }

    func resolveDisplayID(for identity: DisplayIdentity) async throws -> UInt32 {
        resolveCount += 1
        guard !disconnected.contains(identity), let record = records.first(where: { $0.identity == identity }) else {
            throw DisplayColorError.displayDisconnected(identity)
        }
        return record.displayID
    }

    func disconnect(_ identity: DisplayIdentity) { disconnected.insert(identity) }
    func setRecords(_ newRecords: [DisplayRecord]) { records = newRecords }
}

actor FakeProfileSystem: ColorProfileSystem {
    private enum Pending {
        case none
        case value(URL?)
    }

    let display: DisplayIdentity
    let baselineURL: URL
    var currentURL: URL
    var customURL: URL?
    var setterFailureCalls: Set<Int> = []
    var ignoredAssignmentCalls: Set<Int> = []
    var assignmentDelayReads = 0
    var stateError: DisplayColorError?
    var setCalls: [URL?] = []
    var stateReadCount = 0
    private var pending: Pending = .none
    private var readsRemaining = 0

    init(display: DisplayIdentity, baselineURL: URL, customURL: URL? = nil) {
        self.display = display
        self.baselineURL = baselineURL.standardizedFileURL
        self.currentURL = baselineURL.standardizedFileURL
        self.customURL = customURL?.standardizedFileURL
    }

    func profileState(for display: DisplayIdentity) async throws -> ProfileState {
        guard display == self.display else { throw DisplayColorError.displayNotFound(display) }
        if let stateError { throw stateError }
        stateReadCount += 1
        switch pending {
        case .none:
            break
        case .value(let url):
            if readsRemaining > 0 {
                readsRemaining -= 1
            } else {
                apply(url)
                pending = .none
            }
        }
        let record = ProfileRecord(
            url: currentURL,
            profileIdentifier: "default",
            description: "Fake profile",
            isCurrent: true,
            isDeviceDefault: customURL == nil,
            isCustomAssignment: customURL != nil
        )
        return ProfileState(profiles: [record], current: record, customDefaultURL: customURL)
    }

    func setCustomDefaultProfile(_ url: URL?, for display: DisplayIdentity) async throws {
        guard display == self.display else { throw DisplayColorError.displayNotFound(display) }
        setCalls.append(url?.standardizedFileURL)
        let call = setCalls.count
        if setterFailureCalls.contains(call) {
            throw DisplayColorError.customProfileAssignmentRejected(display)
        }
        if ignoredAssignmentCalls.contains(call) { return }
        if assignmentDelayReads > 0 {
            pending = .value(url?.standardizedFileURL)
            readsRemaining = assignmentDelayReads
        } else {
            apply(url)
        }
    }

    func configure(setterFailures: Set<Int> = [], ignoredAssignments: Set<Int> = [], delayReads: Int = 0) {
        setterFailureCalls = setterFailures
        ignoredAssignmentCalls = ignoredAssignments
        assignmentDelayReads = delayReads
    }

    func assignments() -> [URL?] { setCalls }
    func reads() -> Int { stateReadCount }

    private func apply(_ url: URL?) {
        customURL = url?.standardizedFileURL
        currentURL = url?.standardizedFileURL ?? baselineURL
    }
}

actor FakeProfileStore: ProfileFileStoring {
    let staged: StagedProfile
    var stageError: DisplayColorError?
    var removed: [StagedProfile] = []

    init(staged: StagedProfile) { self.staged = staged }

    func stageProfile(from sourceURL: URL) async throws -> StagedProfile {
        if let stageError { throw stageError }
        return staged
    }

    func removeIfOwned(_ profile: StagedProfile) async throws { removed.append(profile) }
    func removalCount() -> Int { removed.count }
}

actor FakeTransferSystem: TransferTableSystem {
    var captured: TransferTable
    var applyCalls: [TransferTable] = []
    var failureCalls: Set<Int> = []
    var fallbackCount = 0
    var verification: TransferVerification = .verified

    init(captured: TransferTable) { self.captured = captured }

    func capture(for display: DisplayIdentity) async throws -> TransferTable { captured }

    func apply(_ table: TransferTable, to display: DisplayIdentity) async throws -> TransferVerification {
        applyCalls.append(table)
        if failureCalls.contains(applyCalls.count) {
            throw DisplayColorError.transferTableWrite(display, code: -1)
        }
        return verification
    }

    func restoreColorSyncSettingsFallback() async { fallbackCount += 1 }
    func configure(failureCalls: Set<Int>) { self.failureCalls = failureCalls }
    func applied() -> [TransferTable] { applyCalls }
    func fallbacks() -> Int { fallbackCount }
}

actor FakeBrightness: BrightnessControlling {
    var supported = true
    var error: DisplayColorError?
    var values: [Float] = []

    func supportsBrightness(for display: DisplayRecord) async throws -> Bool { supported }

    func setBrightness(_ value: Float, for display: DisplayRecord) async throws {
        if let error { throw error }
        values.append(value)
    }
}

actor MemoryJournalStore: RecoveryJournalStoring {
    var storage: [UUID: RecoveryJournal] = [:]
    var savedStages: [SessionStage] = []
    var failRemove = false

    func save(_ journal: RecoveryJournal) async throws {
        savedStages.append(journal.stage)
        storage[journal.id] = journal
    }

    func remove(id: UUID) async throws {
        if failRemove { throw DisplayColorError.system("injected journal removal failure") }
        storage.removeValue(forKey: id)
    }

    func loadAll() async throws -> [RecoveryJournal] { Array(storage.values) }
    func count() -> Int { storage.count }
    func stages() -> [SessionStage] { savedStages }
    func insert(_ journal: RecoveryJournal) { storage[journal.id] = journal }
}

actor TestClock: VerificationClock {
    var instant: Date
    var sleepCount = 0

    init(instant: Date = Date(timeIntervalSince1970: 1_000)) { self.instant = instant }

    func now() async -> Date { instant }

    func sleep(for interval: TimeInterval) async throws {
        sleepCount += 1
        instant = instant.addingTimeInterval(max(0, interval))
    }

    func sleeps() -> Int { sleepCount }
}

func makeDisplay(identity: DisplayIdentity, id: UInt32 = 42) -> DisplayRecord {
    DisplayRecord(
        displayID: id,
        identity: identity,
        localizedName: "Test Display",
        vendorID: 1,
        productID: 2,
        serialNumber: 3,
        isBuiltin: false,
        isOnline: true,
        isMirrored: false,
        pixelSize: DisplaySize(width: 1920, height: 1080),
        bounds: DisplayRectangle(origin: DisplayPoint(x: 0, y: 0), size: DisplaySize(width: 1920, height: 1080)),
        physicalSizeMillimeters: DisplaySize(width: 500, height: 300)
    )
}
