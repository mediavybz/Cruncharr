import Foundation

public actor FileRecoveryJournalStore: RecoveryJournalStoring {
    private let directory: URL
    private let fileManager: FileManager
    private let encoder: JSONEncoder
    private let decoder: JSONDecoder

    public init(directory: URL? = nil, fileManager: FileManager = .default) throws {
        self.fileManager = fileManager
        if let directory {
            self.directory = directory.standardizedFileURL
        } else {
            let applicationSupport = try fileManager.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: false)
            self.directory = applicationSupport
                .appendingPathComponent("DisplayColorKit", isDirectory: true)
                .appendingPathComponent("Recovery", isDirectory: true)
        }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        self.encoder = encoder
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        self.decoder = decoder
    }

    public func save(_ journal: RecoveryJournal) async throws {
        try ensureDirectory()
        let destination = fileURL(for: journal.id)
        do {
            let data = try encoder.encode(journal)
            try data.write(to: destination, options: [.atomic])
        } catch {
            throw DisplayColorError.system("Could not atomically save recovery journal: \(error.localizedDescription)")
        }
    }

    public func remove(id: UUID) async throws {
        let url = fileURL(for: id)
        guard fileManager.fileExists(atPath: url.path) else { return }
        do { try fileManager.removeItem(at: url) }
        catch { throw DisplayColorError.system("Could not remove recovery journal: \(error.localizedDescription)") }
    }

    public func loadAll() async throws -> [RecoveryJournal] {
        guard fileManager.fileExists(atPath: directory.path) else { return [] }
        let files: [URL]
        do {
            files = try fileManager.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.isRegularFileKey])
                .filter { $0.pathExtension == "json" }
                .sorted { $0.lastPathComponent < $1.lastPathComponent }
        } catch {
            throw DisplayColorError.system("Could not enumerate recovery journals: \(error.localizedDescription)")
        }
        var journals: [RecoveryJournal] = []
        for file in files {
            do { journals.append(try decoder.decode(RecoveryJournal.self, from: Data(contentsOf: file))) }
            catch { throw DisplayColorError.corruptRecoveryJournal(file, error.localizedDescription) }
        }
        return journals
    }

    private func ensureDirectory() throws {
        do { try fileManager.createDirectory(at: directory, withIntermediateDirectories: true) }
        catch { throw DisplayColorError.system("Could not create recovery-journal directory: \(error.localizedDescription)") }
    }

    private func fileURL(for id: UUID) -> URL {
        directory.appendingPathComponent(id.uuidString.lowercased()).appendingPathExtension("json")
    }
}
