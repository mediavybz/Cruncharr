import AppKit
import ColorSync
import CoreGraphics
import Foundation
import OSLog

public final class CoreGraphicsDisplaySystem: DisplayHardwareSystem, @unchecked Sendable {
    private let brightness: (any BrightnessControlling)?
    private let logger = Logger(subsystem: "DisplayColorKit", category: "Inventory")

    public init(brightness: (any BrightnessControlling)? = nil) {
        self.brightness = brightness
    }

    public func activeDisplays() async throws -> [DisplayRecord] {
        let displayIDs = try Self.activeDisplayIDs()
        let screenNames: [CGDirectDisplayID: String] = await MainActor.run {
            var result: [CGDirectDisplayID: String] = [:]
            for screen in NSScreen.screens {
                let key = NSDeviceDescriptionKey("NSScreenNumber")
                guard let number = screen.deviceDescription[key] as? NSNumber else { continue }
                result[number.uint32Value] = screen.localizedName
            }
            return result
        }
        let ioDescriptors: [IOKitDisplayDescriptor]
        do {
            ioDescriptors = try IOKitDisplayCatalog.descriptors()
        } catch {
            logger.warning("IOKit display metadata is unavailable: \(error.displayColorDescription, privacy: .public)")
            ioDescriptors = []
        }

        var records: [DisplayRecord] = []
        for displayID in displayIDs {
            guard let unmanagedUUID = CGDisplayCreateUUIDFromDisplayID(displayID) else {
                continue
            }
            let cfUUID = unmanagedUUID.takeRetainedValue()
            guard let string = CFUUIDCreateString(kCFAllocatorDefault, cfUUID) as String?,
                  let identity = DisplayIdentity(rawValue: string) else {
                continue
            }

            let vendor = CGDisplayVendorNumber(displayID)
            let product = CGDisplayModelNumber(displayID)
            let serial = CGDisplaySerialNumber(displayID)
            let ioNames = ioDescriptors.filter {
                $0.vendorID == vendor && $0.productID == product && $0.serialNumber == serial
            }
            let fallbackName = ioNames.count == 1 ? ioNames[0].localizedName : nil
            let bounds = CGDisplayBounds(displayID)
            let physical = CGDisplayScreenSize(displayID)
            let gammaCapacity = CGDisplayGammaTableCapacity(displayID)
            var capabilities: DisplayCapabilities = []
            if gammaCapacity > 0 {
                capabilities.formUnion([.transferTableRead, .transferTableWrite])
            }

            var record = DisplayRecord(
                displayID: displayID,
                identity: identity,
                localizedName: screenNames[displayID] ?? fallbackName ?? "Display \(identity.rawValue.prefix(8))",
                vendorID: vendor,
                productID: product,
                serialNumber: serial,
                isBuiltin: CGDisplayIsBuiltin(displayID) != 0,
                isOnline: CGDisplayIsOnline(displayID) != 0,
                isMirrored: CGDisplayMirrorsDisplay(displayID) != kCGNullDirectDisplay,
                pixelSize: DisplaySize(width: Double(CGDisplayPixelsWide(displayID)), height: Double(CGDisplayPixelsHigh(displayID))),
                bounds: DisplayRectangle(
                    origin: DisplayPoint(x: bounds.origin.x, y: bounds.origin.y),
                    size: DisplaySize(width: bounds.size.width, height: bounds.size.height)
                ),
                physicalSizeMillimeters: DisplaySize(width: physical.width, height: physical.height),
                capabilities: capabilities
            )
            if let brightness {
                do {
                    if try await brightness.supportsBrightness(for: record) {
                        record.capabilities.insert(.brightness)
                    }
                } catch {
                    logger.warning("Brightness probing failed for display=\(identity.rawValue, privacy: .public): \(error.displayColorDescription, privacy: .public)")
                }
            }
            records.append(record)
        }
        return records
    }

    public func resolveDisplayID(for identity: DisplayIdentity) async throws -> UInt32 {
        guard let uuid = CFUUIDCreateFromString(kCFAllocatorDefault, identity.rawValue as CFString) else {
            throw DisplayColorError.displayNotFound(identity)
        }
        let displayID = CGDisplayGetDisplayIDFromUUID(uuid)
        guard displayID != kCGNullDirectDisplay, CGDisplayIsActive(displayID) != 0 else {
            throw DisplayColorError.displayDisconnected(identity)
        }
        guard let currentUUID = CGDisplayCreateUUIDFromDisplayID(displayID)?.takeRetainedValue(),
              CFEqual(currentUUID, uuid) else {
            throw DisplayColorError.displayChangedDuringOperation(identity)
        }
        return displayID
    }

    private static func activeDisplayIDs(maximumAttempts: Int = 3) throws -> [CGDirectDisplayID] {
        for _ in 0..<maximumAttempts {
            var expected: UInt32 = 0
            let countResult = CGGetActiveDisplayList(0, nil, &expected)
            guard countResult == .success else {
                throw DisplayColorError.system("CGGetActiveDisplayList count failed with CGError \(countResult.rawValue).")
            }
            guard expected > 0 else { return [] }

            var ids = [CGDirectDisplayID](repeating: 0, count: Int(expected))
            var actual: UInt32 = expected
            let listResult = ids.withUnsafeMutableBufferPointer {
                CGGetActiveDisplayList(expected, $0.baseAddress, &actual)
            }
            guard listResult == .success else {
                throw DisplayColorError.system("CGGetActiveDisplayList population failed with CGError \(listResult.rawValue).")
            }

            var confirmed: UInt32 = 0
            let confirmResult = CGGetActiveDisplayList(0, nil, &confirmed)
            guard confirmResult == .success else {
                throw DisplayColorError.system("CGGetActiveDisplayList confirmation failed with CGError \(confirmResult.rawValue).")
            }
            if confirmed <= expected, actual <= expected {
                return Array(ids.prefix(Int(actual)))
            }
        }
        throw DisplayColorError.system("Display topology changed repeatedly during enumeration.")
    }
}

public actor DisplayInventory {
    private let displays: any DisplayHardwareSystem
    private let profiles: any ColorProfileSystem

    public init(displays: any DisplayHardwareSystem, profiles: any ColorProfileSystem) {
        self.displays = displays
        self.profiles = profiles
    }

    public func snapshot() async throws -> [DisplayRecord] {
        var records = try await displays.activeDisplays()
        for index in records.indices {
            do {
                let state = try await profiles.profileState(for: records[index].identity)
                records[index].currentProfileURL = state.current.url
                records[index].baselineProfileURL = state.current.url
            } catch let error as DisplayColorError {
                switch error {
                case .currentProfileMissing, .colorSyncDeviceInfoUnavailable:
                    break
                default:
                    throw error
                }
            }
        }
        return records
    }
}
