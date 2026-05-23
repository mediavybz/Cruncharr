# Cruncharr

Headless Crunchyroll downloader for the *arr stack (Sonarr, Radarr, Lidarr, etc.)

## Features

- **Headless**: No GUI, no VNC, no desktop dependencies
- **CLI-first**: Commands for login, download, search, series info
- **JSON Output**: `--format json` for *arr integration
- **Docker-native**: Minimal image with ffmpeg + mkvtoolnix
- **Configuration**: YAML or JSON config with environment variable overrides
- **Daemon Mode**: Continuous monitoring and download queue processing
- **Webhook Notifications**: Notify on completion or error
- **Queue Management**: Batch downloads with concurrent processing

## Quick Start

### Docker

```bash
# Run with docker
docker run --rm \
  -e CRUNCHYROLL_EMAIL=your@email.com \
  -e CRUNCHYROLL_PASSWORD=yourpassword \
  -v ./downloads:/downloads \
  -v ./config:/config \
  cruncharr download "https://www.crunchyroll.com/watch/episode-id"

# Or use docker-compose
docker-compose up
```

### Local (Development)

```bash
# Clone and build
git clone https://github.com/yourusername/cruncharr.git
cd cruncharr
dotnet build

# Run CLI
dotnet run --project src/Cruncharr.CLI -- --help

# Login
dotnet run --project src/Cruncharr.CLI -- login --email your@email.com --password yourpassword

# Download an episode
dotnet run --project src/Cruncharr.CLI -- download "https://www.crunchyroll.com/watch/episode-id"

# Search for a series
dotnet run --project src/Cruncharr.CLI -- search "Attack on Titan" --format json

# Run daemon mode
dotnet run --project src/Cruncharr.CLI -- daemon --interval 300
```

## Commands

```
cruncharr login --email <email> --password <password>
cruncharr logout
cruncharr download <url> [--format json] [--quiet] [--output <dir>]
cruncharr series <id> [--format json] [--download]
cruncharr search <query> [--format json]
cruncharr config get <key>
cruncharr config set <key> <value>
cruncharr daemon [--interval <seconds>]
```

## Configuration

### YAML (Recommended)

Create `/config/cruncharr.yml`:

```yaml
cruncharr:
  crunchyroll:
    email: "your@email.com"
    password: "yourpassword"
  download:
    output_dir: "/downloads"
    filename_template: "{SeriesTitle} - S{season:00}E{episode:00} - {EpisodeTitle}"
    quality: "best"
    dub_languages:
      - "ja-JP"
    subtitle_languages:
      - "en-US"
    simultaneous_downloads: 2
    retry_attempts: 5
  history:
    enabled: true
  notifications:
    webhook_url: "https://hooks.example.com/webhook"
    on_complete: true
    on_error: true
```

### Environment Variables

- `CRUNCHYROLL_EMAIL` - Crunchyroll account email
- `CRUNCHYROLL_PASSWORD` - Crunchyroll account password
- `CRUNCHYROLL_OUTPUT_DIR` - Download output directory
- `CRUNCHYROLL_TEMP_DIR` - Temporary directory
- `CRUNCHYROLL_CONFIG_DIR` - Configuration directory (default: `/config`)
- `CRUNCHYROLL_WEBHOOK_URL` - Webhook URL for notifications

Environment variables override config file values.

## Filename Templates

Use these placeholders in `filename_template`:

- `{SeriesTitle}` - Series name
- `{season}` - Season number
- `{season:00}` - Zero-padded season number
- `{episode}` - Episode number
- `{episode:00}` - Zero-padded episode number
- `{EpisodeTitle}` - Episode title
- `{height}p` - Video resolution

Example: `{SeriesTitle} - S{season:00}E{episode:00} - {EpisodeTitle}`

## Docker

### Build

```bash
docker build -t cruncharr .
```

### Run

```bash
# Download single episode
docker run --rm \
  -e CRUNCHYROLL_EMAIL=... \
  -e CRUNCHYROLL_PASSWORD=... \
  -v ./downloads:/downloads \
  cruncharr download "https://..."

# Daemon mode
docker run -d \
  --name cruncharr \
  -e CRUNCHYROLL_EMAIL=... \
  -e CRUNCHYROLL_PASSWORD=... \
  -v ./downloads:/downloads \
  -v ./config:/config \
  cruncharr daemon
```

### Volumes

- `/downloads` - Downloaded videos
- `/config` - Configuration and history
- `/lib` - Decryption tools (mp4decrypt, shaka-packager)
- `/widevine` - Widevine CDM files

## *arr Integration

### Sonarr Custom Script

Create a custom script in Sonarr that calls Cruncharr when an episode is grabbed:

```bash
#!/bin/bash
# Sonarr custom script

# Environment variables provided by Sonarr:
# $sonarr_series_title
# $sonarr_episodefile_seasonnumber
# $sonarr_episodefile_episodenumbers
# $sonarr_episodefile_episodetitles

# Download from Crunchyroll
cruncharr search "$sonarr_series_title" --format json | \
  jq -r '.[0].id' | \
  xargs -I {} cruncharr series {} --download
```

### Webhook Integration

Configure Cruncharr to send webhooks on completion:

```yaml
notifications:
  webhook_url: "http://sonarr:8989/api/v3/command"
  webhook_method: "POST"
  on_complete: true
```

## Development

### Project Structure

```
Cruncharr/
├── src/
│   ├── Cruncharr.Core/       # Core logic (models, services, config)
│   └── Cruncharr.CLI/        # CLI application
├── Dockerfile
├── docker-compose.yml
└── docs/
    ├── PORTING.md            # Guide for porting core logic
    ├── DEBUGGING.md          # Debugging and testing guide
    └── BUILDARR-REFERENCE.md # Buildarr integration patterns
```

### Building

```bash
# Build
dotnet build

# Run tests
dotnet test

# Publish for Linux
dotnet publish src/Cruncharr.CLI -c Release -r linux-x64 --self-contained false
```

### Porting Core Logic

The core download logic needs to be ported from the original Crunchy-Downloader codebase. See [docs/PORTING.md](docs/PORTING.md) for detailed instructions.

## Status

- [x] Project structure created
- [x] CLI framework implemented
- [x] Configuration system (YAML/JSON + env vars)
- [x] Docker infrastructure
- [x] Queue management
- [x] Webhook notifications
- [x] Daemon mode
- [ ] Core download logic (requires porting from original)
- [ ] Docker image build verification (requires WSL2)
- [ ] Integration tests

## License

MIT

## Disclaimer

This tool is for personal use only. Not affiliated with Crunchyroll. Use may violate Terms of Service.
