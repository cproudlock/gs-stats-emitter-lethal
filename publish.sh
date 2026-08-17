#!/usr/bin/env bash
# Builds the mod and bundles it as a Thunderstore-ready zip in dist/.
#
# Pass --upload (with THUNDERSTORE_TOKEN env var) to auto-publish via the
# Thunderstore experimental API. The flow is:
#   1. POST initiate-upload   → returns a usermedia uuid + a list of S3 PUT URLs
#   2. PUT each part to S3    → returns ETag headers (one per part)
#   3. POST finish-upload     → finalizes the S3 multipart object
#   4. POST submission/submit → publishes the package to the community

set -euo pipefail
cd "$(dirname "$0")"

community="${THUNDERSTORE_COMMUNITY:-lethal-company}"
team="${THUNDERSTORE_TEAM:-cproudlock}"

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

size=$(stat -c %s "$out")
echo
echo "  built: $out ($size bytes)"
echo

if [[ "${1:-}" != "--upload" ]]; then
  echo "next steps:"
  echo "  1. visit https://thunderstore.io/c/$community/create/"
  echo "  2. drag $out into the upload field"
  echo "  3. fill in the team ($team)"
  echo
  echo "  or rerun with --upload (requires THUNDERSTORE_TOKEN env var)"
  exit 0
fi

if [[ -z "${THUNDERSTORE_TOKEN:-}" ]]; then
  echo "ERROR: --upload requires THUNDERSTORE_TOKEN env var" >&2
  exit 2
fi

for tool in jq curl; do
  command -v "$tool" >/dev/null || { echo "ERROR: $tool not found" >&2; exit 2; }
done

api="https://thunderstore.io/api/experimental"
auth=(-H "Authorization: Bearer $THUNDERSTORE_TOKEN")
filename=$(basename "$out")

echo "1/4 initiate-upload ($size bytes)"
init=$(curl -sS -X POST "$api/usermedia/initiate-upload/" "${auth[@]}" -H "Content-Type: application/json" \
  -d "{\"filename\":\"$filename\",\"file_size_bytes\":$size}")
uuid=$(echo "$init" | jq -r '.user_media.uuid')
if [[ -z "$uuid" || "$uuid" == "null" ]]; then
  echo "initiate-upload failed: $init" >&2; exit 1
fi

echo "    usermedia uuid: $uuid"
parts_json="$(mktemp)"
echo '[]' > "$parts_json"

echo "2/4 putting $(echo "$init" | jq '.upload_urls | length') part(s) to S3"
echo "$init" | jq -c '.upload_urls[]' | while read -r part; do
  pn=$(echo "$part" | jq -r '.part_number')
  url=$(echo "$part" | jq -r '.url')
  offset=$(echo "$part" | jq -r '.offset')
  length=$(echo "$part" | jq -r '.length')
  chunk=$(mktemp)
  dd if="$out" bs=1 skip="$offset" count="$length" of="$chunk" status=none
  headers=$(mktemp)
  http=$(curl -sS -o /dev/null -D "$headers" -X PUT "$url" --data-binary "@$chunk" -w "%{http_code}")
  rm -f "$chunk"
  if [[ "$http" != "200" ]]; then
    echo "S3 part $pn upload failed (HTTP $http)" >&2
    cat "$headers" >&2
    exit 1
  fi
  etag=$(grep -i '^etag:' "$headers" | sed -E 's/^[Ee][Tt][Aa][Gg]:\s*//;s/\r$//')
  rm -f "$headers"
  jq --arg etag "$etag" --argjson pn "$pn" '. + [{ETag:$etag, PartNumber:$pn}]' "$parts_json" > "$parts_json.new"
  mv "$parts_json.new" "$parts_json"
  echo "    part $pn ok"
done

echo "3/4 finish-upload"
finish=$(curl -sS -X POST "$api/usermedia/$uuid/finish-upload/" "${auth[@]}" -H "Content-Type: application/json" \
  -d "{\"parts\":$(cat "$parts_json")}")
status=$(echo "$finish" | jq -r '.status // ""')
echo "    status: ${status:-$(echo "$finish" | head -c 200)}"
rm -f "$parts_json"

echo "4/4 submission/submit"
submit=$(curl -sS -X POST "$api/submission/submit/" "${auth[@]}" -H "Content-Type: application/json" \
  -d "{\"author_name\":\"$team\",\"communities\":[\"$community\"],\"categories\":[\"tools\"],\"has_nsfw_content\":false,\"upload_uuid\":\"$uuid\"}")
pkg=$(echo "$submit" | jq -r '.package_version.full_name // empty')
if [[ -n "$pkg" ]]; then
  echo
  echo "  published: $pkg"
  echo "  https://thunderstore.io/c/$community/p/$team/${pkg%-*}/"
else
  echo "submit response: $submit" >&2
  exit 1
fi
