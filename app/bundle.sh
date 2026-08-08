#!/bin/sh
# Wrap the native binary in the smallest .app macOS will accept.
#
# Screen Recording is granted to a bundle with a stable code signature, not to a
# loose binary: run from a terminal, the permission is attributed to the terminal
# instead. Ad-hoc signing keeps the identity stable across rebuilds, so the grant
# survives and does not have to be given again every time.
set -e

root=$(cd "$(dirname "$0")/.." && pwd)
app="$root/app/target/ScreenX Native.app"

cargo build --release --manifest-path "$root/app/Cargo.toml"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS"
cp "$root/app/target/release/screenx" "$app/Contents/MacOS/screenx"

cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>screenx</string>
  <key>CFBundleIdentifier</key><string>com.screenx.native</string>
  <key>CFBundleName</key><string>ScreenX Native</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.3.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <!-- A capture tool belongs in the menu bar, not the Dock. This is also the
       condition the overlay has to work under: an accessory app is outside the
       normal activation order. -->
  <key>LSUIElement</key><true/>
</dict>
</plist>
PLIST

codesign --force --deep --sign - "$app"
echo "built $app"
