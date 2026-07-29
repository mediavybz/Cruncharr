import AppKit
import DisplayColorKit
import Foundation

@main
@MainActor
struct DisplayColorSampleApplication {
    static func main() {
        let application = NSApplication.shared
        let delegate = ApplicationDelegate()
        application.setActivationPolicy(.regular)
        application.delegate = delegate
        application.run()
        _ = delegate
    }
}

@MainActor
private final class ApplicationDelegate: NSObject, NSApplicationDelegate {
    private var window: NSWindow?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let brightness = IOKitBrightnessAdapter()
        let displaySystem = CoreGraphicsDisplaySystem(brightness: brightness)
        let profileSystem = SystemColorProfileAdapter()
        let inventory = DisplayInventory(displays: displaySystem, profiles: profileSystem)
        let controller = InspectorViewController(inventory: inventory, profileSystem: profileSystem)

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 860, height: 620),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "Display Color Inspector"
        window.minSize = NSSize(width: 680, height: 460)
        window.contentViewController = controller
        window.center()
        window.makeKeyAndOrderFront(nil)
        self.window = window
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }
}

@MainActor
private final class InspectorViewController: NSViewController {
    private let inventory: DisplayInventory
    private let profileSystem: SystemColorProfileAdapter
    private var records: [DisplayRecord] = []
    private var loadGeneration = 0

    private let displayPicker = NSPopUpButton(frame: .zero, pullsDown: false)
    private let refreshButton = NSButton(title: "Refresh", target: nil, action: nil)
    private let progress = NSProgressIndicator()
    private let statusLabel = NSTextField(labelWithString: "Waiting to inspect displays…")
    private let detailsView = NSTextView()

    init(inventory: DisplayInventory, profileSystem: SystemColorProfileAdapter) {
        self.inventory = inventory
        self.profileSystem = profileSystem
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not supported")
    }

