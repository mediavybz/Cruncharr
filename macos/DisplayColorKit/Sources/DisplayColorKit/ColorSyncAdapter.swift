import ColorSync
import CoreFoundation
import Foundation

private final class ProfileIterationContext {
    let requestedUUID: CFUUID
    var entries: [(url: URL, identifier: String?, current: Bool, isDefault: Bool, description: String?)] = []
    var diagnostics: [String] = []

    init(requestedUUID: CFUUID) {
        self.requestedUUID = requestedUUID
    }
}

private enum CFDictionaryValue {
    static func object(_ dictionary: CFDictionary, key: CFString) -> CFTypeRef? {
        let keyPointer = Unmanaged.passUnretained(key).toOpaque()
        guard let value = CFDictionaryGetValue(dictionary, keyPointer) else { return nil }
        return unsafeBitCast(value, to: CFTypeRef.self)
    }

    static func string(_ dictionary: CFDictionary, key: CFString) -> CFString? {
        guard let value = object(dictionary, key: key), CFGetTypeID(value) == CFStringGetTypeID() else { return nil }
        return unsafeBitCast(value, to: CFString.self)
    }

    static func uuid(_ dictionary: CFDictionary, key: CFString) -> CFUUID? {
        guard let value = object(dictionary, key: key), CFGetTypeID(value) == CFUUIDGetTypeID() else { return nil }
        return unsafeBitCast(value, to: CFUUID.self)
    }

    static func url(_ dictionary: CFDictionary, key: CFString) -> CFURL? {
        guard let value = object(dictionary, key: key), CFGetTypeID(value) == CFURLGetTypeID() else { return nil }
        return unsafeBitCast(value, to: CFURL.self)
    }

    static func boolean(_ dictionary: CFDictionary, key: CFString) -> Bool? {
        guard let value = object(dictionary, key: key), CFGetTypeID(value) == CFBooleanGetTypeID() else { return nil }
        let boolean = unsafeBitCast(value, to: CFBoolean.self)
        return CFBooleanGetValue(boolean)
    }

    static func dictionary(_ dictionary: CFDictionary, key: CFString) -> CFDictionary? {
        guard let value = object(dictionary, key: key), CFGetTypeID(value) == CFDictionaryGetTypeID() else { return nil }
        return unsafeBitCast(value, to: CFDictionary.self)
    }
}

struct ParsedColorSyncProfileEntry: Equatable {
    let url: URL
    let identifier: String
    let isCurrent: Bool
    let isDeviceDefault: Bool
}

enum ColorSyncProfileEntryParseResult: Equatable {
    case ignored
    case malformed(String)
    case matching(ParsedColorSyncProfileEntry)
}

func parseColorSyncProfileEntry(_ information: CFDictionary, requestedUUID: CFUUID) -> ColorSyncProfileEntryParseResult {
    guard let classKey = kColorSyncDeviceClass?.takeUnretainedValue(),
          let displayClass = kColorSyncDisplayDeviceClass?.takeUnretainedValue(),
          let idKey = kColorSyncDeviceID?.takeUnretainedValue(),
          let profileURLKey = kColorSyncDeviceProfileURL?.takeUnretainedValue(),
          let currentKey = kColorSyncDeviceProfileIsCurrent?.takeUnretainedValue(),
          let profileIDKey = kColorSyncDeviceProfileID?.takeUnretainedValue(),
          let defaultProfileID = kColorSyncDeviceDefaultProfileID?.takeUnretainedValue() else {
        return .malformed("Required ColorSync constants were unavailable.")
    }
    guard let entryClass = CFDictionaryValue.string(information, key: classKey) else {
        return .malformed("Profile entry has a missing or non-string device class.")
    }
    guard CFEqual(entryClass, displayClass) else { return .ignored }
    guard let entryUUID = CFDictionaryValue.uuid(information, key: idKey) else {
        return .malformed("Display profile entry has a missing or non-UUID device ID.")
    }
    guard CFEqual(entryUUID, requestedUUID) else { return .ignored }
    guard let cfURL = CFDictionaryValue.url(information, key: profileURLKey) else {
        return .malformed("Matching profile entry has a missing or non-URL profile URL.")
    }
    guard let current = CFDictionaryValue.boolean(information, key: currentKey) else {
        return .malformed("Matching profile entry has a missing or non-Boolean current flag.")
    }
    guard let profileID = CFDictionaryValue.string(information, key: profileIDKey) else {
        return .malformed("Matching profile entry has a missing or non-string profile identifier.")
    }
    return .matching(ParsedColorSyncProfileEntry(
        url: (cfURL as URL).standardizedFileURL,
        identifier: profileID as String,
        isCurrent: current,
        isDeviceDefault: CFEqual(profileID, defaultProfileID)
    ))
}

private func profileIterationCallback(_ information: CFDictionary?, _ rawContext: UnsafeMutableRawPointer?) -> Bool {
    guard let information, let rawContext else { return true }
    let context = Unmanaged<ProfileIterationContext>.fromOpaque(rawContext).takeUnretainedValue()
    let parsed: ParsedColorSyncProfileEntry
    switch parseColorSyncProfileEntry(information, requestedUUID: context.requestedUUID) {
    case .ignored:
        return true
    case .malformed(let diagnostic):
        context.diagnostics.append(diagnostic)
        return true
    case .matching(let entry):
        parsed = entry
    }

    var creationError: Unmanaged<CFError>?
    var description: String?
    if let unmanagedProfile = ColorSyncProfileCreateWithURL(parsed.url as CFURL, &creationError) {
        let profile = unmanagedProfile.takeRetainedValue()
        description = ColorSyncProfileCopyDescriptionString(profile)?.takeRetainedValue() as String?
    } else {
        let detail = creationError?.takeRetainedValue().localizedDescription ?? "unknown profile creation error"
        context.diagnostics.append("Could not open \(parsed.url.lastPathComponent): \(detail)")
    }
    context.entries.append((
        url: parsed.url,
        identifier: parsed.identifier,
        current: parsed.isCurrent,
        isDefault: parsed.isDeviceDefault,
        description: description
    ))
    return true
}

