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

private func profileIterationCallback(_ information: CFDictionary?, _ rawContext: UnsafeMutableRawPointer?) -> Bool {
    guard let information, let rawContext else { return true }
    let context = Unmanaged<ProfileIterationContext>.fromOpaque(rawContext).takeUnretainedValue()

    guard let classKey = kColorSyncDeviceClass?.takeUnretainedValue(),
          let displayClass = kColorSyncDisplayDeviceClass?.takeUnretainedValue(),
          let idKey = kColorSyncDeviceID?.takeUnretainedValue(),
          let profileURLKey = kColorSyncDeviceProfileURL?.takeUnretainedValue(),
          let currentKey = kColorSyncDeviceProfileIsCurrent?.takeUnretainedValue(),
          let profileIDKey = kColorSyncDeviceProfileID?.takeUnretainedValue(),
          let defaultProfileID = kColorSyncDeviceDefaultProfileID?.takeUnretainedValue() else {
        context.diagnostics.append("Required ColorSync constants were unavailable.")
        return false
    }
    guard let entryClass = CFDictionaryValue.string(information, key: classKey) else {
        context.diagnostics.append("Profile entry has a missing or non-string device class.")
        return true
    }
    guard CFEqual(entryClass, displayClass) else { return true }
    guard let entryUUID = CFDictionaryValue.uuid(information, key: idKey) else {
        context.diagnostics.append("Display profile entry has a missing or non-UUID device ID.")
        return true
    }
    guard CFEqual(entryUUID, context.requestedUUID) else { return true }
    guard let cfURL = CFDictionaryValue.url(information, key: profileURLKey) else {
        context.diagnostics.append("Matching profile entry has a missing or non-URL profile URL.")
        return true
    }
    guard let current = CFDictionaryValue.boolean(information, key: currentKey) else {
        context.diagnostics.append("Matching profile entry has a missing or non-Boolean current flag.")
        return true
    }

    let profileID = CFDictionaryValue.string(information, key: profileIDKey)
    let isDefault = profileID.map { CFEqual($0, defaultProfileID) } ?? false
    let url = cfURL as URL
    var creationError: Unmanaged<CFError>?
    var description: String?
    if let unmanagedProfile = ColorSyncProfileCreateWithURL(cfURL, &creationError) {
        let profile = unmanagedProfile.takeRetainedValue()
        description = ColorSyncProfileCopyDescriptionString(profile)?.takeRetainedValue() as String?
    } else {
        let detail = creationError?.takeRetainedValue().localizedDescription ?? "unknown profile creation error"
        context.diagnostics.append("Could not open \(url.lastPathComponent): \(detail)")
    }
    context.entries.append((
        url: url.standardizedFileURL,
        identifier: profileID as String?,
        current: current,
        isDefault: isDefault,
        description: description
    ))
    return true
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
        let customProfiles = CFDictionaryValue.dictionary(deviceInfo, key: customProfilesKey)
        let customDefaultURL = customProfiles.flatMap { CFDictionaryValue.url($0, key: defaultProfileID) }.map { ($0 as URL).standardizedFileURL }

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
        let current = records.filter(\.isCurrent)
        guard !current.isEmpty else { throw DisplayColorError.currentProfileMissing(display) }
        guard current.count == 1, let selected = current.first else {
            throw DisplayColorError.currentProfileAmbiguous(display, count: current.count)
        }
        return ProfileState(profiles: records, current: selected, customDefaultURL: customDefaultURL, diagnostics: context.diagnostics)
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
        guard ColorSyncDeviceSetCustomProfiles(deviceClass, uuid, assignments) else {
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
