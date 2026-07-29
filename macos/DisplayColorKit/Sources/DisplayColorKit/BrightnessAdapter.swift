import Foundation
import IOKit
import IOKit.graphics

public final class IOKitBrightnessAdapter: BrightnessControlling, @unchecked Sendable {
    public init() {}

    public func supportsBrightness(for display: DisplayRecord) async throws -> Bool {
        do {
            return try IOKitDisplayCatalog.withUniqueService(for: display) { service in
                var value: Float = 0
                let result = IODisplayGetFloatParameter(service, 0, kIODisplayBrightnessKey as CFString, &value)
                return result == KERN_SUCCESS && value.isFinite
            }
        } catch let error as DisplayColorError {
            if case .brightnessUnsupported = error { return false }
            throw error
        }
    }

    public func setBrightness(_ value: Float, for display: DisplayRecord) async throws {
        guard value.isFinite else {
            throw DisplayColorError.brightnessIO(display.identity, code: kIOReturnBadArgument)
        }
        let clamped = min(1, max(0, value))
        try IOKitDisplayCatalog.withUniqueService(for: display) { service in
            let result = IODisplaySetFloatParameter(service, 0, kIODisplayBrightnessKey as CFString, clamped)
            guard result == KERN_SUCCESS else {
                if result == kIOReturnUnsupported { throw DisplayColorError.brightnessUnsupported(display.identity) }
                throw DisplayColorError.brightnessIO(display.identity, code: result)
            }
        }
    }
}
