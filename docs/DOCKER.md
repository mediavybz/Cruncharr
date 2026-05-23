# Cruncharr Docker Documentation

## Overview

Cruncharr runs as a Docker container with an embedded Web API for *arr stack integration. The container exposes port **8585** (uncommon port to avoid conflicts) for API access and includes both the API server and CLI tool.

## Volume Paths

### Required Volumes

| Path | Description | Required |
|------|-------------|----------|
| `/config` | Configuration files (cruncharr.yaml, history.json, queue.json) | Yes |
| `/downloads` | Output directory for completed downloads | Yes |
| `/widevine` | Widevine DRM device files (see below) | Yes* |
| `/tmp/cruncharr` | Temporary processing directory | Yes |

### Optional Volumes

| Path | Description | Use Case |
|------|-------------|----------|
| `/app/fonts` | Subtitle fonts for muxing | ASS subtitle fonts |

### Optional Volumes

| Path | Description | Use Case |
|------|-------------|----------|
| `/app/fonts` | Subtitle fonts for muxing | ASS subtitle fonts |

## Widevine Setup (CRITICAL)

You MUST provide your own Widevine device files. These are NOT included in the image for legal reasons.

1. Obtain your Widevine device files:
   - `device_client_id_blob.bin`
   - `device_private_key.pem`

2. Place them in a directory and mount it to `/widevine`:
   ```yaml
   volumes:
     - ./widevine:/widevine
   ```

3. The application will automatically detect these files at runtime.

## Included Tools

The Docker image includes all required tools for downloading and processing:

- **ffmpeg** (v6.1.2): For video/audio processing and muxing
- **mkvmerge** (from mkvtoolnix v88.0): For MKV container muxing
- **mkvextract**: For extracting tracks from MKV files

You do NOT need to mount any tools.

## Widevine Setup (CRITICAL - ONLY EXTERNAL REQUIREMENT)

You MUST provide your own Widevine device files. These are NOT included in the image for legal/DRM reasons.

1. Obtain your Widevine device files:
   - `device_client_id_blob.bin`
   - `device_private_key.pem`

2. Place them in a directory and mount it to `/widevine`:
   ```yaml
   volumes:
     - ./widevine:/widevine
   ```

3. The application will automatically detect these files at runtime.

### What Each Directory Does

| Directory | What Goes Here | Why Needed |
|-----------|---------------|------------|
| `./widevine/` | device_client_id_blob.bin + device_private_key.pem | **ONLY external requirement** - Widevine CDM for license requests |
| `./config/` | cruncharr.yaml, history.json, queue.json | App configuration & state |
| `./downloads/` | Completed .mkv/.mp4 files | Final output directory |
| `./tmp/` | Temporary files during download | Processing workspace |
| `./fonts/` (optional) | .ttf/.otf font files | Embed fonts in ASS subtitles |

## Configuration

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `CRUNCHYROLL_EMAIL` | Crunchyroll account email | - |
| `CRUNCHYROLL_PASSWORD` | Crunchyroll account password | - |
| `CRUNCHYROLL_CONFIG_PATH` | Path to config file | `/config/cruncharr.yaml` |
| `CRUNCHYROLL_OUTPUT_DIR` | Download output directory | `/downloads` |
| `CRUNCHYROLL_TEMP_DIR` | Temporary processing directory | `/tmp/cruncharr` |
| `CRUNCHYROLL_WEBHOOK_URL` | Notification webhook URL | - |
| `CRUNCHYROLL_QUEUE_PERSIST` | Persist queue across restarts | `true` |
| `CRUNCHYROLL_QUEUE_AUTO_DOWNLOAD` | Auto-start downloads | `true` |
| `CRUNCHYROLL_QUEUE_PROCESSING_JOBS` | Concurrent processing jobs | `2` |
| `ASPNETCORE_URLS` | API bind address | `http://+:8585` |

### Config File (`/config/cruncharr.yaml`)

```yaml
crunchyroll:
  email: "your@email.com"
  password: "your-password"

download:
  output_dir: "/downloads"
  temp_dir: "/tmp/cruncharr"
  filename_template: "{SeriesTitle} - S{season:00}E{episode:00} - {EpisodeTitle}"
  quality: "best"
  dub_languages:
    - "ja-JP"
  subtitle_languages:
    - "en-US"
  simultaneous_downloads: 2
  retry_attempts: 5
  retry_delay_seconds: 5
  skip_muxing: false
  mux_fonts: true
  include_chapters: true
  convert_vtt_to_ass: true

queue:
  persist: true
  auto_download: true
  simultaneous_processing_jobs: 2

history:
  enabled: true
  remove_missing: true

notifications:
  webhook_url: ""
  webhook_method: "POST"
  on_complete: true
  on_error: true
```

## API Endpoints

### Health Check
- `GET /api/v1/health` - Service health status
- `GET /api/v1/health/ready` - Readiness probe
- `GET /api/v1/health/live` - Liveness probe

### Queue Management
- `GET /api/v1/queue` - List all queue items
- `POST /api/v1/queue` - Add episode to queue
- `DELETE /api/v1/queue/{id}` - Remove from queue
- `GET /api/v1/queue/stats` - Queue statistics

### History
- `GET /api/v1/history` - Download history
- `GET /api/v1/history/check/{episodeId}/{audioLanguage}` - Check if downloaded
- `GET /api/v1/history/series/{seriesId}` - History for series

### Series Lookup
- `GET /api/v1/series/search?query={query}` - Search Crunchyroll
- `GET /api/v1/series/{seriesId}/episodes` - Get episodes

### Configuration
- `GET /api/v1/config` - Get current configuration (sanitized)

## Quick Start

### 1. Create directories
```bash
mkdir -p config downloads tmp widevine
```

### 2. Add Widevine files
Place your `device_client_id_blob.bin` and `device_private_key.pem` in the `widevine/` directory.

### 3. Create config file
```bash
cat > config/cruncharr.yaml << 'EOF'
crunchyroll:
  email: "your@email.com"
  password: "your-password"

download:
  output_dir: "/downloads"
  quality: "best"
EOF
```

### 4. Run with docker-compose
```bash
docker-compose up -d
```

### 5. Access the UI
Open http://localhost:8585 in your browser.

## *arr Integration

### Sonarr/Radarr Webhook

Configure Sonarr/Radarr to send webhooks to Cruncharr:

1. In Sonarr: Settings > Connect > Add > Webhook
2. URL: `http://cruncharr:8585/api/v1/queue`
3. Method: POST
4. Payload:
```json
{
  "episodeId": "{{episode.id}}",
  "title": "{{episode.title}}",
  "seriesTitle": "{{series.title}}",
  "seasonNumber": {{season.number}},
  "episodeNumber": {{episode.number}}
}
```

### Custom Download Client

You can also configure Cruncharr as a custom download client in *arr apps by implementing the qBittorrent API compatibility layer (planned feature).

## Troubleshooting

### Container won't start
- Check that Widevine files exist in `/widevine`
- Verify config file syntax
- Check logs: `docker logs cruncharr`

### Downloads failing
- Verify Crunchyroll credentials
- Check `/downloads` directory permissions
- Ensure sufficient disk space

### API not responding
- Check container is running: `docker ps`
- Verify port mapping: `docker port cruncharr`
- Check health endpoint: `curl http://localhost:8585/api/v1/health`

## Building from Source

```bash
docker build -t cruncharr:latest .
docker run -p 8585:8585 \
  -v $(pwd)/config:/config \
  -v $(pwd)/downloads:/downloads \
  -v $(pwd)/widevine:/widevine \
  cruncharr:latest
```
