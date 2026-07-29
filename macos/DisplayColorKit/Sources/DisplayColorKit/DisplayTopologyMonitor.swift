import CoreGraphics
import Foundation
import OSLog

private func displayReconfigurationCallback(
    displayID: CGDirectDisplayID,
    flags: CGDisplayChangeSummaryFlags,
    userInfo: UnsafeMutableRawPointer?
) {
    guard let userInfo else { return }
    let monitor = Unmanaged<DisplayTopologyMonitor>.fromOpaque(userInfo).takeUnretainedValue()
    monitor.enqueue(displayID: displayID, flags: flags)
}

public final class DisplayTopologyMonitor: @unchecked Sendable {
    public typealias Handler = @Sendable (DisplayTopologyEvent) -> Void

    private let handler: Handler
    private let queue: DispatchQueue
    private let lock = NSLock()
    private var registered = false
    private let logger = Logger(subsystem: "DisplayColorKit", category: "Topology")

    public init(queue: DispatchQueue = DispatchQueue(label: "DisplayColorKit.topology"), handler: @escaping Handler) {
        self.queue = queue
        self.handler = handler
    }

    public func start() throws {
        lock.lock()
        defer { lock.unlock() }
        guard !registered else { return }
        let result = CGDisplayRegisterReconfigurationCallback(
            displayReconfigurationCallback,
            Unmanaged.passUnretained(self).toOpaque()
        )
        guard result == .success else {
            throw DisplayColorError.system("CGDisplayRegisterReconfigurationCallback failed with CGError \(result.rawValue).")
        }
        registered = true
    }

    public func stop() throws {
        lock.lock()
        defer { lock.unlock() }
        guard registered else { return }
        let result = CGDisplayRemoveReconfigurationCallback(
            displayReconfigurationCallback,
            Unmanaged.passUnretained(self).toOpaque()
        )
        guard result == .success else {
            throw DisplayColorError.system("CGDisplayRemoveReconfigurationCallback failed with CGError \(result.rawValue).")
        }
        registered = false
    }

    fileprivate func enqueue(displayID: CGDirectDisplayID, flags: CGDisplayChangeSummaryFlags) {
        let event: DisplayTopologyEvent = flags.contains(.beginConfigurationFlag)
            ? .willChange
            : .didChange(displayID: displayID, flags: flags.rawValue)
        queue.async { [handler] in handler(event) }
    }

    deinit {
        lock.lock()
        let needsRemoval = registered
        registered = false
        lock.unlock()
        if needsRemoval {
            let result = CGDisplayRemoveReconfigurationCallback(
                displayReconfigurationCallback,
                Unmanaged.passUnretained(self).toOpaque()
            )
            if result != .success {
                logger.error("Callback removal during deinit failed; CGError=\(result.rawValue, privacy: .public)")
            }
        }
    }
}
