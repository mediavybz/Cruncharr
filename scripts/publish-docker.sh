#!/usr/bin/env bash
# Build the production image for both supported architectures entirely in BuildKit.
#
# With no arguments this performs a cache-only validation and does not publish.
# Pass normal `docker buildx build` options to publish, for example:
#   bash scripts/publish-docker.sh --tag ghcr.io/mediavybz/cruncharr:testing --push
set -euo pipefail
cd "$(dirname "$0")/.."

PLATFORMS=${PLATFORMS:-linux/amd64,linux/arm64}
SOURCE_REVISION=${SOURCE_REVISION:-}

if [ -z "$SOURCE_REVISION" ] && command -v git >/dev/null 2>&1; then
  SOURCE_REVISION=$(git rev-parse HEAD 2>/dev/null || true)
fi

BUILD_ARGS=()
if [ -n "$SOURCE_REVISION" ]; then
  BUILD_ARGS+=(--build-arg "SOURCE_REVISION=$SOURCE_REVISION")
fi

if [ "$#" -eq 0 ]; then
  echo "Validating Cruncharr for ${PLATFORMS} (cache only; nothing will be pushed)..."
  exec docker buildx build \
    --platform "$PLATFORMS" \
    "${BUILD_ARGS[@]}" \
    --output=type=cacheonly \
    .
fi

exec docker buildx build \
  --platform "$PLATFORMS" \
  "${BUILD_ARGS[@]}" \
  "$@" \
  .
