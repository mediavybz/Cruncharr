#!/usr/bin/env bash
# Publish the host binaries the Dockerfile copies in, for BOTH architectures.
#
# The runtime image (debian-slim) has NO .NET runtime installed — it only works with a
# SELF-CONTAINED, SINGLE-FILE, TRIMMED publish. Running a plain `dotnet publish -r <rid>`
# produces framework-dependent, multi-file output; the container then fails at startup with
#   "The application to execute does not exist: '/app/Cruncharr.API.dll'"
# because the apphost looks for a sibling .dll + runtime that were never copied in.
# ALWAYS publish through this script (or the exact flags below) before building the image.
set -euo pipefail
cd "$(dirname "$0")"

API=src/Cruncharr.API/Cruncharr.API.csproj
CLI=src/Cruncharr.CLI/Cruncharr.CLI.csproj

# amd64 -> linux-x64, arm64 -> linux-arm64
declare -A RID=( [amd64]=linux-x64 [arm64]=linux-arm64 )

COMMON=( -c Release --self-contained true
         -p:PublishSingleFile=true -p:PublishTrimmed=true
         -p:TrimMode=partial -p:InvariantGlobalization=true )

for arch in amd64 arm64; do
  rid=${RID[$arch]}
  echo "== Publishing API + CLI for $arch ($rid) =="
  rm -rf "docker-build/$arch/publish" "docker-build/$arch/cli"
  dotnet publish "$API" -r "$rid" "${COMMON[@]}" -o "docker-build/$arch/publish"
  dotnet publish "$CLI" -r "$rid" "${COMMON[@]}" -o "docker-build/$arch/cli"
  # Sanity: a self-contained single-file publish leaves NO loose Cruncharr.API.dll.
  if [ -f "docker-build/$arch/publish/Cruncharr.API.dll" ]; then
    echo "ERROR: $arch produced a framework-dependent build (loose .dll present)." >&2
    exit 1
  fi
  echo "   API apphost: $(du -h "docker-build/$arch/publish/Cruncharr.API" | cut -f1)"
done

echo "Done. Now: docker buildx build --platform linux/amd64,linux/arm64 -t ghcr.io/mediavybz/cruncharr:testing --push ."
