import Foundation

public protocol DisplayHardwareSystem: Sendable {
    func activeDisplays() async throws -> [DisplayRecord]
    func resolveDisplayID(for identity: DisplayIdentity) async throws -> UInt32
}

public protocol ColorProfileSystem: Sendable {
    func profileState(for display: DisplayIdentity) async throws -> ProfileState
    func setCustomDefaultProfile(_ url: URL?, for display: DisplayIdentity) async throws
}

public protocol ProfileFileStoring: Sendable {
    func stageProfile(from sourceURL: URL) async throws -> StagedProfile
    func removeIfOwned(_ profile: StagedProfile) async throws
}

public protocol TransferTableSystem: Sendable {
    func capture(for display: DisplayIdentity) async throws -> TransferTable
    func apply(_ table: TransferTable, to display: DisplayIdentity) async throws -> TransferVerification
    func restoreColorSyncSettingsFallback() async
}

public protocol BrightnessControlling: Sendable {
    func supportsBrightness(for display: DisplayRecord) async throws -> Bool
    func setBrightness(_ value: Float, for display: DisplayRecord) async throws
}

public protocol RecoveryJournalStoring: Sendable {
    func save(_ journal: RecoveryJournal) async throws
    func remove(id: UUID) async throws
    func loadAll() async throws -> [RecoveryJournal]
}

public protocol VerificationClock: Sendable {
    func now() async -> Date
    func sleep(for interval: TimeInterval) async throws
}

public struct SystemVerificationClock: VerificationClock {
    public init() {}

    public func now() async -> Date { Date() }

    public func sleep(for interval: TimeInterval) async throws {
        guard interval > 0 else { return }
        let nanoseconds = UInt64(min(interval * 1_000_000_000, Double(UInt64.max)))
        try await Task.sleep(nanoseconds: nanoseconds)
    }
}
