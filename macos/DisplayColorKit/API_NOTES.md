# Public API and deployment notes

The package targets macOS 12. Apple's current DocC metadata reports the Swift ColorSync device/profile functions used here as introduced in macOS 10.13, Core Graphics active-display and transfer-table APIs as introduced in macOS 10.0–10.3, and `NSScreen.localizedName` as introduced in macOS 10.15. The deployment target therefore covers the complete selected API surface.

Authoritative public references:

- [ColorSync](https://developer.apple.com/documentation/colorsync)
- [ColorSync device assignment](https://developer.apple.com/documentation/colorsync/colorsyncdevicesetcustomprofiles(_:_:_:))
- [ColorSync profile installation](https://developer.apple.com/documentation/colorsync/colorsyncprofileinstall(_:_:_:_:))
- [Quartz Display Services](https://developer.apple.com/documentation/coregraphics/quartz-display-services)
- [CGGetDisplayTransferByTable](https://developer.apple.com/documentation/coregraphics/cggetdisplaytransferbytable(_:_:_:_:_:_:))
- [Display reconfiguration callbacks](https://developer.apple.com/documentation/coregraphics/cgdisplayreconfigurationcallback)
- [IOServiceGetMatchingServices](https://developer.apple.com/documentation/iokit/1514494-ioservicegetmatchingservices)
- [IODisplaySetFloatParameter](https://developer.apple.com/documentation/iokit/1574926-iodisplaysetfloatparameter)

## Restrictions and runtime variability

- `ColorSyncProfileInstall` exposes a profile-install entitlement contract. This package uses collision-safe current-user filesystem staging by default and surfaces sandbox/signing failures; a consuming app can inject a different `ProfileFileStoring` implementation if its approved distribution model uses the installation API.
- Security-scoped access is requested for user-selected source URLs. The source is never modified.
- IOKit brightness support depends on the display, connection, driver, and an unambiguous vendor/product/serial match. Unsupported brightness is a normal capability result.
- Transfer-table capacity, write behavior, and readback depend on hardware and HDR/display mode. Setter success and unavailable readback are represented separately.
- `CGDisplayRestoreColorSyncSettings()` is a global fallback. It is never reported as exact per-display restoration.
- Core Graphics display IDs are session-scoped. Persistent and journaled state contains only display UUIDs.

## Installed SDK gate

Run `Scripts/audit-sdk.sh` on every macOS build host. It prints the Xcode/SDK/Swift versions, confirms every required C symbol is present in the selected SDK headers, builds the package, and runs deterministic tests. Hardware tests remain opt-in.
