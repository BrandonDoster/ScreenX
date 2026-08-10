#!/bin/sh
# Wrap the native binary in the smallest .app macOS will accept.
#
# Screen Recording is granted to a bundle with a stable code signature, not to a
# loose binary: run from a terminal, the permission is attributed to the terminal
# instead. Ad-hoc signing keeps the identity stable across rebuilds, so the grant
# survives and does not have to be given again every time.
#
# Pass --universal to build for both Apple architectures. That needs the two
# Rust targets installed, so it is not the default: a local build wants the one
# it runs on, and a release wants both.
set -e

root=$(cd "$(dirname "$0")/.." && pwd)
app="$root/app/target/ScreenX.app"
manifest="$root/app/Cargo.toml"

# Read the version rather than repeat it. It was written in this file once, and
# was still 0.3.0 after the crate moved on.
version=$(sed -n 's/^version = "\(.*\)"/\1/p' "$manifest" | head -1)

# Both programs, not one. The listener resolves screenx-capture from its own
# path, so a bundle missing the worker has a tray icon and shortcuts that do
# nothing at all — which reads as a denied Screen Recording permission and is
# not one.
mkdir -p "$root/app/target"
if [ "$1" = "--universal" ]; then
    cargo build --release --manifest-path "$manifest" --target aarch64-apple-darwin
    cargo build --release --manifest-path "$manifest" --target x86_64-apple-darwin
    for name in screenx screenx-capture; do
        lipo -create -output "$root/app/target/$name-universal" \
            "$root/app/target/aarch64-apple-darwin/release/$name" \
            "$root/app/target/x86_64-apple-darwin/release/$name"
    done
    suffix="-universal"
    built="$root/app/target"
else
    cargo build --release --manifest-path "$manifest"
    suffix=""
    built="$root/app/target/release"
fi

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$built/screenx$suffix" "$app/Contents/MacOS/screenx"
cp "$built/screenx-capture$suffix" "$app/Contents/MacOS/screenx-capture"

# The Dock, Finder and the .dmg window read CFBundleIconFile out of an .icns in
# Resources. Nothing generates one, and a bundle without it gets the blank
# default document icon — which looks like a broken build rather than a missing
# file. The Windows half of this is app/build.rs, reading the .ico from the same
# folder.
cp "$root/assets/icon.icns" "$app/Contents/Resources/icon.icns"

# Both binaries link several hundred crates, and MIT and Apache-2.0 each ask
# that their copyright notices travel with every copy. This is how they travel
# on macOS. It is generated rather than committed, so it is absent until it is
# made — and refusing to build is deliberate, because a bundle that quietly
# ships without the notices is the thing this file is here to prevent.
notices="$root/THIRD-PARTY-LICENSES.html"
if [ ! -f "$notices" ]; then
    echo "missing $notices — generate it first:" >&2
    echo "  cargo install cargo-about --features cli" >&2
    echo "  cargo about generate --manifest-path app/Cargo.toml app/about.hbs -o THIRD-PARTY-LICENSES.html" >&2
    exit 1
fi
cp "$notices" "$app/Contents/Resources/THIRD-PARTY-LICENSES.html"

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>screenx</string>
  <key>CFBundleIconFile</key><string>icon.icns</string>
  <key>CFBundleIdentifier</key><string>com.screenx.native</string>
  <key>CFBundleName</key><string>ScreenX</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>NSHighResolutionCapable</key><true/>
  <!-- A capture tool belongs in the menu bar, not the Dock. This is also the
       condition the overlay has to work under: an accessory app is outside the
       normal activation order. -->
  <key>LSUIElement</key><true/>
</dict>
</plist>
PLIST

# Inside out. The worker is nested code, and sealing the bundle over an unsigned
# nested executable leaves it unsigned; --deep is deprecated for exactly this.
codesign --force --sign - "$app/Contents/MacOS/screenx-capture"
codesign --force --sign - "$app"
echo "built $app"
