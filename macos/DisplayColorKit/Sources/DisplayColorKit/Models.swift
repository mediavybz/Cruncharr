import CoreGraphics
import Foundation

public struct DisplayIdentity: RawRepresentable, Codable, Hashable, Sendable, CustomStringConvertible {
    public let rawValue: String

    public init?(rawValue: String) {
        let normalized = rawValue.trimmingCharacters(in: .whitespacesAndNewlines).uppercased()
        guard !normalized.isEmpty else { return nil }
        self.rawValue = normalized
    }

    public var description: String { rawValue }
}

public struct DisplayPoint: Codable, Equatable, Sendable {
    public var x: Double
    public var y: Double

    public init(x: Double, y: Double) {
        self.x = x
        self.y = y
    }
}

public struct DisplaySize: Codable, Equatable, Sendable {
    public var width: Double
    public var height: Double

    public init(width: Double, height: Double) {
        self.width = width
        self.height = height
    }
}

public struct DisplayRectangle: Codable, Equatable, Sendable {
    public var origin: DisplayPoint
    public var size: DisplaySize

    public init(origin: DisplayPoint, size: DisplaySize) {
        self.origin = origin
        self.size = size
    }
}

public struct DisplayCapabilities: OptionSet, Codable, Sendable {
    public let rawValue: UInt8

    public init(rawValue: UInt8) { self.rawValue = rawValue }

    public static let transferTableRead = DisplayCapabilities(rawValue: 1 << 0)
    public static let transferTableWrite = DisplayCapabilities(rawValue: 1 << 1)
    public static let brightness = DisplayCapabilities(rawValue: 1 << 2)
}

public struct DisplayRecord: Codable, Equatable, Sendable {
    public var displayID: CGDirectDisplayID
    public let identity: DisplayIdentity
    public var localizedName: String
    public var vendorID: UInt32
    public var productID: UInt32
    public var serialNumber: UInt32
    public var isBuiltin: Bool
    public var isOnline: Bool
    public var isMirrored: Bool
    public var pixelSize: DisplaySize
    public var bounds: DisplayRectangle
    public var physicalSizeMillimeters: DisplaySize
    public var currentProfileURL: URL?
    public var baselineProfileURL: URL?
    public var capabilities: DisplayCapabilities

    public init(
        displayID: CGDirectDisplayID,
        identity: DisplayIdentity,
        localizedName: String,
        vendorID: UInt32,
        productID: UInt32,
        serialNumber: UInt32,
        isBuiltin: Bool,
        isOnline: Bool,
        isMirrored: Bool,
        pixelSize: DisplaySize,
        bounds: DisplayRectangle,
        physicalSizeMillimeters: DisplaySize,
        currentProfileURL: URL? = nil,
        baselineProfileURL: URL? = nil,
        capabilities: DisplayCapabilities = []
    ) {
        self.displayID = displayID
        self.identity = identity
        self.localizedName = localizedName
        self.vendorID = vendorID
        self.productID = productID
        self.serialNumber = serialNumber
        self.isBuiltin = isBuiltin
        self.isOnline = isOnline
        self.isMirrored = isMirrored
        self.pixelSize = pixelSize
        self.bounds = bounds
        self.physicalSizeMillimeters = physicalSizeMillimeters
        self.currentProfileURL = currentProfileURL
        self.baselineProfileURL = baselineProfileURL
        self.capabilities = capabilities
    }
}

public struct ProfileRecord: Codable, Equatable, Sendable {
    public let url: URL
    public let profileIdentifier: String?
    public let description: String?
    public let isCurrent: Bool
    public let isDeviceDefault: Bool
    public let isCustomAssignment: Bool
    public let digest: String?

