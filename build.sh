#!/bin/zsh
set -euo pipefail
cd "${0:A:h}"
./Scripts/prepare-artwork.sh
APP="dist/Bloom Native.app"
SAVER="dist/Bloom Native.saver"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$SAVER/Contents/MacOS" "$SAVER/Contents/Resources"
swiftc -swift-version 5 -O -target arm64-apple-macosx14.0 Sources/BloomLanguage.swift Sources/LidSensor.swift Sources/BloomFrames.swift Sources/BloomRenderer.swift Sources/BloomApp.swift -o "$APP/Contents/MacOS/BloomNative" -framework SwiftUI -framework MetalKit -framework IOKit
swiftc -swift-version 5 -O -target arm64-apple-macosx14.0 -emit-library -module-name BloomSaver Sources/BloomSaver.swift -o "$SAVER/Contents/MacOS/BloomSaver" -framework ScreenSaver
cp Resources/BloomPoster.jpg Resources/BloomOriginal.mp4 Resources/Bloom.metal Resources/AppIcon.icns "$APP/Contents/Resources/"
cp Resources/BloomPoster.jpg "$SAVER/Contents/Resources/"
# Remove unused resources left by older generated builds.
rm -f "$APP/Contents/Resources/Bloom.jpg" "$SAVER/Contents/Resources/Bloom.jpg" "$SAVER/Contents/Resources/BloomOriginal.mp4" "$SAVER/Contents/Resources/Bloom.metal"
cp Resources/AppInfo.plist "$APP/Contents/Info.plist"
cp Resources/SaverInfo.plist "$SAVER/Contents/Info.plist"
codesign --force --sign - "$SAVER"
# Embedded saver keeps installation working after the app moves to Applications.
ditto "$SAVER" "$APP/Contents/Resources/Bloom Native.saver"
codesign --force --sign - "$APP"
