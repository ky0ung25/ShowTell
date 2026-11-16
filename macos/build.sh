#!/bin/bash
# Builds ShowTell.app plus a .zip and .dmg into macos/dist.
#   ./build.sh                       universal (Apple silicon + Intel)
#   ARCHS=arm64 ./build.sh           Apple silicon only (faster)
#   VERSION=1.2.0 ./build.sh         set the app version
#   SIGN_IDENTITY=... NOTARY_...=...  sign with a Developer ID and notarize (see the Signing section)
set -euo pipefail
cd "$(dirname "$0")"

VERSION="${VERSION:-0.1.0}"
BUILD="${BUILD:-1}"
ARCHS="${ARCHS:-arm64 x86_64}"

# One architecture at a time, then lipo them together. (A single multi-arch `swift build` goes through
# SwiftPM's Xcode build path, which exits with an error over some dependencies' language-mode settings.)
echo "Building ShowTell $VERSION for: $ARCHS"
BINS=()
for a in $ARCHS; do
    swift build -c release --arch "$a"
    BINS+=("$(swift build -c release --arch "$a" --show-bin-path)")
done
BIN="${BINS[0]}"

APP=dist/ShowTell.app
rm -rf dist
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
EXES=()
for b in "${BINS[@]}"; do EXES+=("$b/ShowTell"); done
lipo -create "${EXES[@]}" -output "$APP/Contents/MacOS/ShowTell"
lipo -info "$APP/Contents/MacOS/ShowTell"
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

# Signing. Without SIGN_IDENTITY the app is ad-hoc signed: it runs, but macOS says Apple "could not verify"
# it until the user clicks Open Anyway. With a Developer ID certificate in the keychain, set
#   SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)"
# and, to notarize (which removes the warning), either an App Store Connect API key:
#   NOTARY_KEY_PATH=AuthKey_XXXX.p8 NOTARY_KEY_ID=XXXX NOTARY_ISSUER=<issuer uuid>
# or an Apple ID with an app-specific password:
#   NOTARY_APPLE_ID=you@example.com NOTARY_PASSWORD=<app-specific password> NOTARY_TEAM_ID=TEAMID
SIGN_IDENTITY="${SIGN_IDENTITY:-}"
notarize() {
    if [ -n "${NOTARY_KEY_PATH:-}" ]; then
        xcrun notarytool submit "$1" --wait --key "$NOTARY_KEY_PATH" --key-id "$NOTARY_KEY_ID" --issuer "$NOTARY_ISSUER"
    else
        xcrun notarytool submit "$1" --wait --apple-id "$NOTARY_APPLE_ID" --password "$NOTARY_PASSWORD" --team-id "$NOTARY_TEAM_ID"
    fi
}
can_notarize() { [ -n "$SIGN_IDENTITY" ] && { [ -n "${NOTARY_KEY_PATH:-}" ] || [ -n "${NOTARY_APPLE_ID:-}" ]; }; }

if [ -n "$SIGN_IDENTITY" ]; then
    echo "Signing with: $SIGN_IDENTITY"
    codesign --force --options runtime --timestamp --entitlements Resources/ShowTell.entitlements --sign "$SIGN_IDENTITY" "$APP"
else
    echo "Ad-hoc signing (set SIGN_IDENTITY to sign with a Developer ID)"
    codesign --force --deep --sign - "$APP"
fi
codesign --verify --strict --verbose=2 "$APP"

if can_notarize; then
    echo "Notarizing the app..."
    ditto -c -k --keepParent "$APP" dist/notarize.zip
    notarize dist/notarize.zip
    rm -f dist/notarize.zip
    xcrun stapler staple "$APP"
fi

ditto -c -k --keepParent "$APP" dist/ShowTell-macos.zip
mkdir -p dist/dmg
cp -R "$APP" dist/dmg/
ln -s /Applications dist/dmg/Applications
hdiutil create -volname ShowTell -srcfolder dist/dmg -ov -format UDZO dist/ShowTell-macos.dmg >/dev/null
rm -rf dist/dmg

if can_notarize; then
    echo "Notarizing the disk image..."
    codesign --force --timestamp --sign "$SIGN_IDENTITY" dist/ShowTell-macos.dmg
    notarize dist/ShowTell-macos.dmg
    xcrun stapler staple dist/ShowTell-macos.dmg
    spctl --assess --type execute --verbose "$APP"
fi

echo "Built:"
ls -lh dist
