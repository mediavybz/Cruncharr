import Foundation

public enum DisplayColorError: Error, Equatable, Sendable {
    case displayNotFound(DisplayIdentity)
    case displayDisconnected(DisplayIdentity)
    case displayUUIDUnavailable(UInt32)
    case ambiguousDisplayService(DisplayIdentity, matches: Int)
    case colorSyncDeviceInfoUnavailable(DisplayIdentity)
    case malformedColorSyncEntry(String)
    case profileEnumerationFailed(DisplayIdentity, String)
    case currentProfileMissing(DisplayIdentity)
    case currentProfileAmbiguous(DisplayIdentity, count: Int)
    case factoryProfileUnavailable(DisplayIdentity)
    case invalidProfile(URL, String)
    case profileValidation(URL, String)
    case profileDirectoryUnavailable(String)
    case profileStaging(String)
    case sandboxConfiguration(String)
    case customProfileAssignmentRejected(DisplayIdentity)
    case profileVerificationTimedOut(DisplayIdentity)
    case transferTableCapture(DisplayIdentity, code: Int32)
    case transferTableValidation(String)
    case transferTableWrite(DisplayIdentity, code: Int32)
    case transferTableReadback(DisplayIdentity, String)
    case brightnessUnsupported(DisplayIdentity)
    case brightnessIO(DisplayIdentity, code: Int32)
    case displayChangedDuringOperation(DisplayIdentity)
    case mutationAlreadyActive(DisplayIdentity)
    case topologyChangeInProgress
    case cancelled
    case rollbackFailed(primary: String, failures: [String])
    case corruptRecoveryJournal(URL, String)
    case system(String)
}

extension DisplayColorError: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case .displayNotFound(let id): return "Display \(id) was not found."
        case .displayDisconnected(let id): return "Display \(id) disconnected."
        case .displayUUIDUnavailable(let id): return "Display ID \(id) has no stable Core Graphics UUID."
        case .ambiguousDisplayService(let id, let matches): return "Display \(id) matched \(matches) IOKit services; refusing to guess."
        case .colorSyncDeviceInfoUnavailable(let id): return "ColorSync device information is unavailable for \(id)."
        case .malformedColorSyncEntry(let detail): return "ColorSync returned malformed profile data: \(detail)"
        case .profileEnumerationFailed(let id, let detail): return "Profile enumeration failed for \(id): \(detail)"
        case .currentProfileMissing(let id): return "No current profile is reported for \(id)."
        case .currentProfileAmbiguous(let id, let count): return "ColorSync reports \(count) current profiles for \(id)."
        case .factoryProfileUnavailable(let id): return "No defensible factory profile is available for \(id)."
        case .invalidProfile(let url, let detail): return "The ICC profile \(url.lastPathComponent) is invalid: \(detail)"
        case .profileValidation(let url, let detail): return "The ICC profile \(url.lastPathComponent) failed ColorSync validation: \(detail)"
        case .profileDirectoryUnavailable(let detail): return "The user ColorSync profile directory is unavailable: \(detail)"
        case .profileStaging(let detail): return "ICC profile staging failed: \(detail)"
        case .sandboxConfiguration(let detail): return "The app's sandbox or signing configuration does not permit this profile operation: \(detail)"
        case .customProfileAssignmentRejected(let id): return "ColorSync rejected the custom profile assignment for \(id)."
        case .profileVerificationTimedOut(let id): return "The profile assignment for \(id) could not be verified before the deadline."
        case .transferTableCapture(let id, let code): return "Could not capture the transfer table for \(id) (CGError \(code))."
        case .transferTableValidation(let detail): return "Invalid transfer table: \(detail)"
        case .transferTableWrite(let id, let code): return "Could not write the transfer table for \(id) (CGError \(code))."
        case .transferTableReadback(let id, let detail): return "Transfer-table readback failed for \(id): \(detail)"
        case .brightnessUnsupported(let id): return "Brightness control is not supported for \(id)."
        case .brightnessIO(let id, let code): return "Brightness I/O failed for \(id) (IOReturn \(code))."
        case .displayChangedDuringOperation(let id): return "Display \(id) changed during the operation."
        case .mutationAlreadyActive(let id): return "A mutation is already active for \(id)."
        case .topologyChangeInProgress: return "Display reconfiguration is in progress."
        case .cancelled: return "The display operation was cancelled."
        case .rollbackFailed(let primary, let failures): return "\(primary) Rollback also failed: \(failures.joined(separator: "; "))"
        case .corruptRecoveryJournal(let url, let detail): return "Recovery journal \(url.lastPathComponent) is corrupt: \(detail)"
        case .system(let detail): return detail
        }
    }
}

extension Error {
    var displayColorDescription: String {
        (self as? LocalizedError)?.errorDescription ?? localizedDescription
    }
}
