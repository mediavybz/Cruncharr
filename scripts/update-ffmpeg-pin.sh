#!/usr/bin/env bash
# Rotate the pinned BtbN ffmpeg autobuild in the Dockerfile.
#
# BtbN prunes dated autobuild releases after a few weeks; when the pinned
# release disappears every image build fails with a 404 (this broke all
# builds in Aug 2026). This script moves the pin to the newest autobuild
# release with freshly computed SHA256 checksums.
#
# Usage:
#   scripts/update-ffmpeg-pin.sh            dry run: print what would change
#   scripts/update-ffmpeg-pin.sh --apply    rewrite the Dockerfile pins
#   scripts/update-ffmpeg-pin.sh --apply --force
#                                           rotate even if the current pin is
#                                           younger than MIN_PIN_AGE_DAYS
#
# No-op (exit 0) when the pin already matches the newest upstream release.
# Intended to run weekly in CI; safe to run locally.
set -euo pipefail
cd "$(dirname "$0")/.."

DOCKERFILE=Dockerfile
APPLY=0
FORCE=0
MIN_PIN_AGE_DAYS=10

for arg in "$@"; do
  case "$arg" in
    --apply) APPLY=1 ;;
    --force) FORCE=1 ;;
    *) echo "Unknown argument: $arg" >&2; exit 2 ;;
  esac
done

current_release=$(sed -n 's/^ARG FFMPEG_RELEASE=//p' "$DOCKERFILE" | head -1 | tr -d '[:space:]')
current_version=$(sed -n 's/^ARG FFMPEG_VERSION=//p' "$DOCKERFILE" | head -1 | tr -d '[:space:]')
[ -n "$current_release" ] && [ -n "$current_version" ] || {
  echo "Could not read current ffmpeg pins from $DOCKERFILE" >&2
  exit 1
}
echo "Current pin: $current_release / $current_version"

# Newest dated autobuild release (the rolling "latest" release has no stable
# rev-named assets; dated releases carry them but get pruned after a few weeks).
releases_json=$(curl -fsSL "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases?per_page=30")
latest_tag=$(printf '%s\n' "$releases_json" \
  | grep -o '"tag_name": *"[^"]*"' \
  | sed 's/"tag_name": *"\([^"]*\)".*/\1/' \
  | grep '^autobuild-' | head -1)
[ -n "$latest_tag" ] || { echo "No dated autobuild release found upstream" >&2; exit 1; }

assets_json=$(curl -fsSL "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/$latest_tag")
amd64_name=$(printf '%s\n' "$assets_json" \
  | grep -o '"name": *"ffmpeg-N-[^"]*-linux64-gpl\.tar\.xz"' | head -1 \
  | sed 's/"name": *"\([^"]*\)".*/\1/')
arm64_name=$(printf '%s\n' "$assets_json" \
  | grep -o '"name": *"ffmpeg-N-[^"]*-linuxarm64-gpl\.tar\.xz"' | head -1 \
  | sed 's/"name": *"\([^"]*\)".*/\1/')
[ -n "$amd64_name" ] && [ -n "$arm64_name" ] || {
  echo "Expected ffmpeg assets not found in $latest_tag" >&2
  exit 1
}
version=$(printf '%s' "$amd64_name" | sed -E 's/^ffmpeg-(N-[0-9]+-g[0-9a-f]+)-linux64-gpl\.tar\.xz$/\1/')
echo "Newest upstream: $latest_tag / $version"

if [ "$latest_tag" = "$current_release" ] && [ "$version" = "$current_version" ]; then
  echo "Pin already current - nothing to do"
  exit 0
fi

# Only rotate when the pinned release is getting old; upstream publishes daily
# and pruning happens after a few weeks, so rotating just before the cliff
# avoids churning the build for every daily release.
pin_date=$(printf '%s' "$current_release" | sed -E 's/^autobuild-([0-9]{4}-[0-9]{2}-[0-9]{2}).*/\1/')
pin_secs=$(date -d "$pin_date" +%s 2>/dev/null || echo 0)
age_days=$(( ($(date +%s) - pin_secs) / 86400 ))
if [ "$FORCE" -ne 1 ] && [ "$age_days" -lt "$MIN_PIN_AGE_DAYS" ]; then
  echo "Pin is only $age_days days old (min $MIN_PIN_AGE_DAYS); rotation deferred"
  exit 0
fi

echo "Verifying checksums for $amd64_name and $arm64_name ..."
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
base="https://github.com/BtbN/FFmpeg-Builds/releases/download/$latest_tag"
curl -fsSL "$base/$amd64_name" -o "$tmp/amd64.tar.xz"
amd64_sha=$(sha256sum "$tmp/amd64.tar.xz" | awk '{print $1}')
curl -fsSL "$base/$arm64_name" -o "$tmp/arm64.tar.xz"
arm64_sha=$(sha256sum "$tmp/arm64.tar.xz" | awk '{print $1}')

if [ "$APPLY" -ne 1 ]; then
  echo "Dry run - would update $DOCKERFILE:"
  echo "  FFMPEG_RELEASE=$latest_tag"
  echo "  FFMPEG_VERSION=$version"
  echo "  FFMPEG_AMD64_SHA256=$amd64_sha"
  echo "  FFMPEG_ARM64_SHA256=$arm64_sha"
  exit 0
fi

sed -i "s|^ARG FFMPEG_RELEASE=.*|ARG FFMPEG_RELEASE=$latest_tag|" "$DOCKERFILE"
sed -i "s|^ARG FFMPEG_VERSION=.*|ARG FFMPEG_VERSION=$version|" "$DOCKERFILE"
sed -i "s|^ARG FFMPEG_AMD64_SHA256=.*|ARG FFMPEG_AMD64_SHA256=$amd64_sha|" "$DOCKERFILE"
sed -i "s|^ARG FFMPEG_ARM64_SHA256=.*|ARG FFMPEG_ARM64_SHA256=$arm64_sha|" "$DOCKERFILE"
echo "Updated $DOCKERFILE pin to $latest_tag / $version"
