# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Copy project files
COPY src/Cruncharr.Core/Cruncharr.Core.csproj src/Cruncharr.Core/
COPY src/Cruncharr.CLI/Cruncharr.CLI.csproj src/Cruncharr.CLI/
COPY src/Cruncharr.API/Cruncharr.API.csproj src/Cruncharr.API/

# Restore dependencies
RUN dotnet restore src/Cruncharr.API/Cruncharr.API.csproj \
    && dotnet restore src/Cruncharr.CLI/Cruncharr.CLI.csproj

# Copy source code
COPY src/ src/

# Build and publish API (self-contained trimmed single-file)
RUN dotnet publish src/Cruncharr.API/Cruncharr.API.csproj -c Release -o /app/publish \
    --self-contained true \
    --runtime linux-musl-x64 \
    /p:PublishSingleFile=true \
    /p:PublishTrimmed=true \
    /p:TrimMode=partial \
    /p:InvariantGlobalization=true

# Build and publish CLI
RUN dotnet publish src/Cruncharr.CLI/Cruncharr.CLI.csproj -c Release -o /app/cli \
    --self-contained true \
    --runtime linux-musl-x64 \
    /p:PublishSingleFile=true \
    /p:PublishTrimmed=true \
    /p:TrimMode=partial \
    /p:InvariantGlobalization=true

# Runtime stage
FROM alpine:3.21

# Install runtime dependencies
# Note: icu-libs removed via InvariantGlobalization=true
RUN apk add --no-cache \
    ca-certificates \
    libstdc++ \
    libgcc \
    ffmpeg \
    mkvtoolnix \
    curl \
    unzip

# Build mp4decrypt (Bento4) from source for Widevine decryption
RUN apk add --no-cache --virtual .build-deps \
    cmake \
    make \
    g++ \
    git \
    && git clone --depth 1 https://github.com/axiomatic-systems/Bento4.git /tmp/bento4 \
    && cd /tmp/bento4 \
    && mkdir build && cd build \
    && cmake .. -DCMAKE_BUILD_TYPE=Release \
    && make -j$(nproc) \
    && cp /tmp/bento4/build/mp4decrypt /usr/local/bin/ \
    && chmod +x /usr/local/bin/mp4decrypt \
    && rm -rf /tmp/bento4 \
    && apk del .build-deps

# Create directories (matching original app structure)
RUN mkdir -p /downloads /config /tools /widevine /tmp/cruncharr /app/presets /app/fonts /app/video

# Copy published applications
WORKDIR /app
COPY --from=build /app/publish/Cruncharr.API ./cruncharr-api
COPY --from=build /app/cli/cruncharr /usr/local/bin/
RUN chmod +x cruncharr-api && chmod +x /usr/local/bin/cruncharr

# Copy web UI
COPY --from=build /app/publish/wwwroot ./wwwroot

# Set environment
ENV ASPNETCORE_URLS=http://+:8585
ENV CRUNCHYROLL_CONFIG_PATH=/config/cruncharr.yaml
ENV CRUNCHYROLL_OUTPUT_DIR=/downloads
ENV CRUNCHYROLL_TEMP_DIR=/tmp/cruncharr
ENV PATH="/app:/tools:/usr/local/bin:${PATH}"

# Expose API port (8585 - uncommon port to avoid conflicts)
EXPOSE 8585

# Volumes
VOLUME ["/downloads", "/config", "/tools", "/widevine", "/tmp/cruncharr", "/app/presets", "/app/fonts"]

# Health check using busybox wget (avoids curl ~5MB)
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD wget -qO- http://localhost:8585/api/v1/health || exit 1

# Entrypoint for API mode (default)
ENTRYPOINT ["./cruncharr-api"]
