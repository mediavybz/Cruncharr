import XCTest
@testable import DisplayColorKit

final class TransferTableTests: XCTestCase {
    func testLinearCompatibilityTableHasExactEndpoints() throws {
        let table = try TransferTable.linear()
        XCTAssertEqual(table.count, 256)
        XCTAssertEqual(table.red.first, 0)
        XCTAssertEqual(table.red.last, 1)
        XCTAssertEqual(table.red, table.green)
        XCTAssertEqual(table.green, table.blue)
    }

    func testRejectsEmptyAndMismatchedChannels() {
        XCTAssertThrowsError(try TransferTable(red: [], green: [], blue: []))
        XCTAssertThrowsError(try TransferTable(red: [0, 1], green: [0], blue: [0, 1]))
    }

    func testRejectsNonFiniteAndOutOfRangeValues() {
        XCTAssertThrowsError(try TransferTable(red: [0, .nan], green: [0, 1], blue: [0, 1]))
        XCTAssertThrowsError(try TransferTable(red: [0, .infinity], green: [0, 1], blue: [0, 1]))
        XCTAssertThrowsError(try TransferTable(red: [0, 1.01], green: [0, 1], blue: [0, 1]))
        XCTAssertThrowsError(try TransferTable(red: [-0.01, 1], green: [0, 1], blue: [0, 1]))
    }

    func testGeneratedTableRejectsNonmonotonicChannels() {
        XCTAssertThrowsError(try TransferTable.generated(
            red: [0, 0.8, 0.7, 1],
            green: [0, 0.3, 0.7, 1],
            blue: [0, 0.3, 0.7, 1]
        ))
    }

    func testGammaGainPreservesChannelIndependenceAndClamps() throws {
        let table = try TransferTable.gammaGain(gamma: 2, gain: ChannelGain(red: 1, green: 0.5, blue: 2))
        XCTAssertEqual(table.red[0], 0)
        XCTAssertEqual(table.green[0], 0)
        XCTAssertEqual(table.blue[0], 0)
        XCTAssertEqual(table.red[255], 1, accuracy: 0.000_001)
        XCTAssertEqual(table.green[255], 0.5, accuracy: 0.000_001)
        XCTAssertEqual(table.blue[255], 1, accuracy: 0.000_001)
        XCTAssertGreaterThan(table.blue[64], table.red[64])
        XCTAssertLessThan(table.green[64], table.red[64])
    }

    func testRepeatedGainChangesRegenerateFromImmutableBase() throws {
        let base = try TransferTable.linear()
        let halfA = try TransferTable.gammaGain(gamma: 1, gain: ChannelGain(red: 0.5, green: 0.5, blue: 0.5), base: base)
        let halfB = try TransferTable.gammaGain(gamma: 1, gain: ChannelGain(red: 0.5, green: 0.5, blue: 0.5), base: base)
        XCTAssertEqual(halfA, halfB)
        XCTAssertEqual(halfB.red.last, 0.5)

        let fullAgain = try TransferTable.gammaGain(gamma: 1, gain: ChannelGain(), base: base)
        XCTAssertEqual(fullAgain, base)
    }

    func testRejectsInvalidGammaAndGain() {
        XCTAssertThrowsError(try TransferTable.gammaGain(gamma: 0, gain: ChannelGain()))
        XCTAssertThrowsError(try TransferTable.gammaGain(gamma: .nan, gain: ChannelGain()))
        XCTAssertThrowsError(try TransferTable.gammaGain(gamma: 1, gain: ChannelGain(red: -1)))
    }

    func testApproximateComparisonChecksCountAndTolerance() throws {
        let lhs = try TransferTable(red: [0, 0.5, 1], green: [0, 0.5, 1], blue: [0, 0.5, 1])
        let rhs = try TransferTable(red: [0, 0.5001, 1], green: [0, 0.5, 1], blue: [0, 0.5, 1])
        XCTAssertTrue(lhs.approximatelyEquals(rhs, tolerance: 0.001))
        XCTAssertFalse(lhs.approximatelyEquals(rhs, tolerance: 0.00001))
    }
}
