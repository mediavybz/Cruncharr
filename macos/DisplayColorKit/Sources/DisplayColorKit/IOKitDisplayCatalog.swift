import Foundation
import IOKit
import IOKit.graphics
import OSLog

struct IOKitDisplayDescriptor: Sendable {
    let vendorID: UInt32
    let productID: UInt32
    let serialNumber: UInt32
    let localizedName: String?
}

enum IOKitDisplayCatalog {
    private static let logger = Logger(subsystem: "DisplayColorKit", category: "IOKit")

    static func descriptors() throws -> [IOKitDisplayDescriptor] {
        try withDisplayServices { service, information in
            IOKitDisplayDescriptor(
                vendorID: number(information[kDisplayVendorID]) ?? 0,
                productID: number(information[kDisplayProductID]) ?? 0,
                serialNumber: number(information[kDisplaySerialNumber]) ?? 0,
                localizedName: productName(information[kDisplayProductName])
            )
        }
    }

    static func matchingServiceCount(for display: DisplayRecord) throws -> Int {
        try matchingServices(for: display) { services in services.count }
    }

    static func withUniqueService<T>(for display: DisplayRecord, body: (io_service_t) throws -> T) throws -> T {
        try matchingServices(for: display) { services in
            guard services.count == 1, let service = services.first else {
                if services.isEmpty { throw DisplayColorError.brightnessUnsupported(display.identity) }
                throw DisplayColorError.ambiguousDisplayService(display.identity, matches: services.count)
            }
            return try body(service)
        }
    }

    private static func matchingServices<T>(for display: DisplayRecord, body: ([io_service_t]) throws -> T) throws -> T {
        guard let matching = IOServiceMatching("IODisplayConnect") else {
            throw DisplayColorError.system("IOServiceMatching could not create an IODisplayConnect query.")
        }
        var iterator: io_iterator_t = 0
        let result = IOServiceGetMatchingServices(kIOMainPortDefault, matching, &iterator)
        guard result == KERN_SUCCESS else {
            throw DisplayColorError.system("IOServiceGetMatchingServices failed with IOReturn \(result).")
        }
        defer { release(iterator, kind: "iterator") }

        var matches: [io_service_t] = []
        while true {
            let service = IOIteratorNext(iterator)
            guard service != 0 else { break }
            guard let unmanagedInfo = IODisplayCreateInfoDictionary(service, IOOptionBits(kIODisplayOnlyPreferredName)) else {
                release(service, kind: "display service")
                continue
            }
            let information = unmanagedInfo.takeRetainedValue() as NSDictionary
            let vendor = number(information[kDisplayVendorID]) ?? 0
            let product = number(information[kDisplayProductID]) ?? 0
            let serial = number(information[kDisplaySerialNumber]) ?? 0
            if vendor == display.vendorID, product == display.productID, serial == display.serialNumber {
                matches.append(service)
            } else {
                release(service, kind: "nonmatching display service")
            }
        }
        defer { matches.forEach { release($0, kind: "matched display service") } }
        return try body(matches)
    }

    private static func withDisplayServices<T>(_ transform: (io_service_t, NSDictionary) throws -> T) throws -> [T] {
        guard let matching = IOServiceMatching("IODisplayConnect") else {
            throw DisplayColorError.system("IOServiceMatching could not create an IODisplayConnect query.")
        }
        var iterator: io_iterator_t = 0
        let result = IOServiceGetMatchingServices(kIOMainPortDefault, matching, &iterator)
        guard result == KERN_SUCCESS else {
            throw DisplayColorError.system("IOServiceGetMatchingServices failed with IOReturn \(result).")
        }
        defer { release(iterator, kind: "iterator") }

        var output: [T] = []
        while true {
            let service = IOIteratorNext(iterator)
            guard service != 0 else { break }
            do {
                defer { release(service, kind: "display service") }
                guard let unmanagedInfo = IODisplayCreateInfoDictionary(service, IOOptionBits(kIODisplayOnlyPreferredName)) else {
                    continue
                }
                output.append(try transform(service, unmanagedInfo.takeRetainedValue() as NSDictionary))
            }
        }
        return output
    }

    private static func number(_ value: Any?) -> UInt32? {
        guard let number = value as? NSNumber else { return nil }
        return number.uint32Value
    }

    private static func productName(_ value: Any?) -> String? {
        guard let names = value as? [String: String], !names.isEmpty else { return nil }
        let preferred = Locale.preferredLanguages
        for language in preferred {
            if let exact = names[language] { return exact }
            if let base = language.split(separator: "-").first.flatMap({ names[String($0)] }) { return base }
        }
        return names.sorted { $0.key < $1.key }.first?.value
    }

    private static func release(_ object: io_object_t, kind: String) {
        guard object != 0 else { return }
        let result = IOObjectRelease(object)
        if result != KERN_SUCCESS {
            logger.error("Failed to release \(kind, privacy: .public); IOReturn=\(result, privacy: .public)")
        }
    }
}
