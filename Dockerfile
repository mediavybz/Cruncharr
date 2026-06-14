# Build stage (glibc SDK so the self-contained app matches the Debian runtime base)
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
ARG TARGETARCH

# Copy project files
COPY src/Cruncharr.Core/Cruncharr.Core.csproj src/Cruncharr.Core/
COPY src/Cruncharr.CLI/Cruncharr.CLI.csproj src/Cruncharr.CLI/
COPY src/Cruncharr.API/Cruncharr.API.csproj src/Cruncharr.API/

# Restore dependencies
RUN dotnet restore src/Cruncharr.API/Cruncharr.API.csproj \
    && dotnet restore src/Cruncharr.CLI/Cruncharr.CLI.csproj

# Copy source code
COPY src/ src/

# Build and publish API + CLI (self-contained trimmed single-file, glibc RID)
# TARGETARCH=amd64 -> linux-x64, arm64 -> linux-arm64
RUN if [ "$TARGETARCH" = "amd64" ]; then RID=linux-x64; elif [ "$TARGETARCH" = "arm64" ]; then RID=linux-arm64; else RID=linux-$TARGETARCH; fi \
    && echo "Building for $RID" \
    && dotnet publish src/Cruncharr.API/Cruncharr.API.csproj -c Release -o /app/publish \
       --self-contained true --runtime $RID \
       /p:PublishSingleFile=true /p:PublishTrimmed=true /p:TrimMode=partial /p:InvariantGlobalization=true \
    && dotnet publish src/Cruncharr.CLI/Cruncharr.CLI.csproj -c Release -o /app/cli \
       --self-contained true --runtime $RID \
       /p:PublishSingleFile=true /p:PublishTrimmed=true /p:TrimMode=partial /p:InvariantGlobalization=true

# Runtime stage (Debian glibc - runs the BtbN full-GPU ffmpeg build)
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

# Copy published applications
WORKDIR /app
COPY --from=build /app/publish/Cruncharr.API ./cruncharr-api
COPY --from=build /app/cli/cruncharr /usr/local/bin/
RUN chmod +x cruncharr-api && chmod +x /usr/local/bin/cruncharr

# Copy entrypoint script
COPY docker-entrypoint.sh ./docker-entrypoint.sh
RUN chmod +x ./docker-entrypoint.sh

# Copy web UI
COPY --from=build /app/publish/wwwroot ./wwwroot

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
