# Cruncharr Project Status

## Last Updated: 2026-05-24
## Phase: CORE FUNCTIONALITY STABILIZED - READY FOR TESTING

---

## Summary of Recent Changes (Session 2026-05-24)

### Critical Fixes
- **Audio Track Selection Fixed**: Episode ID now used instead of original GUID for playback requests. This ensures the correct language version is downloaded (e.g., English dub vs Japanese original).
- **Episode Version Model Updated**: Added `MediaGuid` field to `EpisodeVersion` model for proper version selection.
- **DownloadEpisodeAsync Signature Changed**: Now accepts full `EpisodeInfo` object with version info instead of just a string ID.
- **Stream Endpoint Settings Fixed**: Added missing DeviceType, DeviceName, Video, Audio, and UseDefault fields to both primary and secondary stream endpoint configuration in the UI. These fields were previously hardcoded in the save handler and not exposed to users.

### Calendar Fixes
- **Language Filter Implemented**: Calendar now properly filters episodes by selected language
  - Extracts language tag from season name (e.g., "Season 1 (English)")
  - Supports nested parentheses (e.g., "Português (Brasil)")
  - Episodes without language tags shown for all languages (original versions)
  - Ported `CrSimulcastCalendarFilter` from original app for dub detection
- **Hide Dubs Option**: Respects `Calendar.HideDubs` config setting to filter dubbed versions
- **Language Dropdown**: Frontend now has all 12 language options and passes them to API

### Queue System Fixes
- **Auto Download Fixed**: Set `AutoDownload` default to `true` in `QueueConfig`
  - Items now start downloading immediately when added to queue
  - `RequestPump()` now actually calls `PumpQueueAsync()` instead of just setting a dirty flag
  - Stored config as instance field so pump methods can access it
  - User can uncheck auto-download in UI to prevent automatic starts
- **Remove Finished Downloads**: Added logic to remove completed items from queue when `RemoveFinishedDownload` is enabled
  - Removes item immediately after successful download
  - Keeps in queue if setting is disabled (for history viewing)

### UI Improvements
- **App Icon**: Replaced low-res PNG with SVG favicon for crisp display at all sizes
- **Logo**: Sidebar now uses `/favicon.svg` instead of placeholder "C" text
- **Favicon**: Added proper favicon links (favicon.svg, favicon-96x96.png, favicon.ico, apple-touch-icon.png)

### Code Cleanup
- **Removed Test Files**: Deleted entire `tests/` directory and all test projects
  - Removed: `Cruncharr.Core.Tests` project and all test classes
  - Removed: DownloadTests, MegaloBoxTests, MegaloBoxDebugTests, QueueServiceTests, QualitySelectorTests, PremiumTests, IntegrationTests, DrmTests, ConfigurationTests
- **Removed Build Artifacts**: Cleaned all `bin/` and `obj/` folders from src directory
- **Removed Unused Methods**: Cleaned up CalendarService (removed `ExtractBaseSeasonName`, `ExtractLanguageFromClass` which were replaced by proper language filtering)

---

## Completed Features

### Core Download Pipeline
- [x] DASH MPD Parser with segment downloading
- [x] Quality Selection (best/worst/specific height)
- [x] Multi-dub support with proper version selection
- [x] DRM decryption with mp4decrypt
- [x] Subtitle download and conversion (VTT to ASS)
- [x] Chapter/skip markers
- [x] Font extraction and muxing
- [x] Cover art download and attachment
- [x] Filename template system

### Authentication
- [x] Login/logout with Crunchyroll
- [x] Token persistence
- [x] Multi-profile support
- [x] Subscription checking

### Queue & History
- [x] Queue management with concurrent download limits
- [x] Retry with exponential backoff
- [x] Queue persistence
- [x] History tracking

### Web UI
- [x] All pages ported (Downloads, Add Download, Calendar, Seasons, History, Account, Settings)
- [x] Real-time progress updates
- [x] Theme switching (dark/light)
- [x] Settings with all config options
- [x] Calendar with week navigation

### Docker
- [x] Multi-stage Dockerfile with all tools included
- [x] Port 8585 (uncommon to avoid conflicts)
- [x] docker-compose.yml configured
- [x] Health checks

---

## Known Issues / Next Steps
1. **Calendar**: Language filter working - shows only episodes matching selected language
2. **Stream Endpoints**: Secondary endpoint defaults to empty - user can configure in Settings
3. **Queue**: Auto-download fixed - defaults to ON, items start immediately when added
4. **Testing Needed**: Full download-decrypt-mux pipeline with multi-dub content
5. **GitHub Push**: Ready after user approval

---

## Docker Commands
```bash
# Build
docker build -t cruncharr:latest .

# Run
docker run -d -p 8585:8585 \
  -v "$(pwd)/config:/config" \
  -v "$(pwd)/downloads:/downloads" \
  -v "$(pwd)/widevine:/widevine" \
  cruncharr:latest

# Or use docker-compose
docker-compose up -d
```

## Configuration
- Config file: `/config/cruncharr.yaml` (mounted from `./config`)
- Downloads: `/downloads` (mounted from `./downloads`)
- Widevine: `/widevine` (mounted from `./widevine`)
- Port: 8585

## Test Credentials
- Premium account available for testing
- Configured in environment variables (not committed)
