#!/bin/bash
# Builds ShowTell.app plus a .zip and .dmg into macos/dist.
#   ./build.sh                       universal (Apple silicon + Intel)
#   ARCHS=arm64 ./build.sh           Apple silicon only (faster)
#   VERSION=1.2.0 ./build.sh         set the app version
# The app is ad-hoc signed (not notarized), so the first launch needs right-click → Open.
set -euo pipefail
cd "$(dirname "$0")"

VERSION="${VERSION:-0.1.0}"
BUILD="${BUILD:-1}"
ARCHS="${ARCHS:-arm64 x86_64}"
ARCH_FLAGS=()
for a in $ARCHS; do ARCH_FLAGS+=(--arch "$a"); done

echo "Building ShowTell $VERSION for: $ARCHS"
swift build -c release "${ARCH_FLAGS[@]}"
# Where SwiftPM put the binary: .build/apple/Products/Release for multi-arch builds, .build/release otherwise.
BIN="$(swift build -c release "${ARCH_FLAGS[@]}" --show-bin-path 2>/dev/null || true)"
for candidate in "$BIN" .build/apple/Products/Release .build/release; do
    if [ -n "$candidate" ] && [ -f "$candidate/ShowTell" ]; then BIN="$candidate"; break; fi
done
[ -f "$BIN/ShowTell" ] || { echo "Couldn't find the built ShowTell binary"; exit 1; }
echo "Binary: $BIN/ShowTell"
lipo -info "$BIN/ShowTell" || true

APP=dist/ShowTell.app
rm -rf dist
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN/ShowTell" "$APP/Contents/MacOS/ShowTell"
# SwiftPM resource bundles from dependencies, if any.
for b in "$BIN"/*.bundle; do
    [ -e "$b" ] && cp -R "$b" "$APP/Contents/Resources/"
done
sed -e "s/__VERSION__/$VERSION/" -e "s/__BUILD__/$BUILD/" Resources/Info.plist > "$APP/Contents/Info.plist"

# App icon (optional: skipped if drawing it fails).
if swift scripts/make-icon.swift dist/AppIcon.iconset >/dev/null 2>&1 && iconutil -c icns dist/AppIcon.iconset -o "$APP/Contents/Resources/AppIcon.icns"; then
    echo "Icon added"
else
    echo "Icon skipped"
fi
rm -rf dist/AppIcon.iconset

codesign --force --deep --sign - "$APP"
codesign --verify --verbose "$APP"

ditto -c -k --keepParent "$APP" dist/ShowTell-macos.zip
mkdir -p dist/dmg
cp -R "$APP" dist/dmg/
ln -s /Applications dist/dmg/Applications
hdiutil create -volname ShowTell -srcfolder dist/dmg -ov -format UDZO dist/ShowTell-macos.dmg >/dev/null
rm -rf dist/dmg

echo "Built:"
ls -lh dist
