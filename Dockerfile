# Runtime-only image. The .NET app is published on the host (self-contained,
# single-file, trimmed) into docker-build/<arch>/ and copied in per-architecture.
# This avoids pulling the MCR .NET SDK base image at build time.
#
# Multi-stage build: stage 1 fetches/builds ffmpeg + Bento4; stage 2 is the
# lean runtime with only the binaries and packages the app actually needs.
# No curl, xz-utils, compilers, or git in the final image.
#
# Before building, publish the binaries on the host. Use ./publish-docker.sh (it does both
# architectures + the CLI with the required flags). A self-contained, single-file, trimmed
# publish is MANDATORY — this image has no .NET runtime, so a framework-dependent publish
# fails at startup ("application to execute does not exist: /app/Cruncharr.API.dll").
# Equivalent manual command (per arch):
#   dotnet publish src/Cruncharr.API/Cruncharr.API.csproj -c Release -r linux-x64 \
#     --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true \
#     -p:TrimMode=partial -p:InvariantGlobalization=true -o docker-build/amd64/publish
#   (repeat for the CLI and for linux-arm64 -> docker-build/arm64/)

# ── Stage 1: Builder ──────────────────────────────────────────────
# Fetch BtbN ffmpeg (static) and build Bento4's mp4decrypt from source.
# All build tools stay in this stage and never reach the final image.
FROM debian:bookworm-slim AS builder
ARG TARGETARCH
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl xz-utils cmake make g++ git \
    && if [ "$TARGETARCH" = "arm64" ]; then FF=linuxarm64; else FF=linux64; fi \
    && echo "Fetching BtbN ffmpeg ($FF, full GPU support)" \
    && curl -fsSL "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-${FF}-gpl.tar.xz" -o /tmp/ffmpeg.tar.xz \
    && mkdir -p /tmp/ff && tar -xf /tmp/ffmpeg.tar.xz -C /tmp/ff --strip-components=1 \
    && cp /tmp/ff/bin/ffmpeg /tmp/ff/bin/ffprobe /usr/local/bin/ \
    && chmod +x /usr/local/bin/ffmpeg /usr/local/bin/ffprobe \
    && rm -rf /tmp/ff /tmp/ffmpeg.tar.xz \
    && git clone --depth 1 https://github.com/axiomatic-systems/Bento4.git /tmp/bento4 \
    && cd /tmp/bento4 && mkdir build && cd build \
    && cmake .. -DCMAKE_BUILD_TYPE=Release \
    && make -j"$(nproc)" \
    && cp /tmp/bento4/build/mp4decrypt /usr/local/bin/ && chmod +x /usr/local/bin/mp4decrypt

# ── Stage 2: Runtime ──────────────────────────────────────────────
# Only the packages and binaries the running app needs.
# No curl, no xz-utils, no compilers, no git — attack surface minimized.
FROM debian:bookworm-slim
ARG TARGETARCH

# Copy compiled binaries from builder (ffmpeg/ffprobe are static; mp4decrypt
# links against libstdc++6 which is present in the base image).
COPY --from=builder /usr/local/bin/ffmpeg /usr/local/bin/ffprobe /usr/local/bin/mp4decrypt /usr/local/bin/

# Runtime deps only: HTTPS certs, MKV muxer, privilege-dropper, PCI device-name
# database (pci.ids ≈1MB — lets the UI show the exact GPU model instead of a
# generic vendor label).
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates mkvtoolnix gosu pci.ids \
    && rm -rf /var/lib/apt/lists/* \
    && useradd -u 1000 -m -s /bin/sh cruncharr \
    && mkdir -p /downloads /config /tools /widevine /tmp/cruncharr /app/fonts \
    && chown -R cruncharr:cruncharr /downloads /config /tools /widevine /tmp/cruncharr /app

# Copy host-published applications for the target architecture
WORKDIR /app
COPY docker-build/${TARGETARCH}/publish/Cruncharr.API ./cruncharr-api
COPY docker-build/${TARGETARCH}/cli/cruncharr /usr/local/bin/
RUN chmod +x cruncharr-api && chmod +x /usr/local/bin/cruncharr

# Copy entrypoint script
COPY docker-entrypoint.sh ./docker-entrypoint.sh
RUN chmod +x ./docker-entrypoint.sh

# Copy web UI
COPY docker-build/${TARGETARCH}/publish/wwwroot ./wwwroot

# Set environment
ENV ASPNETCORE_URLS=http://+:8585
ENV CRUNCHYROLL_CONFIG_PATH=/config/cruncharr.yaml
ENV CRUNCHYROLL_OUTPUT_DIR=/downloads
ENV CRUNCHYROLL_TEMP_DIR=/tmp/cruncharr
ENV PATH="/app:/tools:/usr/local/bin:${PATH}"

# Expose API port (8585 - uncommon port to avoid conflicts)
EXPOSE 8585

# Volumes - only user-facing directories
VOLUME ["/downloads", "/config", "/tools", "/widevine"]

# Health check — uses /proc/net/tcp instead of curl (port 8585 = 0x2189).
# Zero-dependency: no HTTP client package needed in the image.
HEALTHCHECK --interval=30s --timeout=3s --start-period=30s --retries=3 \
    CMD grep -q ':2189 ' /proc/net/tcp /proc/net/tcp6 2>/dev/null || exit 1

# Entrypoint script creates directories then starts API
ENTRYPOINT ["./docker-entrypoint.sh"]
