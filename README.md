# Cruncharr

A self-hosted companion for organizing and maintaining your personal anime media library — a headless backend with a REST API plus a single-page web interface.

## Quick Deploy

### Option 1: Docker Run (Recommended)

```bash
# Create directories
mkdir -p Cruncharr/config Cruncharr/downloads Cruncharr/widevine

# Run the container
docker run -d \
  --name cruncharr \
  -p 8585:8585 \
  -v $(pwd)/Cruncharr/config:/config \
  -v $(pwd)/Cruncharr/downloads:/downloads \
  -v $(pwd)/Cruncharr/widevine:/widevine \
  ghcr.io/mediavybz/cruncharr:latest

# Access the web interface
open http://localhost:8585
```

### Option 2: Docker Compose

```yaml
version: '3.8'

services:
  cruncharr:
    image: ghcr.io/mediavybz/cruncharr:latest
    container_name: cruncharr
    ports:
      - "8585:8585"
    volumes:
      - ./config:/config
      - ./downloads:/downloads
      - ./widevine:/widevine
    environment:
      - CRUNCHYROLL_CONFIG_PATH=/config/cruncharr.yaml
    restart: unless-stopped
```

```bash
docker-compose up -d
```

## First Time Setup

1. **Open the web interface** at `http://your-server:8585`
2. **Sign in** with your account credentials
3. **Configure settings**:
   - Library directory: `/downloads` (inside container)
   - Temp directory: `/tmp/cruncharr`
   - Endpoints: use defaults (recommended)
   - Audio languages: `ja-JP` (Japanese) by default
   - Subtitles: `en-US` (English) by default
   - Adjust these in Settings, then click Save

## Volume Mounts

| Path | Purpose | Required |
|------|---------|----------|
| `/config` | Settings, tokens, catalog/activity history | Yes |
| `/downloads` | Your media library files | Yes |
| `/widevine` | Optional device files, if your source requires them | Optional |

## Hardware Acceleration (GPU)

The image ships a full-GPU **ffmpeg** build (NVIDIA NVENC/CUDA, Intel QSV, AMD
VAAPI/AMF, Vulkan). It is used by the **Sync HW Accel** option
(Settings → Muxing). The dropdown lists **only the GPUs currently available to
the container** — if you don't pass a GPU in, only "None (CPU)" appears.

**Intel / AMD (VAAPI, QSV, AMF)** — pass the render device:

```yaml
    devices:
      - /dev/dri:/dev/dri
    group_add:
      - "video"
      - "render"
```

**NVIDIA (NVENC/CUDA)** — requires the [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html)
on the host:

```yaml
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: all
              capabilities: [gpu]
```

After (re)creating the container, open **Settings → Muxing → Sync HW Accel** and
the detected GPU(s) will be listed (e.g. `NVIDIA (CUDA)` or
`Intel / AMD (VAAPI) (/dev/dri/renderD128)`).

> The runtime image is Debian-based; ffmpeg is a BtbN GPL build. NVIDIA encode
> needs the host driver injected by the NVIDIA Container Toolkit — the image
> alone does not bundle the driver.

## Configuration

### Config File

Create `config/cruncharr.yaml`:

```yaml
crunchyroll:
  email: "your@email.com"
  password: "yourpassword"
  use_beta_api: true
  stream_endpoint:
    endpoint: tv/android_tv
    use_default: true
    video: true
    audio: true
download:
  output_dir: /downloads
  temp_dir: /tmp/cruncharr
  filename: "${seriesTitle} - S${season}E${episode} [${height}p]"
  quality_video: best
  quality_audio: best
  dub_languages:
    - ja-JP  # Default: Japanese only
  soft_subs:
    - en-US  # Default: English only
  simultaneous_downloads: 2
  simultaneous_processing_jobs: 2
queue:
  persist_queue: false
  auto_download: false
history:
  enabled: true
  remove_missing_episodes: true
```

## Building from Source

```bash
git clone https://github.com/mediavybz/Cruncharr.git
cd Cruncharr
docker build -t cruncharr .
docker run -d -p 8585:8585 -v ./config:/config -v ./downloads:/downloads cruncharr
```

Pushes to the `testing` branch validate the production image for AMD64 and ARM64 on the self-hosted
Unraid Forgejo runner, then load and health-check an AMD64 container. The workflow is
`.forgejo/workflows/docker-testing.yml`. To publish `ghcr.io/mediavybz/cruncharr:testing`, manually
run that workflow with its `publish` input enabled. Publishing requires a repository Actions secret
named `GHCR_TOKEN` with permission to write the GitHub Container Registry package.

For a local fallback, validate both supported architectures without publishing:

```bash
bash scripts/publish-docker.sh
```

Or publish the testing image locally when the Unraid runner is unavailable:

```bash
bash scripts/publish-docker.sh --tag ghcr.io/mediavybz/cruncharr:testing --push
```

### Reclaim local Docker build storage

After verifying a published image, remove the disposable cache from the builder used for the publish:

