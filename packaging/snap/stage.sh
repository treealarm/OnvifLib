#!/usr/bin/env bash
# Fills payload/ for snapcraft.yaml from the linux-x64 publish output.
#
#   ./stage.sh <OnvifLib.Gui-linux-x64.zip | publish directory> <version>
#
# Result (gitignored): payload/app (the publish tree), payload/icon.png (the repository icon),
# payload/version (the snap version, normally the gui-X.Y.Z tag without "gui-").
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
src=${1:?usage: stage.sh <zip-or-publish-dir> <version>}
version=${2:?usage: stage.sh <zip-or-publish-dir> <version>}
payload=$here/payload

# Snap version rules: at most 32 characters of letters, digits and . + ~ : -, starting and
# ending with a letter or digit (or + ~ at the end).
if ! [[ $version =~ ^[A-Za-z0-9][A-Za-z0-9.+~:-]{0,30}[A-Za-z0-9+~]?$ ]]; then
  echo "stage.sh: '$version' is not a valid snap version" >&2
  exit 1
fi

rm -rf "$payload"
mkdir -p "$payload"

if [ -d "$src" ]; then
  cp -a "$src/." "$payload/app"
else
  tmp=$(mktemp -d)
  trap 'rm -rf "$tmp"' EXIT
  unzip -q "$src" -d "$tmp"
  # The zip holds one top-level directory (OnvifLib.Gui-linux-x64); find the one with the host.
  app=$(find "$tmp" -maxdepth 2 -type f -name OnvifLib.Gui -printf '%h\n' | head -n1)
  test -n "$app" || { echo "stage.sh: no OnvifLib.Gui executable inside $src" >&2; exit 1; }
  mv "$app" "$payload/app"
fi

test -f "$payload/app/OnvifLib.Gui.dll" || { echo "stage.sh: $payload/app is not a publish tree" >&2; exit 1; }
test -f "$payload/app/ffmpeg/ffmpeg" || { echo "stage.sh: ffmpeg/ffmpeg is missing; the snap must bundle it" >&2; exit 1; }
chmod +x "$payload/app/OnvifLib.Gui" "$payload/app/ffmpeg/ffmpeg" "$payload/app/createdump" 2>/dev/null || true

cp "$here/../../icon.png" "$payload/icon.png"
printf '%s\n' "$version" > "$payload/version"

echo "staged $(du -sh "$payload/app" | cut -f1) into $payload (version $version)"
