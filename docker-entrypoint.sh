#!/bin/sh
set -e

# Create only user-facing directories at runtime (after volumes are mounted)
# Internal directories with built-in content stay inside the container

echo "[Cruncharr] Setting up user directories..."

# Config directory - stores cruncharr.yaml, history.json, queue.json, token
mkdir -p /config
mkdir -p /config/logs

# Downloads output directory - where completed files go
mkdir -p /downloads

# Widevine DRM files directory - user must provide these
mkdir -p /widevine

# Tools directory - for custom tools or ffmpeg overrides
mkdir -p /tools

# Check if Widevine files exist
if [ ! -f /widevine/device_client_id_blob.bin ]; then
    echo "[Cruncharr] NOTE: Place /widevine/device_client_id_blob.bin and /widevine/device_private_key.pem for DRM support"
fi

echo "[Cruncharr] Directory setup complete."
echo "[Cruncharr] Starting API..."

# Execute the main application
exec ./cruncharr-api "$@"
