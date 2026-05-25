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
   - Temp directory: `/tmp/cruncharr`
   - Stream endpoints: Use defaults (recommended)
   - Languages: All languages selected by default

## Volume Mounts

| Path | Purpose | Required |
|------|---------|----------|
| `/config` | Config file, auth tokens, history | Yes |
| `/downloads` | Downloaded videos | Yes |
| `/widevine` | Widevine CDM files (device_private_key.pem, device_client_id_blob.bin) | For premium content |

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
    - ja-JP
    - en-US
    - de-DE
    - es-ES
    - es-419
    - fr-FR
    - it-IT
    - pt-BR
    - pt-PT
    - ru-RU
    - hi-IN
    - ar-SA
    - zh-CN
    - ko-KR
    - pl-PL
    - tr-TR
    - th-TH
    - vi-VN
    - id-ID
    - ms-MY
    - ta-IN
    - te-IN
  soft_subs:
    - ja-JP
    - en-US
    - de-DE
    - es-ES
    - es-419
    - fr-FR
    - it-IT
    - pt-BR
    - pt-PT
    - ru-RU
    - hi-IN
    - ar-SA
    - zh-CN
    - ko-KR
    - pl-PL
    - tr-TR
    - th-TH
    - vi-VN
    - id-ID
    - ms-MY
    - ta-IN
    - te-IN
  simultaneous_downloads: 2
  simultaneous_processing_jobs: 2
queue:
  auto_download: true
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

- **Web UI**: Single-page app at `/` - search, queue, settings, history
- **Auth**: Automatic token refresh, profile switching
- **Downloads**: Concurrent downloads with progress tracking
- **Queue**: Auto-download, manual start, pause/resume
- **History**: Track downloaded episodes, prevent duplicates
- **Stream Endpoints**: Configurable device endpoints (Android TV, Web, Console)
- **Languages**: All audio dubs and subtitle languages supported
- **Muxing**: Automatic muxing with ffmpeg/mkvtoolnix
- **Notifications**: Webhook support for completion/failure

## Troubleshooting

### No Audio Track
- Check `dub_languages` in config includes the episode's audio language
- Default is all 22 languages

### Auth Issues
- Delete `config/token_tv.json` to force re-login
- Check `stream_endpoint.use_default: true`

### Widevine/DRM
- Place `device_private_key.pem` and `device_client_id_blob.bin` in `widevine/` directory
- Required for downloading premium/DRM content

## Credits

This project is based on the original **Crunchy-Downloader** desktop application by [Crunchy-DL](https://github.com/Crunchy-DL/Crunchy-Downloader). All core download logic, Crunchyroll API integration, and media processing is ported from the upstream source.

- **Upstream**: https://github.com/Crunchy-DL/Crunchy-Downloader
- **Port**: Dockerized web UI version with REST API and headless backend

## License

MIT
