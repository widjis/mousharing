#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
version="$(cat ../VERSION)"
arch="$(uname -m)"
output="$PWD/dist/installers"
mkdir -p "$PWD/dist"
staging="$(mktemp -d "$PWD/dist/package.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
mkdir -p "$staging/payload"
MOUSHARING_APP_OUTPUT="$staging/payload/Mousharing.app" bash build-app.sh
pkgbuild --analyze --root "$staging/payload" "$staging/components.plist"
/usr/libexec/PlistBuddy -c "Set :0:BundleIsRelocatable false" "$staging/components.plist"
mkdir -p "$output"
pkg="$output/Mousharing-${version}-macOS-${arch}.pkg"
pkgbuild --root "$staging/payload" --component-plist "$staging/components.plist" --install-location /Applications --identifier com.widjis.mousharing.native --version "$version" "$pkg"
pkgutil --payload-files "$pkg" > "$output/payload-${arch}.txt"
grep -q 'Mousharing.app/Contents/MacOS/MousharingMac' "$output/payload-${arch}.txt"
(cd "$output" && shasum -a 256 "$(basename "$pkg")" > "$(basename "$pkg").sha256")
printf 'Installer: %s\n' "$pkg"
