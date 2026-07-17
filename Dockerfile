# Production image for the .NET API/CLI and native media tools.
#
# The SDK stage cross-publishes self-contained, single-file, trimmed binaries for
# TARGETARCH on BUILDPLATFORM. The native stage obtains ffmpeg and builds Bento4.
# The final Debian slim stage contains neither the .NET SDK nor build toolchains.

ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:8.0.423-bookworm-slim
ARG DEBIAN_IMAGE=debian:bookworm-20260713-slim
ARG FFMPEG_RELEASE=autobuild-2026-07-17-13-22
ARG FFMPEG_VERSION=N-125649-g8d394252d8
ARG FFMPEG_AMD64_SHA256=05578a5e77661c860ef9b3f81dda393e238770a1adb5177fffefa64debc1bb53
ARG FFMPEG_ARM64_SHA256=cfa062e705fa22381ee69d736d9200ad6dc1b9c72d01d90c4c47b0d73ba7bf53
ARG BENTO4_COMMIT=b8c50a078356a1c3444ce0a8744634ed488424a4
ARG BENTO4_SHA256=d8aee66b20b04516de724f0d2146f928d6001fc9f280f5ce14b7858bc90b7889
ARG SOURCE_REVISION

# -- Stage 1: .NET build -------------------------------------------------------
FROM --platform=$BUILDPLATFORM ${DOTNET_SDK_IMAGE} AS dotnet-build
ARG TARGETARCH
ARG SOURCE_REVISION
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NUGET_XMLDOC_MODE=skip
WORKDIR /src

# Restore from stable manifests before copying source so ordinary code changes
# retain the expensive NuGet restore layer.
COPY src/Cruncharr.Core/Cruncharr.Core.csproj src/Cruncharr.Core/
COPY src/Cruncharr.API/Cruncharr.API.csproj src/Cruncharr.API/
COPY src/Cruncharr.CLI/Cruncharr.CLI.csproj src/Cruncharr.CLI/
RUN set -eux; \
    case "$TARGETARCH" in \
        amd64) RID=linux-x64 ;; \
        arm64) RID=linux-arm64 ;; \
        *) echo "Unsupported target architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    dotnet restore src/Cruncharr.API/Cruncharr.API.csproj --runtime "$RID" \
        -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial \
        -p:InvariantGlobalization=true; \
    dotnet restore src/Cruncharr.CLI/Cruncharr.CLI.csproj --runtime "$RID" \
        -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial \
        -p:InvariantGlobalization=true

COPY src/Cruncharr.Core/ src/Cruncharr.Core/
COPY src/Cruncharr.API/ src/Cruncharr.API/
COPY src/Cruncharr.CLI/ src/Cruncharr.CLI/
RUN set -eux; \
    case "$TARGETARCH" in \
        amd64) RID=linux-x64 ;; \
        arm64) RID=linux-arm64 ;; \
        *) echo "Unsupported target architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac; \
    dotnet publish src/Cruncharr.API/Cruncharr.API.csproj \
        --configuration Release --runtime "$RID" --self-contained true --no-restore \
        -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial \
        -p:InvariantGlobalization=true -p:SourceRevisionId="$SOURCE_REVISION" \
        --output /out/api; \
    dotnet publish src/Cruncharr.CLI/Cruncharr.CLI.csproj \
        --configuration Release --runtime "$RID" --self-contained true --no-restore \
        -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial \
        -p:InvariantGlobalization=true -p:SourceRevisionId="$SOURCE_REVISION" \
        --output /out/cli; \
    test ! -e /out/api/Cruncharr.API.dll

