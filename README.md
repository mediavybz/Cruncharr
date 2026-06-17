# Cruncharr

Dockerized Crunchyroll downloader with web UI. Headless backend with REST API + single-page web frontend.

## Quick Deploy

### Option 1: Docker Run (Recommended)

```bash
# Create directories
mkdir -p cruncharr/config cruncharr/downloads cruncharr/widevine

# Run the container
docker run -d \
  --name cruncharr \
  -p 8585:8585 \
  -v $(pwd)/cruncharr/config:/config \
  -v $(pwd)/cruncharr/downloads:/downloads \
  -v $(pwd)/cruncharr/widevine:/widevine \
  ghcr.io/mediavybz/cruncharr:latest

# Access the web UI
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

1. **Access the web UI** at `http://your-server:8585`
2. **Login** with your Crunchyroll credentials
3. **Configure settings**:
   - Download directory: `/downloads` (inside container)
   - Temp directory: `/tmp/cruncharr.`
   - Stream endpoints: Use defaults (recommended)
   - Dub Languages: `ja-JP` (Japanese) by default
   - Soft Subs: `en-US` (English) by default
   - Change these in Settings → Crunchyroll tab, then click Save

## Volume Mounts

| Path | Purpose | Required |
|------|---------|----------|
| `/config` | Config file, auth tokens, history | Yes |
| `/downloads` | Downloaded videos | Yes |
| `/widevine` | Widevine CDM files (device_private_key.pem, device_client_id_blob.bin) | For premium content |

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
docker build -t cruncharr.
docker run -d -p 8585:8585 -v ./config:/config -v ./downloads:/downloads cruncharr
```

## API Endpoints

The backend exposes a REST API at `http://localhost:8585/api/v1/`:

- `GET /api/v1/auth/status` - Auth status
- `POST /api/v1/auth/login` - Login with credentials
- `GET /api/v1/queue` - Download queue
- `POST /api/v1/queue` - Add to queue
- `GET /api/v1/series/search?q=QUERY` - Search series
- `GET /api/v1/series/{id}/episodes` - Get episodes
- `GET /api/v1/config` - Get configuration
- `POST /api/v1/config` - Update configuration

## Features

- **Web UI**: Single-page app at `/` - search series, browse episodes, manage downloads, configure settings
- **Auth**: Login with Crunchyroll credentials, automatic token refresh
- **Downloads**: Select episodes with multi-dub support, concurrent downloads with progress tracking
- **Queue**: Add episodes to queue, auto-download option, download management
- **History**: Track downloaded episodes, Sonarr integration, refresh series data
- **Calendar**: View upcoming episode releases
- **Stream Endpoints**: Configurable device endpoints (Android TV, Web, Console) with working defaults
- **Languages**: Select audio dubs and subtitle languages (default: Japanese audio, English subs)
- **Muxing**: Automatic muxing with ffmpeg/mkvtoolnix, MP4/MP3 output options
- **Hardware acceleration**: Full-GPU ffmpeg (NVENC/CUDA, QSV, VAAPI/AMF, Vulkan); GPU picker for sync that lists only the devices passed into the container
- **Themes**: Multiple selectable UI themes (Dark, Light, Cinematic, AMOLED, Nebula) with a centralized design-token system
- **Settings**: Full settings panel with download, queue, history, notifications, and appearance options
- **Notifications**: Webhook support for completion/failure events

## Troubleshooting

### No Audio Track
- Check that `dub_languages` in config includes the episode's audio language
- Default is `ja-JP` only - add more languages in Settings if needed

### Auth Issues
- Delete `config/token.json` to force re-login
- Check that stream endpoint "Use Default" is enabled

### Reset All Settings
- Stop the container
- Delete `config/cruncharr.yaml.`
- Restart the container - it will recreate with defaults

### Widevine/DRM
- Place `device_private_key.pem` and `device_client_id_blob.bin` in `widevine/` directory
- Required for downloading premium/DRM content

## Security

Cruncharr is built for **private, self-hosted** use (your LAN, or behind a reverse
proxy). Before exposing it more widely:

- **API key.** The API is unauthenticated by default. If the container is reachable
  from an untrusted network, set the `CRUNCHARR_API_KEY` env var — every `/api/*`
  request must then present it via the `X-Api-Key` header, `Authorization: Bearer
  <key>`, or (only for the browser Server-Sent-Events stream, which cannot send
  headers) the `?apiKey=` query param. The web UI prompts for the key once and stores
  it locally. Health checks stay exempt.
- **TLS.** The container serves plain HTTP. Terminate HTTPS at a reverse proxy
  (Caddy / nginx / Traefik) if it leaves your host.
- **Credentials at rest.** Your Crunchyroll login and any Sonarr/proxy keys live in
  `config/cruncharr.yaml` in plaintext (file mode `600`) — the same model as
  Sonarr/Radarr. Protect the `/config` volume accordingly. The config API never
  returns secrets (they read back as `[configured]`), and credentials are never
  written to logs.
- **CORS.** Cross-origin API access defaults to `http://localhost:8585`; override
  with the `CORS_ORIGINS` env var (comma-separated) if you serve the UI elsewhere.
- **Outbound calls are guarded.** Webhook URLs are rejected if they resolve to
  private/loopback/link-local addresses (SSRF protection); catalog images are proxied
  and cached server-side only from `*.crunchyroll.com` over HTTPS.

## Credits

This project is based on the original **Crunchy-Downloader** desktop application by [Crunchy-DL](https://github.com/Crunchy-DL/Crunchy-Downloader). All core download logic, Crunchyroll API integration, and media processing is ported from the upstream source.

- **Upstream**: https://github.com/Crunchy-DL/Crunchy-Downloader
- **Upstream License**: MIT License (Copyright (c) 2024 Crunchy DL)
- **Port**: Dockerized web UI version with REST API and headless backend

## License

MIT License - See upstream [LICENSE](https://github.com/Crunchy-DL/Crunchy-Downloader/blob/master/LICENSE) for full text.
