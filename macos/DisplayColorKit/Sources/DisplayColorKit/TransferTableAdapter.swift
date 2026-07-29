import CoreGraphics
import Foundation

public final class CoreGraphicsTransferTableAdapter: TransferTableSystem, @unchecked Sendable {
    private let displays: any DisplayHardwareSystem
    private let readbackTolerance: Float

    public init(displays: any DisplayHardwareSystem, readbackTolerance: Float = 1.0 / 65_535.0) {
        self.displays = displays
        self.readbackTolerance = readbackTolerance
    }

    public func capture(for display: DisplayIdentity) async throws -> TransferTable {
        let displayID = try await displays.resolveDisplayID(for: display)
        let capacity = CGDisplayGammaTableCapacity(displayID)
        guard capacity > 0, capacity <= UInt32(TransferTable.maximumSampleCount) else {
            throw DisplayColorError.transferTableCapture(display, code: CGError.failure.rawValue)
        }
        var red = [CGGammaValue](repeating: 0, count: Int(capacity))
        var green = [CGGammaValue](repeating: 0, count: Int(capacity))
        var blue = [CGGammaValue](repeating: 0, count: Int(capacity))
        var actual: UInt32 = 0
        let result = red.withUnsafeMutableBufferPointer { redBuffer in
            green.withUnsafeMutableBufferPointer { greenBuffer in
                blue.withUnsafeMutableBufferPointer { blueBuffer in
                    CGGetDisplayTransferByTable(
                        displayID,
                        capacity,
                        redBuffer.baseAddress,
                        greenBuffer.baseAddress,
                        blueBuffer.baseAddress,
                        &actual
                    )
                }
            }
        }
        guard result == .success, actual > 0, actual <= capacity else {
            throw DisplayColorError.transferTableCapture(display, code: result.rawValue)
        }
        do {
            return try TransferTable(
                red: Array(red.prefix(Int(actual))),
                green: Array(green.prefix(Int(actual))),
                blue: Array(blue.prefix(Int(actual)))
            )
        } catch {
            throw DisplayColorError.transferTableReadback(display, error.displayColorDescription)
        }
    }

    public func apply(_ table: TransferTable, to display: DisplayIdentity) async throws -> TransferVerification {
        guard table.count > 0, table.count <= Int(UInt32.max) else {
            throw DisplayColorError.transferTableValidation("sample count is not representable by Core Graphics")
        }
        let displayID = try await displays.resolveDisplayID(for: display)
        let result = table.red.withUnsafeBufferPointer { redBuffer in
            table.green.withUnsafeBufferPointer { greenBuffer in
                table.blue.withUnsafeBufferPointer { blueBuffer in
                    CGSetDisplayTransferByTable(
                        displayID,
                        UInt32(table.count),
                        redBuffer.baseAddress,
                        greenBuffer.baseAddress,
                        blueBuffer.baseAddress
                    )
                }
            }
        }
        guard result == .success else {
            throw DisplayColorError.transferTableWrite(display, code: result.rawValue)
        }
        do {
            let readback = try await capture(for: display)
            guard table.approximatelyEquals(readback, tolerance: readbackTolerance) else {
                throw DisplayColorError.transferTableReadback(display, "readback differs from the requested table")
            }
            return .verified
        } catch let error as DisplayColorError {
            switch error {
            case .transferTableCapture:
                return .acceptedReadbackUnavailable(reason: error.errorDescription ?? "Core Graphics readback unavailable")
            default:
                throw error
            }
        }
    }

    public func restoreColorSyncSettingsFallback() async {
        CGDisplayRestoreColorSyncSettings()
    }
}
