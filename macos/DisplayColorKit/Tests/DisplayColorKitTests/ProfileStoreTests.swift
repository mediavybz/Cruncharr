import Foundation
import XCTest
@testable import DisplayColorKit

private struct AcceptingICCValidator: ICCProfileValidating {
    func validateDisplayProfile(at url: URL) throws -> ICCValidationResult {
        ICCValidationResult(description: "Accepted test profile")
    }
}

private struct RejectingICCValidator: ICCProfileValidating {
    func validateDisplayProfile(at url: URL) throws -> ICCValidationResult {
        throw DisplayColorError.profileValidation(url, "injected corrupt profile")
    }
}

final class ProfileStoreTests: XCTestCase {
    func testMissingFileAndWrongExtensionAreRejected() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { XCTAssertNoThrow(try FileManager.default.removeItem(at: root)) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let store = UserColorSyncProfileStore(destinationOverride: root.appendingPathComponent("profiles"), validator: AcceptingICCValidator())

        await XCTAssertThrowsErrorAsync(try await store.stageProfile(from: root.appendingPathComponent("missing.icc")))
        let text = root.appendingPathComponent("profile.txt")
        try Data("data".utf8).write(to: text)
        await XCTAssertThrowsErrorAsync(try await store.stageProfile(from: text))
    }

    func testValidatorFailureIsPreserved() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { XCTAssertNoThrow(try FileManager.default.removeItem(at: root)) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let source = root.appendingPathComponent("corrupt.icc")
        try Data("not an ICC".utf8).write(to: source)
        let store = UserColorSyncProfileStore(destinationOverride: root.appendingPathComponent("profiles"), validator: RejectingICCValidator())

        do {
            _ = try await store.stageProfile(from: source)
            XCTFail("Expected validation failure")
        } catch let error as DisplayColorError {
            guard case .profileValidation = error else { return XCTFail("Unexpected error: \(error)") }
        }
    }

    func testSameNameSameDigestReusesExistingFile() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { XCTAssertNoThrow(try FileManager.default.removeItem(at: root)) }
        let sourceDirectory = root.appendingPathComponent("source", isDirectory: true)
        let destination = root.appendingPathComponent("profiles", isDirectory: true)
        try FileManager.default.createDirectory(at: sourceDirectory, withIntermediateDirectories: true)
        let source = sourceDirectory.appendingPathComponent("calibration.icc")
        try Data("same bytes".utf8).write(to: source)
        let store = UserColorSyncProfileStore(destinationOverride: destination, validator: AcceptingICCValidator())

        let first = try await store.stageProfile(from: source)
        let second = try await store.stageProfile(from: source)

        XCTAssertTrue(first.createdByComponent)
        XCTAssertFalse(second.createdByComponent)
        XCTAssertEqual(first.url, second.url)
        XCTAssertEqual(first.digest, second.digest)
    }

    func testSameNameDifferentDigestUsesDigestSuffixAndPreservesBoth() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { XCTAssertNoThrow(try FileManager.default.removeItem(at: root)) }
        let firstDirectory = root.appendingPathComponent("a", isDirectory: true)
        let secondDirectory = root.appendingPathComponent("b", isDirectory: true)
        let destination = root.appendingPathComponent("profiles", isDirectory: true)
        try FileManager.default.createDirectory(at: firstDirectory, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: secondDirectory, withIntermediateDirectories: true)
        let firstSource = firstDirectory.appendingPathComponent("calibration.icc")
        let secondSource = secondDirectory.appendingPathComponent("calibration.icc")
        try Data("first bytes".utf8).write(to: firstSource)
        try Data("second bytes".utf8).write(to: secondSource)
        let store = UserColorSyncProfileStore(destinationOverride: destination, validator: AcceptingICCValidator())

        let first = try await store.stageProfile(from: firstSource)
        let second = try await store.stageProfile(from: secondSource)

        XCTAssertNotEqual(first.url, second.url)
        XCTAssertNotEqual(first.digest, second.digest)
        XCTAssertTrue(FileManager.default.fileExists(atPath: first.url.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: second.url.path))
        XCTAssertTrue(second.url.deletingPathExtension().lastPathComponent.hasPrefix("calibration-"))
    }

    func testOwnedFileIsDeletedOnlyWhenDigestStillMatches() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { XCTAssertNoThrow(try FileManager.default.removeItem(at: root)) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let source = root.appendingPathComponent("profile.icc")
        try Data("original".utf8).write(to: source)
        let store = UserColorSyncProfileStore(destinationOverride: root.appendingPathComponent("profiles"), validator: AcceptingICCValidator())
        let staged = try await store.stageProfile(from: source)

        try Data("changed".utf8).write(to: staged.url)
        await XCTAssertThrowsErrorAsync(try await store.removeIfOwned(staged))
        XCTAssertTrue(FileManager.default.fileExists(atPath: staged.url.path))
    }
}

private func XCTAssertThrowsErrorAsync<T>(
    _ expression: @autoclosure () async throws -> T,
    file: StaticString = #filePath,
    line: UInt = #line
) async {
    do {
        _ = try await expression()
        XCTFail("Expected expression to throw", file: file, line: line)
    } catch {
        // Expected.
    }
}
