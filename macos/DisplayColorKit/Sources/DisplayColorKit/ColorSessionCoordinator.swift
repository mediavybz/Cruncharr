import Foundation
import OSLog

public actor ColorSessionCoordinator {
    private let displays: any DisplayHardwareSystem
    private let profiles: any ColorProfileSystem
    private let profileStore: any ProfileFileStoring
    private let transferTables: any TransferTableSystem
    private let brightness: (any BrightnessControlling)?
    private let journals: any RecoveryJournalStoring
    private let clock: any VerificationClock
    private let verificationTimeout: TimeInterval
    private let verificationPollInterval: TimeInterval
    private let logger = Logger(subsystem: "DisplayColorKit", category: "Session")

    private var activeSessions: [DisplayIdentity: RecoveryJournal] = [:]
    private var mutatingDisplays: Set<DisplayIdentity> = []
    private var topologyChanging = false

    public init(
        displays: any DisplayHardwareSystem,
        profiles: any ColorProfileSystem,
        profileStore: any ProfileFileStoring,
        transferTables: any TransferTableSystem,
        brightness: (any BrightnessControlling)? = nil,
        journals: any RecoveryJournalStoring,
        clock: any VerificationClock = SystemVerificationClock(),
        verificationTimeout: TimeInterval = 2,
        verificationPollInterval: TimeInterval = 0.1
    ) {
        self.displays = displays
        self.profiles = profiles
        self.profileStore = profileStore
        self.transferTables = transferTables
        self.brightness = brightness
        self.journals = journals
        self.clock = clock
        self.verificationTimeout = max(0, verificationTimeout)
        self.verificationPollInterval = max(0, verificationPollInterval)
    }

    public func activate(_ request: CalibrationRequest) async throws -> ActiveSession {
        guard !topologyChanging else { throw DisplayColorError.topologyChangeInProgress }
        try acquireMutation(for: request.display)
        defer { releaseMutation(for: request.display) }

        try checkCancellation()
        _ = try await displays.resolveDisplayID(for: request.display)
        let originalProfile = try await profiles.profileState(for: request.display)
        let originalTable = try await transferTables.capture(for: request.display)
        var journal = RecoveryJournal(
            stage: .prepared,
            snapshot: SessionSnapshot(
                display: request.display,
                originalProfileURL: originalProfile.current.url,
                originalCustomDefaultURL: originalProfile.customDefaultURL,
                originalTransferTable: originalTable
            ),
            intendedTransferTable: request.transferTable
        )
        try await journals.save(journal)

        do {
            try checkCancellation()
            let staged = try await profileStore.stageProfile(from: request.profileURL)
            journal.stagedProfile = staged
            try await journals.save(journal)

            try checkCancellation()
            journal.stage = .assigningProfile
            try await journals.save(journal)
            _ = try await displays.resolveDisplayID(for: request.display)
            try await profiles.setCustomDefaultProfile(staged.url, for: request.display)

            journal.stage = .verifyingProfile
            try await journals.save(journal)
            try await verifyProfile(
                display: request.display,
                currentURL: staged.url,
                customDefaultURL: staged.url,
                cancellationAllowed: true
            )

            var transferVerification: TransferVerification?
            if let table = request.transferTable {
                try checkCancellation()
                journal.stage = .applyingTransferTable
                try await journals.save(journal)
                _ = try await displays.resolveDisplayID(for: request.display)
                transferVerification = try await transferTables.apply(table, to: request.display)
            }

            if let requestedBrightness = request.brightness {
                do {
                    try checkCancellation()
                    let display = try await exactDisplay(request.display)
                    guard let brightness else { throw DisplayColorError.brightnessUnsupported(request.display) }
                    try await brightness.setBrightness(requestedBrightness, for: display)
                } catch {
                    if request.brightnessIsRequired { throw error }
                    logger.warning("Optional brightness failed for display=\(request.display.rawValue, privacy: .public): \(error.displayColorDescription, privacy: .public)")
                }
            }

            journal.stage = .active
            try await journals.save(journal)
            activeSessions[request.display] = journal
            return ActiveSession(
                id: journal.id,
                display: request.display,
                stagedProfile: staged,
                transferVerification: transferVerification
            )
        } catch {
            let primary = normalized(error)
            let failures = await rollback(journal, removeStagedProfile: true)
            if failures.isEmpty { throw primary }
            throw DisplayColorError.rollbackFailed(primary: primary.displayColorDescription, failures: failures)
        }
    }

    public func deactivate(sessionID: UUID) async throws {
        guard let journal = activeSessions.values.first(where: { $0.id == sessionID }) else {
            throw DisplayColorError.system("No active color session has ID \(sessionID.uuidString).")
        }
        let display = journal.snapshot.display
        try acquireMutation(for: display, allowActiveSession: true)
        defer { releaseMutation(for: display) }
        let failures = await rollback(journal, removeStagedProfile: true)
        guard failures.isEmpty else {
            throw DisplayColorError.rollbackFailed(primary: "Session deactivation could not be completed.", failures: failures)
        }
        activeSessions.removeValue(forKey: display)
    }

    public func restoreAllActiveSessions() async -> [UUID: String] {
        var outcomes: [UUID: String] = [:]
        let ids = activeSessions.values.map(\.id)
        for id in ids {
            do {
                try await deactivate(sessionID: id)
                outcomes[id] = "restored"
            } catch {
                outcomes[id] = error.displayColorDescription
            }
        }
        return outcomes
    }

    public func recoverUnfinishedSessions() async throws -> [UUID: String] {
        let pending = try await journals.loadAll()
        var outcomes: [UUID: String] = [:]
        for journal in pending {
            let display = journal.snapshot.display
            do {
                try acquireMutation(for: display, allowActiveSession: true)
            } catch {
                outcomes[journal.id] = error.displayColorDescription
                continue
            }
            let failures = await rollback(journal, removeStagedProfile: true)
            releaseMutation(for: display)
            if failures.isEmpty {
                activeSessions.removeValue(forKey: display)
                outcomes[journal.id] = "restored"
            } else {
                outcomes[journal.id] = failures.joined(separator: "; ")
            }
        }
        return outcomes
    }

    public func handleTopologyEvent(_ event: DisplayTopologyEvent) async {
        switch event {
        case .willChange:
            topologyChanging = true
        case .didChange:
            do {
                _ = try await displays.activeDisplays()
                topologyChanging = false
                for journal in activeSessions.values {
                    guard let staged = journal.stagedProfile else { continue }
                    do {
                        _ = try await displays.resolveDisplayID(for: journal.snapshot.display)
                        let state = try await profiles.profileState(for: journal.snapshot.display)
                        guard Self.sameFile(state.current.url, staged.url),
                              let customURL = state.customDefaultURL,
                              Self.sameFile(customURL, staged.url) else { continue }
                        if let intended = journal.intendedTransferTable {
                            let needsReapply: Bool
                            do {
                                let currentTable = try await transferTables.capture(for: journal.snapshot.display)
                                needsReapply = !currentTable.approximatelyEquals(intended, tolerance: 1.0 / 65_535.0)
                            } catch {
                                needsReapply = true
                            }
                            if needsReapply {
                                _ = try await transferTables.apply(intended, to: journal.snapshot.display)
                            }
                        }
                    } catch {
                        logger.error("Reconfiguration recovery deferred for display=\(journal.snapshot.display.rawValue, privacy: .public): \(error.displayColorDescription, privacy: .public)")
                    }
                }
            } catch {
                topologyChanging = true
                logger.error("Display inventory is incoherent after reconfiguration: \(error.displayColorDescription, privacy: .public)")
            }
        }
    }

    public nonisolated func topologyHandler() -> DisplayTopologyMonitor.Handler {
        { [weak self] event in
            guard let self else { return }
            Task { await self.handleTopologyEvent(event) }
        }
    }

    private func rollback(_ originalJournal: RecoveryJournal, removeStagedProfile: Bool) async -> [String] {
        var journal = originalJournal
        var failures: [String] = []
        journal.stage = .rollingBack
        do { try await journals.save(journal) }
        catch { failures.append("could not update recovery journal: \(error.displayColorDescription)") }

        do {
            _ = try await displays.resolveDisplayID(for: journal.snapshot.display)
            try await profiles.setCustomDefaultProfile(journal.snapshot.originalCustomDefaultURL, for: journal.snapshot.display)
            try await verifyProfile(
                display: journal.snapshot.display,
                currentURL: journal.snapshot.originalProfileURL,
                customDefaultURL: journal.snapshot.originalCustomDefaultURL,
                cancellationAllowed: false
            )
        } catch {
            failures.append("profile restore failed: \(error.displayColorDescription)")
        }

        do {
            _ = try await displays.resolveDisplayID(for: journal.snapshot.display)
            _ = try await transferTables.apply(journal.snapshot.originalTransferTable, to: journal.snapshot.display)
        } catch {
            await transferTables.restoreColorSyncSettingsFallback()
            failures.append("exact transfer-table restore failed; global ColorSync fallback invoked: \(error.displayColorDescription)")
        }

        if failures.isEmpty {
            journal.stage = .completed
            do {
                try await journals.save(journal)
                try await journals.remove(id: journal.id)
            } catch {
                failures.append("recovery journal cleanup failed: \(error.displayColorDescription)")
            }
            if failures.isEmpty, removeStagedProfile, let staged = journal.stagedProfile {
                do { try await profileStore.removeIfOwned(staged) }
                catch { failures.append("staged profile cleanup failed: \(error.displayColorDescription)") }
            }
        } else {
            journal.stage = .failed
            do { try await journals.save(journal) }
            catch { failures.append("failed-state journal write failed: \(error.displayColorDescription)") }
        }
        return failures
    }

    private func verifyProfile(
        display: DisplayIdentity,
        currentURL: URL,
        customDefaultURL: URL?,
        cancellationAllowed: Bool
    ) async throws {
        let start = await clock.now()
        let deadline = start.addingTimeInterval(verificationTimeout)
        var lastError: Error?
        while true {
            if cancellationAllowed { try checkCancellation() }
            do {
                _ = try await displays.resolveDisplayID(for: display)
                let state = try await profiles.profileState(for: display)
                let currentMatches = Self.sameFile(state.current.url, currentURL)
                let customMatches: Bool
                switch (state.customDefaultURL, customDefaultURL) {
                case (nil, nil): customMatches = true
                case (.some(let actual), .some(let expected)): customMatches = Self.sameFile(actual, expected)
                default: customMatches = false
                }
                if currentMatches && customMatches { return }
            } catch {
                lastError = error
            }
            if await clock.now() >= deadline {
                if let lastError {
                    logger.error("Profile verification timed out for display=\(display.rawValue, privacy: .public); lastError=\(lastError.displayColorDescription, privacy: .public)")
                }
                throw DisplayColorError.profileVerificationTimedOut(display)
            }
            do { try await clock.sleep(for: verificationPollInterval) }
            catch {
                if cancellationAllowed { throw normalized(error) }
            }
        }
    }

    private func exactDisplay(_ identity: DisplayIdentity) async throws -> DisplayRecord {
        let records = try await displays.activeDisplays()
        guard let record = records.first(where: { $0.identity == identity }) else {
            throw DisplayColorError.displayDisconnected(identity)
        }
        return record
    }

    private func acquireMutation(for display: DisplayIdentity, allowActiveSession: Bool = false) throws {
        let activeConflict = !allowActiveSession && activeSessions[display] != nil
        guard !mutatingDisplays.contains(display), !activeConflict else {
            throw DisplayColorError.mutationAlreadyActive(display)
        }
        mutatingDisplays.insert(display)
    }

    private func releaseMutation(for display: DisplayIdentity) {
        mutatingDisplays.remove(display)
    }

    private func checkCancellation() throws {
        if Task.isCancelled { throw DisplayColorError.cancelled }
    }

    private func normalized(_ error: Error) -> DisplayColorError {
        if error is CancellationError { return .cancelled }
        return (error as? DisplayColorError) ?? .system(error.localizedDescription)
    }

    private static func sameFile(_ lhs: URL, _ rhs: URL) -> Bool {
        lhs.standardizedFileURL.resolvingSymlinksInPath() == rhs.standardizedFileURL.resolvingSymlinksInPath()
    }
}
