import ColorSync
import CryptoKit
import Foundation
import OSLog

public struct ICCValidationResult: Equatable, Sendable {
    public let description: String?
    public let warnings: [String]

    public init(description: String?, warnings: [String] = []) {
        self.description = description
        self.warnings = warnings
    }
}

public protocol ICCProfileValidating: Sendable {
    func validateDisplayProfile(at url: URL) throws -> ICCValidationResult
}

public struct SystemICCProfileValidator: ICCProfileValidating {
    public init() {}

    public func validateDisplayProfile(at url: URL) throws -> ICCValidationResult {
        var creationError: Unmanaged<CFError>?
        guard let unmanagedProfile = ColorSyncProfileCreateWithURL(url as CFURL, &creationError) else {
            let detail = creationError?.takeRetainedValue().localizedDescription ?? "ColorSync could not construct a profile"
            throw DisplayColorError.invalidProfile(url, detail)
        }
        let profile = unmanagedProfile.takeRetainedValue()

        var verificationError: Unmanaged<CFError>?
        var verificationWarning: Unmanaged<CFError>?
        let isValid = ColorSyncProfileVerify(profile, &verificationError, &verificationWarning)
        let validationDetail = verificationError?.takeRetainedValue().localizedDescription
        let warningDetail = verificationWarning?.takeRetainedValue().localizedDescription
        guard isValid else {
            throw DisplayColorError.profileValidation(url, validationDetail ?? "ColorSyncProfileVerify returned false")
        }

        guard let unmanagedHeader = ColorSyncProfileCopyHeader(profile) else {
            throw DisplayColorError.invalidProfile(url, "profile header is unavailable")
        }
        let header = unmanagedHeader.takeRetainedValue() as Data
        guard header.count >= 16 else {
            throw DisplayColorError.invalidProfile(url, "ICC header is shorter than 16 bytes")
        }
        let profileClass = String(bytes: header[12..<16], encoding: .ascii)
        guard profileClass == "mntr" else {
            throw DisplayColorError.invalidProfile(url, "ICC profile class is \(profileClass ?? "unreadable"), not display/monitor (mntr)")
        }
        let description = ColorSyncProfileCopyDescriptionString(profile)?.takeRetainedValue() as String?
        return ICCValidationResult(description: description, warnings: warningDetail.map { [$0] } ?? [])
    }
}

