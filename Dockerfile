# Runtime-only image. The .NET app is published on the host (self-contained,
# single-file, trimmed) into docker-build/<arch>/ and copied in per-architecture.
# This avoids pulling the MCR .NET SDK base image at build time (which has been
# rate-limiting/refusing anonymous pulls); only the Debian runtime base is needed.
#
# Before building, publish the binaries on the host:
#   dotnet publish src/Cruncharr.API/Cruncharr.API.csproj -c Release -r linux-x64 \
#     --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true \
#     -p:TrimMode=partial -p:InvariantGlobalization=true -o docker-build/amd64/publish
#   (repeat for the CLI and for linux-arm64 -> docker-build/arm64/)
FROM debian:bookworm-slim
ARG TARGETARCH

# Runtime deps + a full-GPU ffmpeg (NVENC/CUDA/VAAPI/QSV/Vulkan) from BtbN static
# builds, plus mp4decrypt (Bento4) built from source. Build-only deps are purged
# afterwards to keep the image lean.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl xz-utils mkvtoolnix gosu \
        cmake make g++ git \
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
    && cp /tmp/bento4/build/mp4decrypt /usr/local/bin/ && chmod +x /usr/local/bin/mp4decrypt \
    && rm -rf /tmp/bento4 \
    && apt-get purge -y cmake make g++ git && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

# Create directories (matching original app structure)
RUN mkdir -p /downloads /config /tools /widevine /tmp/cruncharr /app/presets /app/fonts /app/video

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

# Default non-root user (uid 1000). Container starts as root so the entrypoint can
# chown the mounted volumes to PUID/PGID, then drops privileges with gosu.
RUN useradd -u 1000 -m -s /bin/sh cruncharr && \
    chown -R cruncharr:cruncharr /downloads /config /tools /widevine /tmp/cruncharr /app

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

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -fsS http://localhost:8585/api/v1/health || exit 1

# Entrypoint script creates directories then starts API
ENTRYPOINT ["./docker-entrypoint.sh"]
