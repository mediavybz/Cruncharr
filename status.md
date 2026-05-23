# Cruncharr Project Status - DETAILED AUDIT

## Last Updated: 2026-05-23
## Phase: READY FOR LOCAL TESTING - ALL CORE FEATURES PORTED

### Summary
All core features from Crunchy-Downloader have been ported. Original UI has been ported to web. Docker image includes all tools (ffmpeg, mkvtoolnix) - only Widevine files need to be provided by users. Ready for local Docker Desktop testing before GitHub push.

---

## COMPLETED - Everything Ported from Crunchy-Downloader

### Core Download Pipeline
- [x] **DASH MPD Parser**: `DashSegmentDownloader.ParseManifest()`
  - SegmentList, SegmentTemplate+SegmentTimeline, SegmentBase (init extraction)
  - ContentProtection PSSH extraction
- [x] **Segment-by-segment DASH downloader**: `DashSegmentDownloader.DownloadTrackAsync()`
  - Parallel threaded downloads with semaphore throttling
  - Retry with exponential backoff
  - Byte-range support
  - Resume via `.resume` files
- [x] **Quality Selection**: `QualitySelector.cs`
  - `WidthBucket`, `SnapToAudioBucket`, `ToKbps`
  - Deduplicates by (height, widthBucket)
  - Supports "best", "worst", or specific height
- [x] **DRM Decryption Pipeline**: Auto-detects `mp4decrypt`/`shaka-packager`
  - Decrypts files with Widevine keys after download

### Error Handling & Recovery
- [x] **Stream Error Models**: `StreamError.cs` with all error codes
- [x] **TOO_MANY_ACTIVE_STREAMS**: De-auth + retry flow
- [x] **Rate Limit 4294**: Retry-After header + exponential backoff
- [x] **Maturity Rating Errors**: Proper handling
- [x] **DeAuthVideoAsync()**: Endpoint for releasing active streams

### Authentication (Full CRAuth.cs Port)
- [x] Anonymous + Premium login (beta API, TV endpoint, Basic auth)
- [x] **Multi-profile support**: `GetMultiProfileAsync`, `ChangeProfileAsync`
- [x] **Cloudflare/DDOS guard detection**: `CheckForCloudflare`
- [x] **Detailed subscription checking**: Expiration dates, grace periods, third-party, Funimation
- [x] **Token persistence**: `LoadToken`, `SaveToken`, `DeleteToken` with configurable path
- [x] **Token restoration**: `LoginWithTokenAsync` for saved sessions
- [x] **Avatar support** in profiles
- [x] AuthController: `POST /api/v1/auth/login`, `POST /api/v1/auth/logout`, `GET /api/v1/auth/status`, `GET /api/v1/auth/profiles`, `POST /api/v1/auth/profiles/switch`

### Subtitles & Chapters
- [x] **Subtitle download**: VTT/ASS format with language filtering
- [x] **VTT to ASS conversion**: `ConvertVttToAss()`
- [x] **Chapter/Skip Markers**: `ChapterService.cs`
  - Skip-events API (`static.crunchyroll.com/skip-events/production/{mediaId}.json`)
  - Fallback to old datalab API
  - OGM format output
  - FFMetadata conversion for FFmpeg

### Fonts & Muxing
- [x] **Font extraction**: `FontService.cs`
  - Extracts font names from ASS styles and `\fn` tags
  - Resolves from known Crunchyroll mappings + system font directories
  - Attaches to mkvmerge output
- [x] **Cover art download**: Downloads episode cover image, attaches as `cover.png`
- [x] **Muxing pipeline**: mkvmerge primary, ffmpeg fallback
  - Handles video/audio/subtitles/chapters/fonts/cover art

### Filename System
- [x] **Template engine**: `FilenameService.cs`
  - `{SeriesTitle}`, `{EpisodeTitle}`, `{season:00}`, `{episode:00}`
  - `${var}` syntax, quality/language placeholders
  - Whitespace replacement
  - Proper sanitization (illegal chars, reserved names, length limit)