    override func loadView() {
        let root = NSView()
        root.translatesAutoresizingMaskIntoConstraints = false
        view = root

        let title = NSTextField(labelWithString: "Display Color Inspector")
        title.font = .systemFont(ofSize: 24, weight: .semibold)

        let subtitle = NSTextField(labelWithString: "Read-only view of Core Graphics displays and ColorSync profile assignments")
        subtitle.textColor = .secondaryLabelColor
        subtitle.maximumNumberOfLines = 2

        displayPicker.target = self
        displayPicker.action = #selector(displaySelectionChanged)
        displayPicker.setContentHuggingPriority(.defaultLow, for: .horizontal)

        refreshButton.target = self
        refreshButton.action = #selector(refreshPressed)
        refreshButton.keyEquivalent = "r"
        refreshButton.keyEquivalentModifierMask = [.command]

        progress.style = .spinning
        progress.controlSize = .small
        progress.isDisplayedWhenStopped = false

        statusLabel.textColor = .secondaryLabelColor
        statusLabel.lineBreakMode = .byTruncatingTail

        let controls = NSStackView(views: [displayPicker, refreshButton, progress])
        controls.orientation = .horizontal
        controls.alignment = .centerY
        controls.spacing = 10

        detailsView.isEditable = false
        detailsView.isSelectable = true
        detailsView.isRichText = false
        detailsView.drawsBackground = false
        detailsView.font = .monospacedSystemFont(ofSize: 12.5, weight: .regular)
        detailsView.textContainerInset = NSSize(width: 12, height: 12)
        detailsView.isVerticallyResizable = true
        detailsView.isHorizontallyResizable = false
        detailsView.autoresizingMask = [.width]
        detailsView.textContainer?.widthTracksTextView = true
        detailsView.string = "Press Refresh to enumerate connected displays."

        let scrollView = NSScrollView()
        scrollView.hasVerticalScroller = true
        scrollView.autohidesScrollers = true
        scrollView.borderType = .bezelBorder
        scrollView.documentView = detailsView

        let stack = NSStackView(views: [title, subtitle, controls, statusLabel, scrollView])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 10
        stack.translatesAutoresizingMaskIntoConstraints = false
        stack.setCustomSpacing(18, after: subtitle)
        stack.setCustomSpacing(6, after: controls)
        root.addSubview(stack)

        scrollView.translatesAutoresizingMaskIntoConstraints = false
        controls.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 24),
            stack.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -24),
            stack.topAnchor.constraint(equalTo: root.topAnchor, constant: 22),
            stack.bottomAnchor.constraint(equalTo: root.bottomAnchor, constant: -24),
            controls.widthAnchor.constraint(equalTo: stack.widthAnchor),
            scrollView.widthAnchor.constraint(equalTo: stack.widthAnchor),
            scrollView.heightAnchor.constraint(greaterThanOrEqualToConstant: 300)
        ])
    }

    override func viewDidAppear() {
        super.viewDidAppear()
        if records.isEmpty {
            reloadDisplays()
        }
    }

    @objc private func refreshPressed() {
        reloadDisplays()
    }

    @objc private func displaySelectionChanged() {
        loadSelectedProfileState()
    }

    private func reloadDisplays() {
        loadGeneration += 1
        let generation = loadGeneration
        setLoading(true, status: "Enumerating connected displays…")

        Task { [weak self] in
            guard let self else { return }
            do {
                let records = try await inventory.snapshot()
                guard generation == loadGeneration else { return }
                self.records = records
                displayPicker.removeAllItems()
                displayPicker.addItems(withTitles: records.map { record in
                    record.isBuiltin ? "\(record.localizedName) — Built-in" : record.localizedName
                })

                guard !records.isEmpty else {
                    detailsView.string = "No online displays were reported by Core Graphics."
                    setLoading(false, status: "No connected displays found")
                    return
                }

                displayPicker.selectItem(at: 0)
                setLoading(false, status: "Found \(records.count) display\(records.count == 1 ? "" : "s")")
                loadSelectedProfileState()
            } catch {
                guard generation == loadGeneration else { return }
                show(error: error, context: "Display enumeration failed")
            }
        }
    }

    private func loadSelectedProfileState() {
        let index = displayPicker.indexOfSelectedItem
        guard records.indices.contains(index) else { return }
        let record = records[index]
        loadGeneration += 1
        let generation = loadGeneration
        setLoading(true, status: "Reading ColorSync profiles for \(record.localizedName)…")

        Task { [weak self] in
            guard let self else { return }
            do {
                let state = try await profileSystem.profileState(for: record.identity)
                guard generation == loadGeneration else { return }
                detailsView.string = render(record: record, state: state)
                detailsView.scrollToBeginningOfDocument(nil)
                setLoading(false, status: "Loaded \(state.profiles.count) profile\(state.profiles.count == 1 ? "" : "s")")
            } catch {
                guard generation == loadGeneration else { return }
                detailsView.string = render(record: record, profileError: error)
                setLoading(false, status: "Display loaded; profile query failed")
            }
        }
    }

    private func setLoading(_ loading: Bool, status: String) {
        statusLabel.stringValue = status
        refreshButton.isEnabled = !loading
        displayPicker.isEnabled = !loading && !records.isEmpty
        if loading {
            progress.startAnimation(nil)
        } else {
            progress.stopAnimation(nil)
        }
    }

    private func show(error: Error, context: String) {
        records = []
        displayPicker.removeAllItems()
        detailsView.string = "\(context)\n\n\(String(describing: error))"
        setLoading(false, status: context)
    }

    private func render(record: DisplayRecord, state: ProfileState) -> String {
        var lines = displayLines(for: record)
        lines.append("")
        lines.append("CURRENT COLORSYNC PROFILE")
        lines.append(contentsOf: profileLines(for: state.current, prefix: "  "))
        lines.append("")
        lines.append("AVAILABLE PROFILES (\(state.profiles.count))")

        for (index, profile) in state.profiles.enumerated() {
            lines.append("")
            lines.append("\(index + 1). \(profile.description ?? profile.url.deletingPathExtension().lastPathComponent)")
            lines.append(contentsOf: profileLines(for: profile, prefix: "   "))
        }

        if let customDefaultURL = state.customDefaultURL {
            lines.append("")
            lines.append("CUSTOM DEFAULT")
            lines.append("  \(customDefaultURL.path)")
        }

        if !state.diagnostics.isEmpty {
            lines.append("")
            lines.append("DIAGNOSTICS")
            lines.append(contentsOf: state.diagnostics.map { "  • \($0)" })
        }

        return lines.joined(separator: "\n")
    }

    private func render(record: DisplayRecord, profileError: Error) -> String {
        var lines = displayLines(for: record)
        lines.append("")
        lines.append("COLORSYNC PROFILE QUERY FAILED")
        lines.append("  \(String(describing: profileError))")
        return lines.joined(separator: "\n")
    }

    private func displayLines(for record: DisplayRecord) -> [String] {
        [
            "DISPLAY",
            "  Name: \(record.localizedName)",
            "  Stable identity: \(record.identity.rawValue)",
            "  Core Graphics ID: \(record.displayID)",
            "  Vendor / product / serial: \(record.vendorID) / \(record.productID) / \(record.serialNumber)",
            "  Built-in: \(yesNo(record.isBuiltin))",
            "  Online: \(yesNo(record.isOnline))",
            "  Mirrored: \(yesNo(record.isMirrored))",
            "  Pixel size: \(whole(record.pixelSize.width)) × \(whole(record.pixelSize.height))",
            "  Physical size: \(whole(record.physicalSizeMillimeters.width)) × \(whole(record.physicalSizeMillimeters.height)) mm",
            "  Bounds: (\(decimal(record.bounds.origin.x)), \(decimal(record.bounds.origin.y)))  \(decimal(record.bounds.size.width)) × \(decimal(record.bounds.size.height))",
            "  Inventory profile: \(record.currentProfileURL?.path ?? "None")",
            "  Baseline profile: \(record.baselineProfileURL?.path ?? "None")"
        ]
    }

    private func profileLines(for profile: ProfileRecord, prefix: String) -> [String] {
        var flags: [String] = []
        if profile.isCurrent { flags.append("current") }
        if profile.isDeviceDefault { flags.append("device default") }
        if profile.isCustomAssignment { flags.append("custom assignment") }
        if profile.isFactoryCandidateInferred { flags.append("inferred factory candidate") }

        return [
            "\(prefix)Path: \(profile.url.path)",
            "\(prefix)Identifier: \(profile.profileIdentifier ?? "Unavailable")",
            "\(prefix)Flags: \(flags.isEmpty ? "none" : flags.joined(separator: ", "))",
            "\(prefix)Digest: \(profile.digest ?? "Not calculated")"
        ]
    }

    private func yesNo(_ value: Bool) -> String {
        value ? "Yes" : "No"
    }

    private func whole(_ value: Double) -> String {
        String(format: "%.0f", value)
    }

    private func decimal(_ value: Double) -> String {
        String(format: "%.1f", value)
    }
}