```bash
docker buildx prune --all --force
docker image prune --force
docker system df
```

If a dedicated temporary builder was created, remove it with `docker buildx rm <builder-name>`. To remove **all** unused local images, stopped containers, networks, build cache, and volumes, use `docker system prune --all --volumes --force`; this is machine-wide and must not be run when unused volumes contain data that should be kept.

On Windows, Docker may free space internally without shrinking its physical VHD. With Docker data already pruned, run the following from an elevated PowerShell window:

```powershell
$dockerVhd = "$env:LOCALAPPDATA\Docker\wsl\disk\docker_data.vhdx"
docker desktop stop
wsl --shutdown
Optimize-VHD -Path $dockerVhd -Mode Full
docker desktop start
```

## API Endpoints

The backend exposes a REST API at `http://localhost:8585/api/v1/`:

- `GET /api/v1/auth/status` - Auth status
- `POST /api/v1/auth/login` - Sign in with credentials
- `GET /api/v1/queue` - Activity queue
- `POST /api/v1/queue` - Add to queue
- `GET /api/v1/series/search?query=QUERY` - Search catalog
- `GET /api/v1/series/{id}/episodes` - Get episodes
- `GET /api/v1/config` - Get configuration
- `POST /api/v1/config` - Update configuration

## Features

- **Web interface**: Single-page app at `/` - search the catalog, browse episodes, manage activity, configure settings
- **Sign-in**: Account login with automatic token refresh
- **Library**: Select episodes with multi-track support and concurrent processing with progress tracking
- **Queue**: Add episodes to a queue, optional automatic processing, activity management
- **History**: Track your catalog, Sonarr integration, refresh series data
- **Calendar**: View upcoming release schedule
- **Endpoints**: Configurable device endpoints (Android TV, Web, Console) with working defaults
- **Languages**: Select audio and subtitle languages (default: Japanese audio, English subtitles)
- **Muxing**: Automatic muxing with ffmpeg/mkvtoolnix, MP4/MP3 output options
- **Hardware acceleration**: Full-GPU ffmpeg (NVENC/CUDA, QSV, VAAPI/AMF, Vulkan); GPU picker for sync that lists only the devices passed into the container
- **Themes**: Multiple selectable UI themes (Dark, Light, Cinematic, AMOLED, Nebula, Seerr) with a centralized design-token system
- **Settings**: Full settings panel with library, queue, history, notifications, and appearance options
- **Notifications**: Webhook support for completion/failure events

## Troubleshooting

### No Audio Track
- Check that `dub_languages` in config includes the item's audio language
- Default is `ja-JP` only - add more languages in Settings if needed

### Sign-in Issues
- Delete `config/token.json` to force a fresh sign-in
- Check that the stream endpoint "Use Default" is enabled

### Reset All Settings
- Stop the container
- Delete `config/cruncharr.yaml`
- Restart the container - it will recreate with defaults

### Device Files
- Some sources require `device_private_key.pem` and `device_client_id_blob.bin` in the `widevine/` directory

## Security

Cruncharr is built for **private, self-hosted** use (your LAN, or behind a reverse
proxy). Before exposing it more widely:

- **API key.** The API is unauthenticated by default. If the container is reachable
  from an untrusted network, set the `CRUNCHARR_API_KEY` env var — every `/api/*`
  request must then present it via the `X-Api-Key` header, `Authorization: Bearer
  <key>`, or (only for the browser Server-Sent-Events stream, which cannot send
  headers) the `?apiKey=` query param. The web interface prompts for the key once and
  stores it locally. Health checks stay exempt.
- **TLS.** The container serves plain HTTP. Terminate HTTPS at a reverse proxy
  (Caddy / nginx / Traefik) if it leaves your host.
- **Credentials at rest.** Your account login and any Sonarr/proxy keys live in
  `config/cruncharr.yaml` in plaintext (file mode `600`) — the same model as
  Sonarr/Radarr. Protect the `/config` volume accordingly. The config API never
  returns secrets (they read back as `[configured]`), and credentials are never
  written to logs.
- **CORS.** Cross-origin API access defaults to `http://localhost:8585`; override
  with the `CORS_ORIGINS` env var (comma-separated) if you serve the UI elsewhere.
- **Outbound calls are guarded.** Webhook URLs are rejected if they resolve to
  private/loopback/link-local addresses (SSRF protection); catalog images are proxied
  and cached server-side only from the source's own image host over HTTPS.

## Credits

This project is based on the original **Crunchy-Downloader** desktop application by [Crunchy-DL](https://github.com/Crunchy-DL/Crunchy-Downloader). All core logic and media processing is ported from the upstream source, as required by its license.

- **Upstream**: https://github.com/Crunchy-DL/Crunchy-Downloader
- **Upstream License**: MIT License (Copyright (c) 2024 Crunchy DL)
- **Port**: Dockerized web interface with REST API and headless backend

## License

MIT License - See upstream [LICENSE](https://github.com/Crunchy-DL/Crunchy-Downloader/blob/master/LICENSE) for full text.