### Queue System
- [x] **QueueService.cs**: Concurrent download limits via `SemaphoreSlim`
- [x] **Retry scheduling**: Exponential backoff with automatic wake timers
- [x] **QueuePersistenceService**: JSON save/restore with debounced writes
- [x] **ProcessingSlotManager**: Post-download processing limits

### API & Web UI (Original UI Ported)
- [x] **ASP.NET Core Web API**: `Cruncharr.API`
  - Endpoints: health, queue, history, series search, config, calendar, auth
- [x] **Web UI**: Original Crunchy-Downloader UI ported to web
  - **Navigation**: Downloads, Add Download, Calendar, Seasons, History (main); Account, Settings (footer)
  - **Icons**: SVG icons matching original Fluent System Icons (Download, Add, Calendar, Clock, Library, Contact, Settings)
  - **Colors**: WinUI 3 / Fluent Design dark theme (ported from FluentAvalonia)
  - **Downloads page**: Queue items with thumbnails (208x117), progress bars, title, info text, speed, time. Top controls: Remove Finished, Auto Download, Shutdown PC toggles. Retry Failed, Pause Running, Clear Queue buttons. Per-item: pause/resume, retry, remove
  - **Add Download page**: URL search with popup results (120x180 posters), season dropdown, episode list with thumbnails (208x117), checkboxes, "All" checkbox, "Add" button
  - **Calendar page**: 7-day week grid with prev/next buttons, language selector, episode items with thumbnails
  - **History page**: Poster grid view (with new episode badges) and table view (expandable seasons/episodes). Toolbar: Refresh Filtered, Add To Queue, Edit, Search, Sonarr, View toggle, Sort, Filter
  - **Account page**: Centered layout with 170x170 circular avatar, name, subscription time, login/logout
  - **Settings page**: Tabbed layout - General, Download, Queue, Sonarr, Notifications
  - Real-time polling, toast notifications, auth status display
  - **Removed**: Update page (not applicable to Docker/web version)

### Calendar & History
- [x] **CalendarService.cs**: Full port from Crunchy-Downloader
  - HTML scraping from simulcastcalendar
  - Multi-language support
  - AniList integration
- [x] **HistoryService.cs**: Persistent JSON with deduplication
  - Series/Season/Episode tree structure
  - Download progress tracking

### Crunchyroll API
- [x] **CrunchyrollApiService.cs**: Full CrSeries port
  - Cover art extraction (poster_tall, poster_wide, thumbnail)
  - Episode versions support
  - Subtitle locales

### Configuration
- [x] **CruncharrConfig.cs**: Comprehensive config system
  - Download, Queue, History, Notifications sections
  - TokenFilePath for custom token location
  - YAML/JSON + environment variables

### Testing
- [x] **51/51 tests passing** (27 original + 24 QueueService tests)

### Docker Infrastructure
- [x] **Multi-stage Dockerfile**: Alpine 3.21 base
  - Image size: **361MB** (includes all tools)
  - Includes **ffmpeg** (v6.1.2) and **mkvtoolnix** (v88.0) - no external tools needed
  - Enabled `InvariantGlobalization=true`, removed `icu-libs`
  - Only **Widevine files** need to be mounted externally
- [x] **docker-compose.yml**: Updated - removed `/tools` volume requirement
- [x] **Health checks**: `wget` on port 8585
- [x] **GitHub Actions**: `.github/workflows/docker-publish.yml`
- [x] **Documentation**: `docs/DOCKER.md` updated - only Widevine is external requirement

### Bug Fixes During Session
- [x] Fixed Docker `/lib` mount overwriting Alpine system libraries → changed to `/tools`
- [x] Fixed `CoverImageUrl` typo → `CoverArtUrl` in `DownloadService.cs`
- [x] Fixed `QueueService` timing issue with `ProcessQueueAsync_Processes_Items` test

---

## Feature Status Matrix

