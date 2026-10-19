#!/bin/bash
# Builds OneDesk.app, a signed (ad-hoc by default) installer package, and a zip.
# Must be run on macOS. Apple Silicon is required at runtime; cross-building on an
# Intel Mac is possible but the resulting app will only run on arm64.
#
# Usage:
#   ./installer/macos/build.sh
#   VERSION=1.0.0 CODESIGN_IDENTITY="Developer ID Application: ..." \
#     PKG_SIGN_IDENTITY="Developer ID Installer: ..." ./installer/macos/build.sh

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION="${VERSION:-1.0.0}"
BUILD="${BUILD:-1}"
OUT="$ROOT/dist"
PUB="$ROOT/publish/osx-arm64"
STAGE="$OUT/stage"
APP="$STAGE/Applications/OneDesk.app"
COMPONENT="$OUT/OneDesk.pkg"
FINAL="$OUT/OneDesk-$VERSION.pkg"
ZIP="$OUT/OneDesk-$VERSION-macOS-arm64.zip"

if ! command -v dotnet >/dev/null 2>&1; then
    echo "error: the .NET 8 SDK is required (https://dotnet.microsoft.com/download/dotnet/8.0)." >&2
    exit 1
fi

SDKS="$(dotnet --list-sdks || true)"
if ! printf '%s\n' "$SDKS" | grep -q '^8\.'; then
    echo "error: a .NET 8 SDK was not found. Installed SDKs:" >&2
    printf '%s\n' "$SDKS" >&2
    exit 1
fi

if [ "$(uname -m)" != "arm64" ]; then
    echo "warning: building on $(uname -m); the app is published for osx-arm64 only."
fi

echo "==> Publishing macOS app (osx-arm64, self-contained)"
rm -rf "$PUB" "$STAGE" "$COMPONENT" "$FINAL" "$ZIP"
mkdir -p "$PUB" "$APP/Contents/MacOS" "$APP/Contents/Resources"

dotnet publish "$ROOT/src/OneDesk.Client.Mac/OneDesk.Client.Mac.csproj" \
    -c Release \
    -r osx-arm64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:Version="$VERSION" \
    -o "$PUB"

cp -R "$PUB"/. "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/OneDesk"

echo "==> Writing Info.plist"
sed -e "s/__VERSION__/$VERSION/g" -e "s/__BUILD__/$BUILD/g" \
    "$ROOT/installer/macos/Info.plist" > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist" >/dev/null

echo "==> Generating application icon"
ICON_PNG="$ROOT/assets/OneDesk-1024.png"
if [ -f "$ICON_PNG" ] && command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1; then
    ICONSET="$OUT/OneDesk.iconset"
    rm -rf "$ICONSET"
    mkdir -p "$ICONSET"
    for size in 16 32 128 256 512; do
        sips -z "$size" "$size" "$ICON_PNG" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
        sips -z "$((size * 2))" "$((size * 2))" "$ICON_PNG" \
            --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
    done
    iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/OneDesk.icns"
    rm -rf "$ICONSET"
else
    echo "warning: could not generate OneDesk.icns (missing source PNG or macOS icon tools)."
fi

echo "==> Looking for m1ddc"
M1DDC="${M1DDC_PATH:-}"
if [ -z "$M1DDC" ] && command -v m1ddc >/dev/null 2>&1; then
    M1DDC="$(command -v m1ddc)"
fi
if [ -n "$M1DDC" ] && [ -f "$M1DDC" ]; then
    cp "$M1DDC" "$APP/Contents/Resources/m1ddc"
    chmod +x "$APP/Contents/Resources/m1ddc"
    echo "    bundled: $M1DDC"
else
    echo "    warning: m1ddc not found. Automatic monitor switching will require"
    echo "             'brew install m1ddc' or M1DDC_PATH=/path/to/m1ddc."
fi

echo "==> Signing application"
/usr/bin/xattr -cr "$APP" >/dev/null 2>&1 || true
ENTITLEMENTS="$ROOT/installer/macos/OneDesk.entitlements"
if [ -n "${CODESIGN_IDENTITY:-}" ] && [ "$CODESIGN_IDENTITY" != "-" ]; then
    if [ -f "$APP/Contents/Resources/m1ddc" ]; then
        codesign --force --options runtime --timestamp \
            --sign "$CODESIGN_IDENTITY" "$APP/Contents/Resources/m1ddc"
    fi
    codesign --force --deep --options runtime --timestamp \
        --entitlements "$ENTITLEMENTS" --sign "$CODESIGN_IDENTITY" "$APP"
else
    if [ -f "$APP/Contents/Resources/m1ddc" ]; then
        codesign --force --sign - "$APP/Contents/Resources/m1ddc"
    fi
    codesign --force --deep --entitlements "$ENTITLEMENTS" --sign - "$APP"
fi
codesign --verify --deep --strict --verbose=2 "$APP"

echo "==> Building component package"
chmod +x "$ROOT/installer/macos/scripts/preinstall" "$ROOT/installer/macos/scripts/postinstall"
if [ -n "${PKG_SIGN_IDENTITY:-}" ]; then
    pkgbuild --root "$STAGE" --identifier com.onedesk.client --version "$VERSION" \
        --scripts "$ROOT/installer/macos/scripts" --install-location / \
        --sign "$PKG_SIGN_IDENTITY" "$COMPONENT"
else
    pkgbuild --root "$STAGE" --identifier com.onedesk.client --version "$VERSION" \
        --scripts "$ROOT/installer/macos/scripts" --install-location / \
        "$COMPONENT"
fi

echo "==> Building product archive"
DIST_DIR="$OUT/distribution"
rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR"
sed -e "s/__VERSION__/$VERSION/g" \
    "$ROOT/installer/macos/Distribution.xml" > "$DIST_DIR/Distribution.xml"
cp "$COMPONENT" "$DIST_DIR/OneDesk.pkg"
if [ -n "${PKG_SIGN_IDENTITY:-}" ]; then
    productbuild --distribution "$DIST_DIR/Distribution.xml" \
        --package-path "$DIST_DIR" --sign "$PKG_SIGN_IDENTITY" "$FINAL"
else
    productbuild --distribution "$DIST_DIR/Distribution.xml" \
        --package-path "$DIST_DIR" "$FINAL"
fi
rm -rf "$DIST_DIR" "$COMPONENT"

echo "==> Creating portable zip"
/usr/bin/ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"

rm -rf "$STAGE"

echo ""
echo "Package: $FINAL"
echo "Zip:     $ZIP"
shasum -a 256 "$FINAL" "$ZIP"

if [ -z "${CODESIGN_IDENTITY:-}" ] || [ "$CODESIGN_IDENTITY" = "-" ]; then
    echo ""
    echo "Note: this build is ad-hoc signed and not notarized. On first launch macOS may"
    echo "require System Settings > Privacy & Security > Open Anyway, or right-click Open."
fi
