#!/bin/sh
set -eu

SDK_PATH="$(xcrun --sdk macosx --show-sdk-path)"
echo "Xcode: $(xcodebuild -version | tr '\n' ' ')"
echo "SDK path: $SDK_PATH"
echo "SDK version: $(xcrun --sdk macosx --show-sdk-version)"
swift --version

SYMBOLS="
CGGetActiveDisplayList
CGDisplayCreateUUIDFromDisplayID
CGDisplayGetDisplayIDFromUUID
CGDisplayRegisterReconfigurationCallback
CGDisplayRemoveReconfigurationCallback
ColorSyncDeviceCopyDeviceInfo
ColorSyncIterateDeviceProfiles
ColorSyncDeviceSetCustomProfiles
ColorSyncProfileCreateWithURL
ColorSyncProfileVerify
CGGetDisplayTransferByTable
CGSetDisplayTransferByTable
CGDisplayRestoreColorSyncSettings
IOServiceGetMatchingServices
IODisplayCreateInfoDictionary
IODisplaySetFloatParameter
"

MISSING=0
for SYMBOL in $SYMBOLS; do
    if grep -R -q "$SYMBOL" "$SDK_PATH/System/Library/Frameworks"; then
        echo "present: $SYMBOL"
    else
        echo "missing: $SYMBOL" >&2
        MISSING=1
    fi
done

if [ "$MISSING" -ne 0 ]; then
    echo "Required public SDK symbols are missing." >&2
    exit 1
fi

swift build --build-tests -Xswiftc -warnings-as-errors
swift test -Xswiftc -warnings-as-errors
