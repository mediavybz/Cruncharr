# DisplayColorKit

`DisplayColorKit` is an independently implemented macOS 12+ Swift package for display discovery, ColorSync profile assignment, per-channel transfer-table management, and optional IOKit brightness control.

The library identifies displays by Core Graphics UUID. Numeric display IDs are treated as session-scoped and are resolved again immediately before every hardware mutation. All system calls are behind protocols so deterministic unit tests never change a developer's displays.

## Products

- `DisplayColorKit`: reusable library.
- `display-colorctl`: minimal read-only inventory/profile harness.
- `DisplayColorKitTests`: deterministic unit tests using in-memory adapters.
- Hardware integration tests are opt-in with `DISPLAY_COLOR_HARDWARE_TESTS=1`; they always attempt restoration in teardown.

## Build

```sh
cd macos/DisplayColorKit
swift build
swift test
```

SwiftPM builds the native architecture. Universal distribution artifacts can be produced by building the library for both `arm64-apple-macosx12.0` and `x86_64-apple-macosx12.0`, then combining them as an XCFramework in the consuming application.

## Operational constraints

- The current-user ColorSync profile directory must be writable. A sandboxed host must supply appropriate user-selected-file access and a distribution/signing configuration that permits profile staging or installation.
- Brightness is reported as unsupported unless exactly one `IODisplayConnect` service matches the selected display's vendor, product, and serial identity.
- Gamma-table writes can be accepted by Core Graphics without useful hardware readback in some HDR modes and on some hardware. The API reports this distinction.
- `CGDisplayRestoreColorSyncSettings()` reloads ColorSync settings globally. It is used only as a last-resort rollback after exact table restoration fails and is reported to the caller.
- A process crash cannot run cleanup. Active sessions remain in an atomic recovery journal and are restored on the next launch.

See `ATTRIBUTION.md` for the clean-room boundary and `Tests/DisplayColorKitTests/HardwareIntegrationTests.swift` for hardware-test safeguards.
