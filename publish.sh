#!/usr/bin/env bash
# Builds the mod and bundles it as a Thunderstore-ready zip in dist/.
#
# After this runs you can upload dist/GsLethalStatsEmitter-<version>.zip via
# https://thunderstore.io/c/lethal-company/create/
#
# Once you have a Thunderstore API token, set THUNDERSTORE_TOKEN and pass
# --upload to auto-publish via the API.

set -euo pipefail
cd "$(dirname "$0")"

version=$(grep -oE '"version_number":\s*"[^"]+"' manifest.json | sed -E 's/.*"([^"]+)"$/\1/')
echo "building GsLethalStatsEmitter v$version"

~/.dotnet/dotnet build -c Release | tail -5

dist_abs="$(pwd)/dist"
mkdir -p "$dist_abs"
work=$(mktemp -d)
cp manifest.json README.md icon.png bin/Release/GsLethalStatsEmitter.dll "$work/"
out="$dist_abs/GsLethalStatsEmitter-$version.zip"
rm -f "$out"
(cd "$work" && zip -q "$out" *)
rm -rf "$work"

echo
echo "  built: $out ($(stat -c %s "$out") bytes)"
echo
echo "next steps:"
echo "  1. visit https://thunderstore.io/c/lethal-company/create/"
echo "  2. drag $out into the upload field"
echo "  3. fill in the team (must match the prefix in your r2modman folder)"

if [[ "${1:-}" == "--upload" ]]; then
  if [[ -z "${THUNDERSTORE_TOKEN:-}" ]]; then
    echo "ERROR: --upload requires THUNDERSTORE_TOKEN env var" >&2
    exit 2
  fi
  community="${THUNDERSTORE_COMMUNITY:-lethal-company}"
  team="${THUNDERSTORE_TEAM:-cproudlock}"
  echo "uploading to thunderstore (community=$community, team=$team)..."
  curl -sS -X POST "https://thunderstore.io/api/experimental/package-submission/submit/" \
    -H "Authorization: Bearer $THUNDERSTORE_TOKEN" \
    -F "file=@$out" \
    -F "metadata={\"upload_uuid\":\"$(uuidgen)\",\"author_name\":\"$team\",\"categories\":[\"tools\"],\"communities\":[\"$community\"],\"has_nsfw_content\":false}"
  echo
fi