# -- Stage 2: Native tools ----------------------------------------------------
# All download/build tools stay in this stage and never reach the final image.
FROM ${DEBIAN_IMAGE} AS native-build
ARG TARGETARCH
ARG FFMPEG_RELEASE
ARG FFMPEG_VERSION
ARG FFMPEG_AMD64_SHA256
ARG FFMPEG_ARM64_SHA256
ARG BENTO4_COMMIT
ARG BENTO4_SHA256
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates curl xz-utils cmake make g++ \
    && if [ "$TARGETARCH" = "arm64" ]; then \
        FF=linuxarm64; FFMPEG_SHA256="$FFMPEG_ARM64_SHA256"; \
       else \
        FF=linux64; FFMPEG_SHA256="$FFMPEG_AMD64_SHA256"; \
       fi \
    && FFMPEG_ARCHIVE="ffmpeg-${FFMPEG_VERSION}-${FF}-gpl.tar.xz" \
    && echo "Fetching verified BtbN ffmpeg ${FFMPEG_VERSION} ($FF, full GPU support)" \
    && curl -fsSL "https://github.com/BtbN/FFmpeg-Builds/releases/download/${FFMPEG_RELEASE}/${FFMPEG_ARCHIVE}" -o /tmp/ffmpeg.tar.xz \
    && echo "${FFMPEG_SHA256}  /tmp/ffmpeg.tar.xz" | sha256sum -c - \
    && mkdir -p /tmp/ff && tar -xf /tmp/ffmpeg.tar.xz -C /tmp/ff --strip-components=1 \
    && cp /tmp/ff/bin/ffmpeg /tmp/ff/bin/ffprobe /usr/local/bin/ \
    && chmod +x /usr/local/bin/ffmpeg /usr/local/bin/ffprobe \
    && rm -rf /tmp/ff /tmp/ffmpeg.tar.xz \
    && curl -fsSL "https://codeload.github.com/axiomatic-systems/Bento4/tar.gz/${BENTO4_COMMIT}" -o /tmp/bento4.tar.gz \
    && echo "${BENTO4_SHA256}  /tmp/bento4.tar.gz" | sha256sum -c - \
    && mkdir -p /tmp/bento4 \
    && tar -xzf /tmp/bento4.tar.gz -C /tmp/bento4 --strip-components=1 \
    && rm /tmp/bento4.tar.gz \
    && cd /tmp/bento4 && mkdir build && cd build \
    && cmake .. -DCMAKE_BUILD_TYPE=Release \
    && make -j"$(nproc)" \
    && cp /tmp/bento4/build/mp4decrypt /usr/local/bin/ \
    && chmod +x /usr/local/bin/mp4decrypt

# -- Stage 3: Runtime ---------------------------------------------------------
# No curl, xz-utils, compilers, SDK, package manager metadata, or git remain.
FROM ${DEBIAN_IMAGE} AS runtime

# ffmpeg/ffprobe are static. mp4decrypt and the .NET apphost use glibc/libstdc++
# supplied by the Debian runtime packages below.
COPY --from=native-build /usr/local/bin/ffmpeg /usr/local/bin/ffprobe /usr/local/bin/mp4decrypt /usr/local/bin/

RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates mkvtoolnix gosu pci.ids \
    && rm -rf /var/lib/apt/lists/* \
    && useradd -u 1000 -m -s /bin/sh cruncharr \
    && mkdir -p /downloads /config /tools /widevine /tmp/cruncharr /app/fonts \
    && chown -R cruncharr:cruncharr /downloads /config /tools /widevine /tmp/cruncharr /app

WORKDIR /app
COPY --from=dotnet-build --chmod=0755 /out/api/Cruncharr.API ./cruncharr-api
COPY --from=dotnet-build --chmod=0755 /out/cli/cruncharr /usr/local/bin/cruncharr
COPY --from=dotnet-build /out/api/wwwroot ./wwwroot
COPY --chmod=0755 docker-entrypoint.sh ./docker-entrypoint.sh

# C.UTF-8 is built into glibc and preserves non-ASCII paths passed to native tools.
ENV LANG=C.UTF-8
ENV LC_ALL=C.UTF-8
ENV ASPNETCORE_URLS=http://+:8585
ENV CRUNCHYROLL_CONFIG_PATH=/config/cruncharr.yaml
ENV CRUNCHYROLL_OUTPUT_DIR=/downloads
ENV CRUNCHYROLL_TEMP_DIR=/tmp/cruncharr
ENV PATH="/app:/tools:/usr/local/bin:${PATH}"

EXPOSE 8585
VOLUME ["/downloads", "/config", "/tools", "/widevine"]

# Zero-dependency liveness check: port 8585 is 0x2189 in /proc/net/tcp.
HEALTHCHECK --interval=30s --timeout=3s --start-period=30s --retries=3 \
    CMD grep -q ':2189 ' /proc/net/tcp /proc/net/tcp6 2>/dev/null || exit 1

# Starts as root only to align bind-mount ownership, then execs the API through gosu.
ENTRYPOINT ["./docker-entrypoint.sh"]
