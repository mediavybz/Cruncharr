#!/bin/sh
set -e

# PUID/PGID let the container match the ownership of host bind-mounts so it can
# write /config (settings, history, queue, token) and /downloads (output +
# temp segment files). Defaults to 1000:1000.
PUID="${PUID:-1000}"
PGID="${PGID:-1000}"
UMASK="${UMASK:-002}"

case "$PUID$PGID" in
    *[!0-9]*|"")
        echo "[Cruncharr] ERROR: PUID and PGID must be numeric (got PUID=${PUID} PGID=${PGID})" >&2
        exit 1
        ;;
esac

umask "$UMASK"

# The image PATH puts /app and /tools first so the app can pick up tool overrides.
# Both are writable by the unprivileged user, so the root phase below must never
# resolve commands through them: use a fixed system PATH and absolute paths, and
# restore the app PATH only for the final exec.
APP_PATH="$PATH"
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
export PATH

echo "[Cruncharr] Setting up user directories (PUID=${PUID} PGID=${PGID} UMASK=${UMASK})..."

# Create user-facing directories at runtime (after volumes are mounted)
/bin/mkdir -p /config /config/logs /downloads /widevine /tools /tmp/cruncharr

# Check if Widevine files exist
if [ ! -f /widevine/device_client_id_blob.bin ]; then
    echo "[Cruncharr] NOTE: Place /widevine/device_client_id_blob.bin and /widevine/device_private_key.pem for DRM support"
fi

if [ "$(/usr/bin/id -u)" = "0" ]; then
    if [ "$PUID" = "0" ]; then
        echo "[Cruncharr] WARNING: PUID=0 runs the application as root inside the container."
    fi

    # Running as root: align ownership of the mount points so the dropped-privilege
    # process can write to them, then exec as PUID:PGID.
    # Mount points get a shallow chown (a recursive chown of a large media library
    # would be slow); /config and the temp dir are small so chown them recursively
    # to fix existing files (e.g. cruncharr.yaml created by a previous root run).
    /bin/chown "${PUID}:${PGID}" /downloads /widevine /tools 2>/dev/null || true
    /bin/chown -R "${PUID}:${PGID}" /config /tmp/cruncharr 2>/dev/null || true

    echo "[Cruncharr] Directory setup complete. Starting API as ${PUID}:${PGID}..."
    PATH="$APP_PATH"
    export PATH
    # Drop privileges with whichever helper the base image ships (gosu on Debian,
    # su-exec on Alpine), located by absolute path so nothing under /tools or /app
    # can stand in for them.
    if [ -x /usr/sbin/gosu ]; then
        exec /usr/sbin/gosu "${PUID}:${PGID}" ./cruncharr-api "$@"
    elif [ -x /usr/local/bin/gosu ]; then
        exec /usr/local/bin/gosu "${PUID}:${PGID}" ./cruncharr-api "$@"
    elif [ -x /sbin/su-exec ]; then
        exec /sbin/su-exec "${PUID}:${PGID}" ./cruncharr-api "$@"
    else
        echo "[Cruncharr] WARNING: no gosu/su-exec found; running as root."
        exec ./cruncharr-api "$@"
    fi
else
    # Already running as a non-root user (e.g. compose `user:` override). Just run;
    # the mounts must already be writable by this user.
    echo "[Cruncharr] Running as $(/usr/bin/id -u):$(/usr/bin/id -g) (non-root). Starting API..."
    PATH="$APP_PATH"
    export PATH
    exec ./cruncharr-api "$@"
fi