func strictCurrentProfile(in records: [ProfileRecord], display: DisplayIdentity) throws -> ProfileRecord {
    let current = records.filter(\.isCurrent)
    guard !current.isEmpty else { throw DisplayColorError.currentProfileMissing(display) }
    guard current.count == 1, let selected = current.first else {
        throw DisplayColorError.currentProfileAmbiguous(display, count: current.count)
    }
    return selected
}

public final class SystemColorProfileAdapter: ColorProfileSystem, @unchecked Sendable {
    public init() {}

    public func profileState(for display: DisplayIdentity) async throws -> ProfileState {
        let uuid = try makeUUID(display)
        guard let deviceClass = kColorSyncDisplayDeviceClass?.takeUnretainedValue(),
              let customProfilesKey = kColorSyncCustomProfiles?.takeUnretainedValue(),
              let defaultProfileID = kColorSyncDeviceDefaultProfileID?.takeUnretainedValue() else {
            throw DisplayColorError.profileEnumerationFailed(display, "required ColorSync constants are unavailable")
        }
        guard let unmanagedDeviceInfo = ColorSyncDeviceCopyDeviceInfo(deviceClass, uuid) else {
            throw DisplayColorError.colorSyncDeviceInfoUnavailable(display)
        }
        let deviceInfo = unmanagedDeviceInfo.takeRetainedValue()
        var deviceDiagnostics: [String] = []
        let customProfiles = CFDictionaryValue.dictionary(deviceInfo, key: customProfilesKey)
        if CFDictionaryValue.object(deviceInfo, key: customProfilesKey) != nil, customProfiles == nil {
            deviceDiagnostics.append("ColorSync custom-profile state is not a dictionary.")
        }
        let customDefaultValue = customProfiles.flatMap { CFDictionaryValue.object($0, key: defaultProfileID) }
        let customDefaultURL = customProfiles
            .flatMap { CFDictionaryValue.url($0, key: defaultProfileID) }
            .map { ($0 as URL).standardizedFileURL }
        if customDefaultValue != nil, customDefaultURL == nil {
            deviceDiagnostics.append("ColorSync default custom-profile mapping is not a URL.")
        }

        let context = ProfileIterationContext(requestedUUID: uuid)
        ColorSyncIterateDeviceProfiles(profileIterationCallback, Unmanaged.passUnretained(context).toOpaque())

        let records = context.entries.map { entry in
            ProfileRecord(
                url: entry.url,
                profileIdentifier: entry.identifier,
                description: entry.description,
                isCurrent: entry.current,
                isDeviceDefault: entry.isDefault,
                isCustomAssignment: customDefaultURL.map { Self.sameFile($0, entry.url) } ?? false
            )
        }
        let selected = try strictCurrentProfile(in: records, display: display)
        return ProfileState(profiles: records, current: selected, customDefaultURL: customDefaultURL, diagnostics: deviceDiagnostics + context.diagnostics)
    }

    public func factoryProfile(for display: DisplayIdentity) async throws -> ProfileRecord {
        let state = try await profileState(for: display)
        if let explicit = state.profiles.first(where: { $0.isDeviceDefault && !$0.isCustomAssignment }) {
            return explicit
        }
        if let fallback = state.profiles.first(where: { !$0.isCustomAssignment }) {
            return ProfileRecord(
                url: fallback.url,
                profileIdentifier: fallback.profileIdentifier,
                description: fallback.description,
                isCurrent: fallback.isCurrent,
                isDeviceDefault: fallback.isDeviceDefault,
                isCustomAssignment: false,
                isFactoryCandidateInferred: true,
                digest: fallback.digest
            )
        }
        throw DisplayColorError.factoryProfileUnavailable(display)
    }

    public func setCustomDefaultProfile(_ url: URL?, for display: DisplayIdentity) async throws {
        let uuid = try makeUUID(display)
        guard let deviceClass = kColorSyncDisplayDeviceClass?.takeUnretainedValue(),
              let defaultProfileID = kColorSyncDeviceDefaultProfileID?.takeUnretainedValue() else {
            throw DisplayColorError.system("Required ColorSync assignment constants are unavailable.")
        }
        let assignments = NSMutableDictionary()
        if let url {
            assignments[defaultProfileID as String] = url.standardizedFileURL as NSURL
        }
        guard ColorSyncDeviceSetCustomProfiles(deviceClass, uuid, assignments as CFDictionary) else {
            throw DisplayColorError.customProfileAssignmentRejected(display)
        }
    }

    private func makeUUID(_ display: DisplayIdentity) throws -> CFUUID {
        guard let uuid = CFUUIDCreateFromString(kCFAllocatorDefault, display.rawValue as CFString) else {
            throw DisplayColorError.displayNotFound(display)
        }
        return uuid
    }

    static func sameFile(_ lhs: URL, _ rhs: URL) -> Bool {
        lhs.standardizedFileURL.resolvingSymlinksInPath() == rhs.standardizedFileURL.resolvingSymlinksInPath()
    }
}