### Deployment Ready
| Feature | Status | Notes |
|---------|--------|-------|
| MPD/DASH Parser | **DONE** | All segment types supported |
| Segment Downloader | **DONE** | Parallel, retry, resume |
| DRM Decryption | **DONE** | mp4decrypt/shaka auto-detect |
| Quality Selection | **DONE** | Best/worst/specific height |
| Stream Error Recovery | **DONE** | De-auth, rate limits, backoff |
| Queue Management | **DONE** | Concurrent limits, retry, persistence |
| Resume Support | **DONE** | `.resume` files |
| Authentication | **DONE** | Full CRAuth.cs port |
| Chapters | **DONE** | Skip-events API + fallback |
| Fonts | **DONE** | ASS extraction + system resolution |
| Cover Art | **DONE** | Download + mux |
| Filename Templates | **DONE** | Full template engine |
| Calendar | **DONE** | HTML scraping + AniList |
| History | **DONE** | Rich tree structure |
| Web UI | **DONE** | *arr-style with auth |
| Docker | **DONE** | 133MB, health checks |

### Deferred Features
| Feature | Status | Notes |
|---------|--------|-------|
| Multi-Dub "Download Once" | **DEFERRED** | Download video once, reuse for multiple audio dubs |
| Original Crunchy-Downloader UI | **DEFERRED** | Port after all other features complete |

---

## Docker Image Details
- **Size**: 361MB (includes all tools)
- **Base**: Alpine 3.21
- **Port**: 8585 (uncommon, avoids *arr conflicts)
- **Tools INCLUDED** (no mounting needed):
  - `ffmpeg` v6.1.2 - Video/audio processing and muxing
  - `mkvmerge` v88.0 - MKV container muxing
  - `mkvextract` - Extract tracks from MKV
- **Widevine NOT included** (ONLY external requirement - must be mounted in `/widevine`):
  - `device_client_id_blob.bin`
  - `device_private_key.pem`

---

## Test Account Credentials
- Premium: savion67@nethnn.com / Mr9mdmtt7t8
- Environment variables for integration tests:
  - `CRUNCHYROLL_TEST_EMAIL`
  - `CRUNCHYROLL_TEST_PASSWORD`

---

## Future-Proofing / Porting Strategy

### Current Approach
The UI is a **manual port** from Avalonia XAML to HTML/CSS/JS. When new versions of Crunchy-Downloader are released, updates need to be manually synced.

### Maintainability Features Added
- **Porting documentation comments** in `index.html` mapping original files to web UI sections
- **WinUI 3 color system** using CSS variables matching original FluentAvalonia resources
- **SVG icons** matching original Fluent System Icons (Download, Add, Calendar, Clock, Library, Contact, Settings)
- **API response shapes** kept similar to original ViewModels to minimize UI changes

### What Was Ported vs Created
- **Ported from original**: Layout structure, controls, icons, color scheme, page organization
- **Created new**: Web-specific implementation (HTML/CSS/JS instead of Avalonia), API integration layer

### How to Port Future Updates
1. Check the original View file (e.g., `DownloadsPageView.axaml`)
2. Find corresponding function in `index.html` (e.g., `renderDownloads()`)
3. Diff and apply changes manually
4. Update the porting documentation comment if new files are added

---

## Next Steps
1. **Local Docker Desktop Testing** (current)
2. **Real-world download-decrypt-mux pipeline test** with premium content
3. **GitHub push** (after user approval)
4. **Enable GitHub Actions** for automated Docker builds
5. **Multi-dub optimization** (deferred)
6. **Port original UI** (deferred)

---

## Local Testing Commands
```bash
# Build image
docker build -t cruncharr:latest .

# Run with required volumes
docker run -d -p 8585:8585 \
  -v "$(pwd)/config:/config" \
  -v "$(pwd)/downloads:/downloads" \
  -v "$(pwd)/tools:/tools" \
  -v "$(pwd)/widevine:/widevine" \
  cruncharr:latest

# Or use docker-compose
docker-compose up -d

# Check health
curl http://localhost:8585/api/v1/health

# Open Web UI
open http://localhost:8585
```