    public init(
        url: URL,
        profileIdentifier: String?,
        description: String?,
        isCurrent: Bool,
        isDeviceDefault: Bool,
        isCustomAssignment: Bool,
        digest: String? = nil
    ) {
        self.url = url.standardizedFileURL
        self.profileIdentifier = profileIdentifier
        self.description = description
        self.isCurrent = isCurrent
        self.isDeviceDefault = isDeviceDefault
        self.isCustomAssignment = isCustomAssignment
        self.digest = digest
    }
}

public struct ProfileState: Equatable, Sendable {
    public let profiles: [ProfileRecord]
    public let current: ProfileRecord
    public let customDefaultURL: URL?
    public let diagnostics: [String]

    public init(profiles: [ProfileRecord], current: ProfileRecord, customDefaultURL: URL?, diagnostics: [String] = []) {
        self.profiles = profiles
        self.current = current
        self.customDefaultURL = customDefaultURL?.standardizedFileURL
        self.diagnostics = diagnostics
    }
}

public struct ChannelGain: Codable, Equatable, Sendable {
    public var red: Float
    public var green: Float
    public var blue: Float

    public init(red: Float = 1, green: Float = 1, blue: Float = 1) {
        self.red = red
        self.green = green
        self.blue = blue
    }
}

public struct TransferTable: Codable, Equatable, Sendable {
    public static let compatibilitySampleCount = 256
    public static let maximumSampleCount = 16_384

    public let red: [Float]
    public let green: [Float]
    public let blue: [Float]

    public var count: Int { red.count }

    public init(red: [Float], green: [Float], blue: [Float]) throws {
        try Self.validateChannels(red: red, green: green, blue: blue)
        self.red = red
        self.green = green
        self.blue = blue
    }

    public static func linear(sampleCount: Int = compatibilitySampleCount) throws -> TransferTable {
        guard sampleCount > 1, sampleCount <= maximumSampleCount else {
            throw DisplayColorError.transferTableValidation("sample count must be between 2 and \(maximumSampleCount)")
        }
        let denominator = Float(sampleCount - 1)
        let values = (0..<sampleCount).map { Float($0) / denominator }
        return try generated(red: values, green: values, blue: values)
    }

    public static func gammaGain(
        gamma: Float,
        gain: ChannelGain,
        base: TransferTable? = nil
    ) throws -> TransferTable {
        guard gamma.isFinite, gamma > 0 else {
            throw DisplayColorError.transferTableValidation("gamma must be finite and greater than zero")
        }
        for value in [gain.red, gain.green, gain.blue] where !value.isFinite || value < 0 {
            throw DisplayColorError.transferTableValidation("channel gains must be finite and nonnegative")
        }

        let immutableBase = try base ?? linear()
        let exponent = 1 / gamma
        func transform(_ values: [Float], gain: Float) -> [Float] {
            values.map { min(1, max(0, pow($0, exponent) * gain)) }
        }
        return try generated(
            red: transform(immutableBase.red, gain: gain.red),
            green: transform(immutableBase.green, gain: gain.green),
            blue: transform(immutableBase.blue, gain: gain.blue)
        )
    }

    public static func generated(red: [Float], green: [Float], blue: [Float]) throws -> TransferTable {
        let table = try TransferTable(red: red, green: green, blue: blue)
        for (name, channel) in [("red", red), ("green", green), ("blue", blue)] {
            guard zip(channel, channel.dropFirst()).allSatisfy({ $0 <= $1 }) else {
                throw DisplayColorError.transferTableValidation("generated \(name) channel is not monotonically nondecreasing")
            }
        }
        return table
    }

    public func approximatelyEquals(_ other: TransferTable, tolerance: Float) -> Bool {
        guard tolerance.isFinite, tolerance >= 0, count == other.count else { return false }
        return zip(red, other.red).allSatisfy { abs($0 - $1) <= tolerance }
            && zip(green, other.green).allSatisfy { abs($0 - $1) <= tolerance }
            && zip(blue, other.blue).allSatisfy { abs($0 - $1) <= tolerance }
    }

