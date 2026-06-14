#!/bin/sh
set -e

# PUID/PGID let the container match the ownership of host bind-mounts so it can
# write /config (settings, history, queue, token) and /downloads (output +
# temp segment files). Defaults to 1000:1000.
PUID="${PUID:-1000}"
PGID="${PGID:-1000}"

echo "[Cruncharr] Setting up user directories (PUID=${PUID} PGID=${PGID})..."

# Create user-facing directories at runtime (after volumes are mounted)
mkdir -p /config /config/logs /downloads /widevine /tools /tmp/cruncharr

# Check if Widevine files exist
if [ ! -f /widevine/device_client_id_blob.bin ]; then
    echo "[Cruncharr] NOTE: Place /widevine/device_client_id_blob.bin and /widevine/device_private_key.pem for DRM support"
fi

if [ "$(id -u)" = "0" ]; then
    # Running as root: align ownership of the mount points so the dropped-privilege
    # process can write to them, then exec as PUID:PGID.
    # Mount points get a shallow chown (a recursive chown of a large media library
    # would be slow); /config and the temp dir are small so chown them recursively
    # to fix existing files (e.g. cruncharr.yaml created by a previous root run).
    chown "${PUID}:${PGID}" /downloads /widevine /tools 2>/dev/null || true
    chown -R "${PUID}:${PGID}" /config /tmp/cruncharr 2>/dev/null || true

    echo "[Cruncharr] Directory setup complete. Starting API as ${PUID}:${PGID}..."
    # Drop privileges with whichever helper the base image ships (gosu on Debian,
    # su-exec on Alpine).
    if command -v gosu >/dev/null 2>&1; then
        exec gosu "${PUID}:${PGID}" ./cruncharr-api "$@"
    elif command -v su-exec >/dev/null 2>&1; then
        exec su-exec "${PUID}:${PGID}" ./cruncharr-api "$@"
    else
        echo "[Cruncharr] WARNING: no gosu/su-exec found; running as root."
        exec ./cruncharr-api "$@"
    fi
else
    # Already running as a non-root user (e.g. compose `user:` override). Just run;
    # the mounts must already be writable by this user.
    echo "[Cruncharr] Running as $(id -u):$(id -g) (non-root). Starting API..."
    exec ./cruncharr-api "$@"
fi