public actor UserColorSyncProfileStore: ProfileFileStoring {
    private let fileManager: FileManager
    private let destinationOverride: URL?
    private let validator: any ICCProfileValidating
    private let logger = Logger(subsystem: "DisplayColorKit", category: "ProfileStore")

    public init(
        fileManager: FileManager = .default,
        destinationOverride: URL? = nil,
        validator: any ICCProfileValidating = SystemICCProfileValidator()
    ) {
        self.fileManager = fileManager
        self.destinationOverride = destinationOverride
        self.validator = validator
    }

    public func stageProfile(from sourceURL: URL) async throws -> StagedProfile {
        let source = sourceURL.standardizedFileURL
        let didAccess = source.startAccessingSecurityScopedResource()
        defer { if didAccess { source.stopAccessingSecurityScopedResource() } }

        let resource: URLResourceValues
        do {
            resource = try source.resourceValues(forKeys: [.isRegularFileKey, .isReadableKey])
        } catch {
            throw DisplayColorError.invalidProfile(source, error.localizedDescription)
        }
        guard resource.isRegularFile == true, resource.isReadable != false, fileManager.isReadableFile(atPath: source.path) else {
            throw DisplayColorError.invalidProfile(source, "source must be an existing readable regular file")
        }
        guard ["icc", "icm"].contains(source.pathExtension.lowercased()) else {
            throw DisplayColorError.invalidProfile(source, "expected an .icc or .icm file")
        }

        let validation = try validator.validateDisplayProfile(at: source)
        for warning in validation.warnings {
            logger.warning("ColorSync profile validation warning for \(source.lastPathComponent, privacy: .public): \(warning, privacy: .public)")
        }

        let sourceData: Data
        do {
            sourceData = try Data(contentsOf: source, options: .mappedIfSafe)
        } catch {
            throw DisplayColorError.profileStaging("could not read source bytes: \(error.localizedDescription)")
        }
        let digest = Self.sha256(sourceData)
        let directory = try profileDirectory()
        try createDirectoryIfNeeded(directory)

        let destination = try collisionSafeDestination(source: source, digest: digest, directory: directory)
        if fileManager.fileExists(atPath: destination.path) {
            let existingDigest = try digestOfFile(destination)
            guard existingDigest == digest else {
                throw DisplayColorError.profileStaging("collision-safe destination unexpectedly contains unrelated bytes")
            }
            return StagedProfile(url: destination, digest: digest, description: validation.description, createdByComponent: false)
        }

        let temporary = directory.appendingPathComponent(".displaycolorkit-\(UUID().uuidString)-\(destination.lastPathComponent)")
        defer {
            if fileManager.fileExists(atPath: temporary.path) {
                do { try fileManager.removeItem(at: temporary) }
                catch { logger.error("Temporary profile cleanup failed: \(error.localizedDescription, privacy: .public)") }
            }
        }
        do {
            try fileManager.copyItem(at: source, to: temporary)
            guard try digestOfFile(temporary) == digest else {
                throw DisplayColorError.profileStaging("temporary copy digest does not match the source")
            }
            do {
                try fileManager.moveItem(at: temporary, to: destination)
            } catch {
                if fileManager.fileExists(atPath: destination.path), try digestOfFile(destination) == digest {
                    return StagedProfile(url: destination, digest: digest, description: validation.description, createdByComponent: false)
                }
                throw error
            }
        } catch let error as DisplayColorError {
            throw error
        } catch {
            if (error as NSError).domain == NSCocoaErrorDomain,
               [NSFileWriteNoPermissionError, NSFileReadNoPermissionError].contains((error as NSError).code) {
                throw DisplayColorError.sandboxConfiguration(error.localizedDescription)
            }
            throw DisplayColorError.profileStaging(error.localizedDescription)
        }
        return StagedProfile(url: destination, digest: digest, description: validation.description, createdByComponent: true)
    }

    public func removeIfOwned(_ profile: StagedProfile) async throws {
        guard profile.createdByComponent else { return }
        guard fileManager.fileExists(atPath: profile.url.path) else { return }
        guard try digestOfFile(profile.url) == profile.digest else {
            throw DisplayColorError.profileStaging("refusing to delete a staged path whose contents changed")
        }
        do {
            try fileManager.removeItem(at: profile.url)
        } catch {
            throw DisplayColorError.profileStaging("could not remove component-created profile: \(error.localizedDescription)")
        }
    }

    private func profileDirectory() throws -> URL {
        if let destinationOverride { return destinationOverride.standardizedFileURL }
        do {
            let library = try fileManager.url(for: .libraryDirectory, in: .userDomainMask, appropriateFor: nil, create: false)
            return library.appendingPathComponent("ColorSync", isDirectory: true).appendingPathComponent("Profiles", isDirectory: true)
        } catch {
            throw DisplayColorError.profileDirectoryUnavailable(error.localizedDescription)
        }
    }

    private func createDirectoryIfNeeded(_ directory: URL) throws {
        do {
            try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        } catch {
            let nsError = error as NSError
            if nsError.domain == NSCocoaErrorDomain && nsError.code == NSFileWriteNoPermissionError {
                throw DisplayColorError.sandboxConfiguration(error.localizedDescription)
            }
            throw DisplayColorError.profileDirectoryUnavailable(error.localizedDescription)
        }
    }

    private func collisionSafeDestination(source: URL, digest: String, directory: URL) throws -> URL {
        let requested = directory.appendingPathComponent(source.lastPathComponent)
        guard fileManager.fileExists(atPath: requested.path) else { return requested }
        if try digestOfFile(requested) == digest { return requested }
        let base = source.deletingPathExtension().lastPathComponent
        let ext = source.pathExtension.lowercased()
        let suffixed = directory.appendingPathComponent("\(base)-\(digest.prefix(12)).\(ext)")
        if fileManager.fileExists(atPath: suffixed.path), try digestOfFile(suffixed) != digest {
            return directory.appendingPathComponent("\(base)-\(digest).\(ext)")
        }
        return suffixed
    }

    private func digestOfFile(_ url: URL) throws -> String {
        do { return Self.sha256(try Data(contentsOf: url, options: .mappedIfSafe)) }
        catch { throw DisplayColorError.profileStaging("could not hash \(url.lastPathComponent): \(error.localizedDescription)") }
    }

    private static func sha256(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
}
