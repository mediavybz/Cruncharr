import DisplayColorKit
import Foundation

@main
struct DisplayColorCLI {
    static func main() async throws {
        let brightness = IOKitBrightnessAdapter()
        let displaySystem = CoreGraphicsDisplaySystem(brightness: brightness)
        let profileSystem = SystemColorProfileAdapter()
        let inventory = DisplayInventory(displays: displaySystem, profiles: profileSystem)
        let arguments = Array(CommandLine.arguments.dropFirst())

        switch arguments.first ?? "list" {
        case "list":
            let records = try await inventory.snapshot()
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            let data = try encoder.encode(records)
            guard let output = String(data: data, encoding: .utf8) else {
                throw DisplayColorError.system("Could not encode display inventory as UTF-8.")
            }
            print(output)
        case "profiles":
            guard arguments.count == 2, let identity = DisplayIdentity(rawValue: arguments[1]) else {
                throw DisplayColorError.system("Usage: display-colorctl profiles <display-uuid>")
            }
            let state = try await profileSystem.profileState(for: identity)
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            let data = try encoder.encode(state.profiles)
            guard let output = String(data: data, encoding: .utf8) else {
                throw DisplayColorError.system("Could not encode profile records as UTF-8.")
            }
            print(output)
        case "help", "--help", "-h":
            print("Usage: display-colorctl [list | profiles <display-uuid>]")
            print("This harness is intentionally read-only. Mutations are available through DisplayColorKit.")
        default:
            throw DisplayColorError.system("Unknown command. Use display-colorctl --help.")
        }
    }
}