    private static func validateChannels(red: [Float], green: [Float], blue: [Float]) throws {
        guard !red.isEmpty, red.count == green.count, red.count == blue.count else {
            throw DisplayColorError.transferTableValidation("channels must have the same nonzero sample count")
        }
        guard red.count <= maximumSampleCount, UInt64(red.count) <= UInt64(UInt32.max) else {
            throw DisplayColorError.transferTableValidation("sample count exceeds the configured or Core Graphics limit")
        }
        for value in red + green + blue where !value.isFinite || !(0...1).contains(value) {
            throw DisplayColorError.transferTableValidation("all channel values must be finite and in 0...1")
        }
    }
}

public enum TransferVerification: Equatable, Sendable {
    case verified
    case acceptedReadbackUnavailable(reason: String)
}

public enum SessionStage: String, Codable, Sendable {
    case prepared
    case assigningProfile
    case verifyingProfile
    case applyingTransferTable
    case active
    case rollingBack
    case completed
    case failed
}

public struct StagedProfile: Codable, Equatable, Sendable {
    public let url: URL
    public let digest: String
    public let description: String?
    public let createdByComponent: Bool

    public init(url: URL, digest: String, description: String?, createdByComponent: Bool) {
        self.url = url.standardizedFileURL
        self.digest = digest
        self.description = description
        self.createdByComponent = createdByComponent
    }
}

public struct CalibrationRequest: Sendable {
    public let display: DisplayIdentity
    public let profileURL: URL
    public let transferTable: TransferTable?
    public let brightness: Float?
    public let brightnessIsRequired: Bool

    public init(
        display: DisplayIdentity,
        profileURL: URL,
        transferTable: TransferTable? = nil,
        brightness: Float? = nil,
        brightnessIsRequired: Bool = false
    ) {
        self.display = display
        self.profileURL = profileURL
        self.transferTable = transferTable
        self.brightness = brightness
        self.brightnessIsRequired = brightnessIsRequired
    }
}

public struct SessionSnapshot: Codable, Equatable, Sendable {
    public let display: DisplayIdentity
    public let originalProfileURL: URL
    public let originalCustomDefaultURL: URL?
    public let originalTransferTable: TransferTable

    public init(display: DisplayIdentity, originalProfileURL: URL, originalCustomDefaultURL: URL?, originalTransferTable: TransferTable) {
        self.display = display
        self.originalProfileURL = originalProfileURL.standardizedFileURL
        self.originalCustomDefaultURL = originalCustomDefaultURL?.standardizedFileURL
        self.originalTransferTable = originalTransferTable
    }
}

public struct RecoveryJournal: Codable, Equatable, Sendable, Identifiable {
    public let id: UUID
    public var stage: SessionStage
    public let snapshot: SessionSnapshot
    public var stagedProfile: StagedProfile?
    public var intendedTransferTable: TransferTable?
    public let createdAt: Date

    public init(
        id: UUID = UUID(),
        stage: SessionStage,
        snapshot: SessionSnapshot,
        stagedProfile: StagedProfile? = nil,
        intendedTransferTable: TransferTable? = nil,
        createdAt: Date = Date()
    ) {
        self.id = id
        self.stage = stage
        self.snapshot = snapshot
        self.stagedProfile = stagedProfile
        self.intendedTransferTable = intendedTransferTable
        self.createdAt = createdAt
    }
}

public struct ActiveSession: Sendable, Identifiable {
    public let id: UUID
    public let display: DisplayIdentity
    public let stagedProfile: StagedProfile
    public let transferVerification: TransferVerification?

    public init(id: UUID, display: DisplayIdentity, stagedProfile: StagedProfile, transferVerification: TransferVerification?) {
        self.id = id
        self.display = display
        self.stagedProfile = stagedProfile
        self.transferVerification = transferVerification
    }
}

public enum DisplayTopologyEvent: Sendable, Equatable {
    case willChange
    case didChange(displayID: CGDirectDisplayID, flags: UInt32)
}
