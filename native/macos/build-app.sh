#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
swift build -c release
bundle="$PWD/dist/Mousharing.app"
mkdir -p "$bundle/Contents/MacOS"
cp .build/release/MousharingMac "$bundle/Contents/MacOS/MousharingMac"
cat > "$bundle/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>com.widjis.mousharing.native</string>
<key>CFBundleName</key><string>Mousharing</string>
<key>CFBundleDisplayName</key><string>Mousharing</string>
<key>CFBundleExecutable</key><string>MousharingMac</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>0.1.0</string>
<key>CFBundleVersion</key><string>1</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSLocalNetworkUsageDescription</key><string>Mousharing menemukan dan menghubungkan laptop Anda untuk berbagi mouse, keyboard, dan clipboard.</string>
</dict></plist>
PLIST
codesign --force --sign - "$bundle"
printf 'Built %s\n' "$bundle"
