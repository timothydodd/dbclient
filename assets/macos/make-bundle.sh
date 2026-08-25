#!/usr/bin/env bash
# Assemble dbclient.app from a `dotnet publish` output directory.
#
# Usage: assets/macos/make-bundle.sh <publish-dir> [out-dir]
#   e.g. dotnet publish src/dbclient/dbclient.csproj -p:PublishProfile=osx-arm64
#        assets/macos/make-bundle.sh src/dbclient/bin/publish/osx-arm64 dist
#
# Produces <out-dir>/dbclient.app. If `iconutil` is available (macOS) and
# assets/dbclient-icon-512.png exists, an .icns icon is generated too.
set -euo pipefail

publish="${1:?publish dir required}"
out="${2:-.}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
app="$out/dbclient.app"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$here/Info.plist" "$app/Contents/Info.plist"
cp -R "$publish"/. "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/dbclient"

png="$here/../dbclient-icon-512.png"
if command -v iconutil >/dev/null && command -v sips >/dev/null && [ -f "$png" ]; then
  iconset="$(mktemp -d)/dbclient.iconset"
  mkdir -p "$iconset"
  for s in 16 32 128 256 512; do
    sips -z "$s" "$s" "$png" --out "$iconset/icon_${s}x${s}.png" >/dev/null
    d=$((s * 2))
    sips -z "$d" "$d" "$png" --out "$iconset/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns "$iconset" -o "$app/Contents/Resources/dbclient.icns"
fi

echo "built $app"
# Signing/notarization is not done here; see the TODO in .github/workflows/release.yml.
