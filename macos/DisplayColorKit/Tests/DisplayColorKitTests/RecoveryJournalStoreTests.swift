import Foundation
import XCTest
@testable import DisplayColorKit

final class RecoveryJournalStoreTests: XCTestCase {
    func testJournalRoundTripsAndRemovesAtomicallyWrittenRecord() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { XCTAssertNoThrow(try FileManager.default.removeItem(at: root)) }
        let identity = try XCTUnwrap(DisplayIdentity(rawValue: "AAAAAAAA-0000-0000-0000-000000000000"))
        let table = try TransferTable.linear(sampleCount: 4)
        let journal = RecoveryJournal(
            stage: .prepared,
            snapshot: SessionSnapshot(
                display: identity,
                originalProfileURL: URL(fileURLWithPath: "/profiles/original.icc"),
                originalCustomDefaultURL: nil,
                originalTransferTable: table
            )
        )
        let store = try FileRecoveryJournalStore(directory: root)

        try await store.save(journal)
        XCTAssertEqual(try await store.loadAll(), [journal])
        try await store.remove(id: journal.id)
        XCTAssertEqual(try await store.loadAll(), [])
    }

    func testCorruptJournalReturnsTypedError() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { XCTAssertNoThrow(try FileManager.default.removeItem(at: root)) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data("not json".utf8).write(to: root.appendingPathComponent("bad.json"))
        let store = try FileRecoveryJournalStore(directory: root)

        do {
            _ = try await store.loadAll()
            XCTFail("Expected corrupt journal error")
        } catch let error as DisplayColorError {
            guard case .corruptRecoveryJournal = error else { return XCTFail("Unexpected error: \(error)") }
        }
    }
}
