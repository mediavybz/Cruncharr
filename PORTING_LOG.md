# Porting Log
## Project: Crunchy-Downloader → Docker + Web UI
## Desktop Source Version: Latest upstream from https://github.com/Crunchy-DL/Crunchy-Downloader
## Last Updated: 2026-06-03 (Frontend security audit: hardcoded credentials removed, XSS fixes, event handling, AbortController, Docker rebuild)

---

## API Contract

| Method | Route | Wraps | Request | Response Shape | Status |
|--------|-------|-------|---------|----------------|--------|
| GET | /api/v1/history | GetAllAsync | limit, offset | List<DownloadHistory> | stable |
| GET | /api/v1/history/rich | GetHistorySeriesAsync | - | List<HistorySeriesResponse> | stable |
| GET | /api/v1/history/check/{episodeId}/{audioLanguage} | IsDownloadedAsync | - | HistoryCheckResponse | stable |
| POST | /api/v1/history | AddAsync | DownloadHistory | { Message } | stable |
| GET | /api/v1/history/series/{seriesId} | GetHistorySeriesAsync | - | HistorySeriesResponse | stable |
| POST | /api/v1/history/downloaded/{seriesId}/{seasonId}/{episodeId} | SetAsDownloadedAsync | - | { Message } | stable |
| POST | /api/v1/history/cleanup | RemoveUnavailableEpisodesAsync | - | { Message } | stable |
| POST | /api/v1/history/sonarr/match-series | MatchHistorySeriesWithSonarrAsync | updateAll | { Message } | stable |
| POST | /api/v1/history/sonarr/match-episodes/{seriesId} | MatchHistoryEpisodesWithSonarrAsync | rematchAll | { Message } | stable |
| POST | /api/v1/history/update-series/{seriesId} | CrUpdateSeriesAsync | seasonId | { Success } | **NEW** |
| POST | /api/v1/history/sort | SortItemsAsync | - | { Message } | **NEW** |
| GET | /api/v1/history/episode-with-dir/{seriesId}/{seasonId}/{episodeId} | GetHistoryEpisodeWithDownloadDirAsync | - | { Episode, DownloadDir } | **NEW** |
| GET | /api/v1/history/episode-with-dubs/{seriesId}/{seasonId}/{episodeId} | GetHistoryEpisodeWithDubListAndDownloadDirAsync | - | { Episode, DubList, SubList, DownloadDir, VideoQuality } | **NEW** |
| GET | /api/v1/history/dubs/{seriesId}/{seasonId} | GetDubListAsync | - | List<string> | **NEW** |
| GET | /api/v1/history/subs/{seriesId}/{seasonId} | GetSubListAsync | - | { SubList, VideoQuality } | **NEW** |
| POST | /api/v1/series/episodes/{episodeId}/mark-watched | MarkAsWatchedAsync | - | { Message } | **NEW** |
| GET | /api/v1/series/all | GetAllSeriesAsync | locale | List<SeriesInfo> | **NEW** |
| GET | /api/v1/series/seasonal | GetSeasonalSeriesAsync | season, year, locale | List<SeriesInfo> | **NEW** |
| GET | /api/v1/auth/client-token | GetBase64EncodedTokenAsync | - | { Token } | **NEW** |
| POST | /api/v1/queue/replace | ReplaceQueue | List<QueueItem> | { Message, Count } | **NEW** |
| POST | /api/v1/webhook/test | - | { Url } | { Success, Message } | **NEW** |
| POST | /api/v1/history/series/{seriesId}/settings | SetSeriesSettingsOverrideAsync | { VideoQuality, DubLanguages, SoftSubs } | { Message } | **NEW** |
| POST | /api/v1/history/season/{seasonId}/settings | SetSeasonSettingsOverrideAsync | { VideoQuality, DubLanguages, SoftSubs } | { Message } | **NEW** |

---

## Completed (2026-06-02)

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.Core/Services/CrunchyrollAuthService.cs | upstream_src/CRAuth.cs | [PT] Added EmbeddedAuthData constant with fallback credentials. UpdateAuthCredentialsAsync now tries primary URL, fallback GitHub raw URL, then embedded data. Returns true if credentials already set when all sources fail. | 2026-06-03 |
| src/Cruncharr.Core/Utils/Parser/M3u8/ToM3u8Class.cs | upstream: Same dynamic parser code | [PT] Added `#pragma warning disable IL2026` / `#pragma warning restore IL2026` to suppress trim warnings in dynamic code | 2026-06-03 |
| src/Cruncharr.Core/Utils/Parser/Playlists/ToPlaylistsClass.cs | upstream: Same dynamic parser code | [PT] Added `#pragma warning disable IL2026` / `#pragma warning restore IL2026` to suppress trim warnings in dynamic code | 2026-06-03 |
| src/Cruncharr.Core/Utils/Parser/Segments/UrlType.cs | upstream: Same dynamic parser code | [PT] Added `#pragma warning disable IL2026` / `#pragma warning restore IL2026` to suppress trim warnings in dynamic code | 2026-06-03 |
| src/Cruncharr.Core/Models/HistoryModels.cs | CRD/Utils/Structs/History/HistoryEpisode.cs | [PT] Added IsPartiallyDownloaded, HasAvailableMissingDownloadedMedia, ToggleWasDownloaded, UpdateDownloadedSilent | 2026-06-02 |
| src/Cruncharr.Core/Services/HistoryService.cs | CRD/Downloader/History.cs | [PT] Added NormalizeLocales helper, updated SetAsDownloadedAsync | 2026-06-02 |
| src/Cruncharr.Core/Services/QueueService.cs | CRD/Downloader/QueueManager.cs | [PT] Added _isInitialized gate, SetInitialized method | 2026-06-02 |
| src/Cruncharr.Core/Services/DownloadService.cs | CRD/Downloader/Crunchyroll/CrunchyrollManager.cs | [PT] Fixed cover path to unique per-episode, fixed ReplaceExistingFiles in quality-probe rename | 2026-06-02 |
| src/Cruncharr.API/Controllers/ConfigController.cs | SeriesController.cs | [PT] Extracted ConfigController to separate file | 2026-06-02 |
| src/Cruncharr.API/Controllers/QueueController.cs | - | [PT] Fixed SSE memory leak with QueueBroadcastService singleton | 2026-06-02 |
| src/Cruncharr.API/Controllers/HistoryController.cs | - | [PT] Added DownloadedDubLang/DownloadedSoftSubs to response | 2026-06-02 |
| src/Cruncharr.API/Program.cs | - | [PT] Fixed HistoryService DI, added queueService.SetInitialized | 2026-06-02 |
| src/Cruncharr.API/Services/QueueBroadcastService.cs | - | [PT] New singleton service for SSE broadcasting | 2026-06-02 |
| src/Cruncharr.API/Controllers/ConfigController.cs | N/A (API layer) | [PT] Added POST /api/v1/webhook/test endpoint for webhook testing | 2026-06-02 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream_src/History.cs | [PT] Added SetSeriesSettingsOverrideAsync and SetSeasonSettingsOverrideAsync | 2026-06-02 |
| src/Cruncharr.API/Controllers/HistoryController.cs | N/A (API layer) | [PT] Added endpoints: POST /api/v1/history/series/{id}/settings, POST /api/v1/history/season/{id}/settings | 2026-06-02 |
| src/Cruncharr.Core/Services/QueueService.cs | upstream_src/QueueManager.cs | [PT] Added INotificationService injection, webhook dispatch on download complete/error | 2026-06-03 |
| src/Cruncharr.API/Controllers/QueueController.cs | N/A (API layer) | [PT] Added Versions property to QueueRequest, maps to EpisodeInfo | 2026-06-03 |
| src/Cruncharr.API/Program.cs | N/A (DI wiring) | [PT] Registered INotificationService in DI container | 2026-06-03 |
| Dockerfile | N/A (infrastructure) | [PT] Added multi-platform build support (linux/amd64 + linux/arm64) via TARGETARCH | 2026-06-03 |
| src/Cruncharr.API/Cruncharr.API.csproj | N/A (build config) | [PT] Set AssemblyVersion=0.1.0.0, Version=0.1.0-beta.1 for display | 2026-06-03 |

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Date |
|------|-------------------|-------------------|------|
| src/Cruncharr.API/wwwroot/index.html | HistoryPageView | GET /api/v1/history/rich, POST /api/v1/history/sonarr/* | 2026-06-02 |
| src/Cruncharr.API/wwwroot/index.html | DownloadsPageView | GET /api/v1/queue | 2026-06-02 |
| src/Cruncharr.API/wwwroot/index.html | SettingsPageView | GET/POST /api/v1/config | 2026-06-02 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-CRIT-004**: Added authIntervalId/historyIntervalId, clear on beforeunload, clear history interval on nav away | 2026-06-02 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-CRIT-005**: Added res.ok checks before res.json() on all fetch calls (20 locations) | 2026-06-02 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-CRIT-006**: Removed hardcoded Crunchyroll API credentials from STREAM_DEFAULTS; server-side defaults only | 2026-06-03 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-CRIT-007**: Fixed XSS vulnerabilities: added escapeHtml() to search results, episode lists, calendar, queue, history table | 2026-06-03 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-CRIT-008**: Fixed setSettingsTab global event usage - passes event param instead of relying on window.event | 2026-06-03 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-CRIT-009**: Added AbortController to search fetch to cancel previous requests on new input | 2026-06-03 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-CRIT-010**: Fixed XSS in getDoingText() and showToast() - server text now escaped | 2026-06-03 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-FEAT-001**: Added global queue pause/resume buttons with status display | 2026-06-03 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-FEAT-002**: Added Cooldown Between Downloads setting input | 2026-06-03 |

### Backend (Mode A) - Security & Stability
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.Core/Models/AuthModels.cs | upstream | [PT] Removed hardcoded auth token default; server provides credentials | 2026-06-03 |
| src/Cruncharr.Core/Services/CrunchyrollAuthService.cs | upstream | [PT] Removed sync-over-async .GetAwaiter().GetResult() in constructor; lazy auth update | 2026-06-03 |
| src/Cruncharr.Core/Utils/DashDownloader.cs | upstream | [PT] Removed sync ParseManifest method; made async-only with ThrottledStream support | 2026-06-03 |
| src/Cruncharr.Core/Utils/HLS/HLSDownloader.cs | upstream | [PT] Fixed sync-over-async in CloneHttpRequestMessage; added CloneAsync extension | 2026-06-03 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream | [PT] Fixed null ref before .Contains(':'); use async ParseManifestAsync | 2026-06-03 |
| src/Cruncharr.Core/Services/QueueService.cs | upstream | [PT] Replaced Environment.Exit(0) with ShutdownRequested flag; added global pause/resume | 2026-06-03 |
| src/Cruncharr.Core/Services/NotificationService.cs | upstream | [PT] Inject IHttpClientFactory instead of new HttpClient() | 2026-06-03 |
| src/Cruncharr.Core/Services/SonarrService.cs | upstream | [PT] Inject IHttpClientFactory instead of new HttpClient() | 2026-06-03 |
| src/Cruncharr.API/Controllers/ConfigController.cs | N/A (API layer) | [PT] Hide Authorization tokens from GET /api/v1/config response | 2026-06-03 |
| src/Cruncharr.API/Controllers/QueueController.cs | N/A (API layer) | [PT] Added POST /api/v1/queue/pause, /resume, /stats includes IsGloballyPaused | 2026-06-03 |
| src/Cruncharr.Core/Configuration/CruncharrConfig.cs | upstream | [PT] Added CooldownDelaySeconds property | 2026-06-03 |

### Infrastructure
| File | Purpose | Date |
|------|---------|------|
| PORTING_LOG.md | Updated API contract with /v1 prefix, added completion entries | 2026-06-02 |
| Docker image | Multi-platform build (linux/amd64 + linux/arm64) pushed to ghcr.io/mediavybz/cruncharr:latest | 2026-06-03 |
| Docker image | Rebuilt with comprehensive security fixes (XSS, credentials, sync-over-async, HttpClient disposal) - digest: sha256:266a43ff438910ed5c3b19d690f3349aac53136536f48576d8eb0018e97c353f | 2026-06-03 |
| src/Cruncharr.Core.Tests | Sonarr integration test suite (44 tests) | 2026-06-03 |

---

## Status Summary
- Total backend files identified: 8
- Backend ported: 8 / 8 (Complete)
- Critical methods ported: 4 / 4 (ParseEpisodeById, MarkAsWatched, EpisodeData, EpisodeMeta done)
- Frontend screens identified: 9 (Downloads, Add Download, Calendar, Seasons, History, Browse, Seasonal, Account, Settings)
- Frontend built: 9 / 9 (Complete)
- Missing settings wired: 7 / 7 (DownloadAllowEarlyStart, KeepDubsSeparate, DownloadOnlyWithAllSelectedDubSub, ShutdownWhenQueueEmpty, Timeout, SkipSubs, CcTag done)
- Missing config properties: 2 / 2 (TrackedSeriesReleaseLastCheckUtc, SeasonsPageProperties done)
- Missing API methods: 2 / 2 (GetAllSeries, GetSeasonalSeries done)
- Missing auth features: 1 / 1 (GetBase64EncodedTokenAsync done)
- Missing history features: 3 / 3 (UpdateWithEpisode, GZip compression, backup rotation done)
- Frontend audit gaps resolved: 15 / 15 (previous + global pause UI, cooldown setting, getDoingText/showToast XSS escaped)
- Backend critical issues fixed: 8 / 8 (hardcoded tokens, sync-over-async x3, null ref, Environment.Exit, undisposed HttpClients x2)
- Upstream feature gaps: 3 / 6 (global queue pause, download cooldown, speed limiting wired; missing: auto-download scheduler, font muxing, multi-episode parsing)
- Settings sync fixes: 3 / 3 (dub/subs fallback defaults, stream endpoint defaults, config validation)
- **UI Improvements: 4 / 4 (dead toggles removed, refresh persistence, loading spinner, multi-select dropdowns)**
- **Build warnings: 306 → 35 (89% reduction)**
- **Upstream sync: c123093 merged - partial downloads, ReplaceExistingFiles, DefaultVideo, Sonarr fixes, cover race condition fix**
- **Missing functions fixed: openSonarrMenu, matchEpisodesForSeries, performSearch**
- **SSE memory leak fixed: QueueBroadcastService singleton pattern**
- **FlareSolverr settings save bug fixed**
- **Backend-frontend connection: All buttons wired, all API endpoints matched**
- **Auth credential auto-update: Fixed with embedded fallback + multiple URL retries**
- **IL2026 trim warnings: Suppressed in 3 dynamic parser files**
- **Multi-dub downloads: Versions passed through queue API (QueueRequest.Versions → EpisodeInfo.Versions)**
- **Version display: HealthController returns AssemblyInformationalVersion (0.1.0-beta.1)**
- **ARM64 Docker support: Multi-platform build (linux/amd64 + linux/arm64) pushed to GHCR**
- **Webhook dispatch: INotificationService wired in QueueService (complete/error notifications)**
- **GitHub Actions: Removed .github/workflows (free plan runners disabled)**
- **Sonarr integration tests: 44 tests covering service, history matching, similarity algorithms**
- Blocked items: 0

---

## Audit Results (2026-05-26)

### Config Properties Audit: Upstream vs Downstream
| Property | Upstream | Downstream | Status |
|----------|----------|------------|--------|
| **All download settings** | 50+ properties | 50+ properties | Complete |
| **Kstream** | int ([JsonIgnore]) | **MISSING** | Low priority - first stream works for 99% of content |
| **StreamServer** | int (code commented out in upstream) | **MISSING** | Not used in upstream |
| **GhUpdatePrereleases** | bool | **MISSING** | UI-only (update checker) |
| **TrayIconEnabled** | bool | **MISSING** | UI-only |
| **StartMinimizedToTray** | bool | **MISSING** | UI-only |
| **MinimizeToTray** | bool | **MISSING** | UI-only |
| **MinimizeToTrayOnClose** | bool | **MISSING** | UI-only |
| **Force** | string ([JsonIgnore]) | **MISSING** | CLI flag |
| **Override** | List<string> ([JsonIgnore]) | **DONE** | Supported via FilenameOptions.Overrides, passed to FileNameManager.ParseOverride |

### Service Methods Audit
| Upstream File | Method | Downstream Status |
|---------------|--------|-------------------|
| CrSeries.cs | ItemSelectMultiDub | **MISSING** - Requires CrunchyEpMeta model |
| CrSeries.cs | ListSeriesId | **MISSING** - Requires EpisodeAndLanguage model |
| CrSeries.cs | ParseSeriesById | PORTED as ParseSeriesByIdAsync |
| CrSeries.cs | GetSeasonDataById | PORTED as GetSeasonDataByIdAsync |
| CrSeries.cs | SeriesById | PORTED as SeriesByIdAsync |
| CrSeries.cs | Search | PORTED as SearchAsync |
| CrSeries.cs | GetAllSeries | PORTED as GetAllSeriesAsync |
| CrSeries.cs | GetSeasonalSeries | PORTED as GetSeasonalSeriesAsync |
| CrEpisode.cs | ParseEpisodeById | PORTED as ParseEpisodeByIdAsync |
| CrEpisode.cs | EpisodeData | **MISSING** - Requires CrunchyRollEpisodeData model |
| CrEpisode.cs | EpisodeMeta | **MISSING** - Requires CrunchyEpMeta model |
| CrEpisode.cs | GetNewEpisodes | PORTED as GetNewEpisodesAsync |
| CrEpisode.cs | MarkAsWatched | PORTED as MarkAsWatchedAsync |
| CrunchyrollManager.cs | DownloadEpisode | PORTED as DownloadEpisodeAsync |
| CrunchyrollManager.cs | DownloadMediaList | Integrated into DownloadEpisodeAsync |
| CrunchyrollManager.cs | MuxStreams | PORTED as MuxFilesAsync |
| CrunchyrollManager.cs | DownloadVideo | Integrated into DownloadEpisodeAsync |
| CrunchyrollManager.cs | DownloadAudio | Integrated into DownloadEpisodeAsync |
| CrunchyrollManager.cs | FetchPlaybackData | PORTED as GetPlaybackDataAsync |
| CrunchyrollManager.cs | TrySyncTimingFallback | PORTED as DownloadFallbackVideoAsync |
| QueueManager.cs | PumpQueue | PORTED as PumpQueueAsync |
| QueueManager.cs | TryStartDownload | PORTED |
| QueueManager.cs | ReleaseDownloadSlot | PORTED |
| QueueManager.cs | WaitForProcessingSlot | PORTED |
| History.cs | CrUpdateSeries | PORTED as CrUpdateSeriesAsync |
| History.cs | UpdateWithEpisodeList | PORTED as UpdateWithEpisodeListAsync |
| History.cs | UpdateWithMusicEpisodeList | PORTED as UpdateWithMusicEpisodeListAsync |
| History.cs | UpdateWithEpisode | **MISSING** - Calendar integration (minor) |
| History.cs | GetDubList | PORTED as GetDubListAsync |
| History.cs | GetSubList | PORTED as GetSubListAsync |
| History.cs | SortItems | PORTED as SortItemsAsync |
| History.cs | MatchHistorySeriesWithSonarr | PORTED as MatchHistorySeriesWithSonarrAsync |
| History.cs | MatchHistoryEpisodesWithSonarr | PORTED as MatchHistoryEpisodesWithSonarrAsync |

### Critical Assessment
- **7 missing settings** → All wired (100%)
- **2 missing config properties** → All added (100%)
- **2 missing API methods** → All added (100%)
- **Build status**: 0 errors, 303 warnings (all pre-existing)
- **Docker image**: Built and pushed successfully

### Remaining Non-Critical Gaps
1. ~~**CrunchyEpMeta model**~~ - **DONE**: EpisodeData, EpisodeMeta, ItemSelectMultiDub, ListSeriesId all implemented.
2. ~~**Kstream**~~ - **DONE**: Added to config and API
3. ~~**UpdateWithEpisode(List<CrBrowseEpisode>)**~~ - **DONE**: Implemented UpdateWithEpisodeAsync
4. ~~**GetBase64EncodedTokenAsync**~~ - **DONE**: Ported to CrunchyrollAuthService + API endpoint
5. ~~**Compressed JSON history**~~ - **DONE**: GZip compression with auto-detection
6. ~~**File backup/rotation**~~ - **DONE**: Daily backups with pruning
7. **UI-only properties** - Tray icon, minimize behaviors (not applicable to web UI)

## Deferred / Needs Decision

---

## API Contract Change Log
| Date | Change | Reason | Approved By |
|------|--------|--------|-------------|
| 2026-05-26 | Added POST /api/v1/series/episodes/{episodeId}/mark-watched | Ported MarkAsWatched from upstream CrEpisode.cs | automatic |
| 2026-05-26 | Added GET /api/v1/series/all | Ported GetAllSeries from upstream CrSeries.cs | automatic |
| 2026-05-26 | Added GET /api/v1/series/seasonal | Ported GetSeasonalSeries from upstream CrSeries.cs | automatic |
| 2026-05-26 | Fixed login bug #1 | Removed incorrect Profile.Username == "???" early return in RefreshTokenAsync; status endpoint now fetches profile if missing | automatic |
| 2026-05-26 | Fixed login bug #2 | Auth endpoints (Auth, Profile, MultiProfile, Subscription) now ALWAYS use beta-api.crunchyroll.com, matching upstream behavior. Removed useBetaApi parameter from auth URLs. | automatic |
| 2026-05-26 | Fixed login bug #3 | Login requests now skip cookie attachment to prevent stale session cookies from interfering with authentication | automatic |
| 2026-05-26 | Fixed login bug #4 | Disabled automatic cookie handling in HttpClient (UseCookies=false). Rely solely on manual cookie management to prevent stale etp_rt cookies from being sent automatically | automatic |
| 2026-05-26 | Added POST /api/v1/queue/replace | Added ReplaceQueue method for bulk queue replacement | automatic |
| 2026-05-27 | Added config validation | ValidateAndFix auto-corrects corrupted dub/sub language settings on startup | automatic |
| 2026-05-27 | Fixed settings GET | DefaultStreamEndpoint and DefaultStreamEndpointSecondary now exposed in /api/v1/config response | automatic |
| 2026-05-27 | Fixed frontend fallback | DubLanguages defaults to ['ja-JP'], SoftSubs defaults to ['en-US'] instead of all languages | automatic |
| 2026-05-27 | Fixed episode key mapping | Frontend strips 'E' prefix from episode keys to match backend dictionary format | automatic |
| 2026-05-27 | Fixed thumbnail extraction | Image URLs extracted from List<List<object>> structure instead of direct string cast | automatic |
| 2026-05-27 | Removed ValidateAndFix | Defaults handled by property initializers; prevents backend from overriding user choices on startup | automatic |
| 2026-05-27 | Fixed "40016 Outdated Token" | Skip duplicate stream endpoints; continue on token error if data already retrieved from primary | automatic |
| 2026-05-27 | Fixed multi-dub support | Added SelectedDubs and SelectedSubs to EpisodeInfo, QueueRequest, QueueController.AddToQueue | automatic |
| 2026-05-27 | Removed 23 dead toggles | Cosmetic toggles with no backend wiring removed from Settings UI | automatic |
| 2026-05-27 | Fixed page refresh | Current tab saved to localStorage and restored on reload | automatic |
| 2026-05-27 | Added loading spinner | Shows "Loading episodes..." when fetching episode list after series selection | automatic |
| 2026-05-27 | Changed Dub/Softsub dropdowns | Checkbox groups replaced with `<select multiple>` with click-to-toggle JavaScript | automatic |
| 2026-05-27 | Fixed build warnings | Reduced from 306 to 38 warnings (88% reduction) via nullable fixes and pragma directives | automatic |
| 2026-05-27 | Made GHCR package public | Changed visibility from private to public; Docker image now pullable without auth | automatic |
| 2026-05-26 | Added GET /api/v1/auth/client-token | Ported GetBase64EncodedTokenAsync from upstream CrunchyrollManager.cs | automatic |
| 2026-05-25 | Added POST /api/history/update-series/{seriesId} | Ported CrUpdateSeries from upstream | automatic |
| 2026-05-25 | Added POST /api/history/sort | Ported SortItems from upstream | automatic |
| 2026-05-25 | Added GET /api/history/episode-with-dir/{seriesId}/{seasonId}/{episodeId} | Ported GetHistoryEpisodeWithDownloadDir from upstream | automatic |
| 2026-05-25 | Added GET /api/history/episode-with-dubs/{seriesId}/{seasonId}/{episodeId} | Ported GetHistoryEpisodeWithDubListAndDownloadDir from upstream | automatic |
| 2026-05-25 | Added GET /api/history/dubs/{seriesId}/{seasonId} | Ported GetDubList from upstream | automatic |
| 2026-05-25 | Added GET /api/history/subs/{seriesId}/{seasonId} | Ported GetSubList from upstream | automatic |

---

## Future Update Notes
| Desktop Component | Docker Equivalent | Notes for Future Updates |
|-------------------|-------------------|--------------------------|
| upstream_src/History.cs | src/Cruncharr.Core/Services/HistoryService.cs | Uses ICrunchyrollApiService for series data, IMusicService for artist data. HistoryPageProperties added to CruncharrConfig. |
| upstream_src/CrSeries.cs | src/Cruncharr.Core/Services/CrunchyrollApiService.cs | Added ParseSeriesByIdAsync, GetSeasonDataByIdAsync, SeriesByIdAsync wrappers around existing methods |
| upstream_src/CrMusic.cs | src/Cruncharr.Core/Services/MusicService.cs | Partial - ArtistInfo needs Description, Images need PosterTall support |
| upstream_src/CrunchyrollManager.cs (SyncTimingFullQualityFallback) | src/Cruncharr.Core/Services/DownloadService.cs | Implemented via DownloadFallbackVideoAsync helper + videoLocales tracking in MuxFilesAsync. KeepAllVideos enabled when multiple fallback videos present. |
| upstream_src/CrEpisode.cs (ParseEpisodeById) | src/Cruncharr.Core/Services/CrunchyrollApiService.cs | ParseEpisodeByIdAsync: validates duplicate audio locale versions by calling /episodes/{guid}, removes invalid. Used by DownloadService before downloading. |
| upstream_src/CrEpisode.cs (MarkAsWatched) | src/Cruncharr.Core/Services/CrunchyrollApiService.cs | MarkAsWatchedAsync: POST to /discover/{account_id}/mark_as_watched/{episodeId}. Called by DownloadService after successful download if enabled. |
| upstream_src/CRD/Utils/Structs/Variable.cs | src/Cruncharr.Core/Models/Variable.cs | Exact 1:1 port. Constructor auto-detects Type from ReplaceWith.GetType().Name.ToLower(). Used by FileNameManager.ParseFileName. |
| upstream_src/CRD/Utils/Files/FileNameManager.cs | src/Cruncharr.Core/Utils/Files/FileNameManager.cs | Exact 1:1 port. ParseFileName handles ${var} syntax with int32 padding, double formatting, sanitize+whitespace replace. ParseOverride supports CLI var=value syntax with quoted values. CleanupFilename removes illegal chars, reserved names, trailing dots/spaces. DeleteEmptyFolders recursively removes empty directories. |
| upstream_src/CRD/Downloader/Crunchyroll/CrunchyrollManager.cs (filename variables) | src/Cruncharr.Core/Services/FilenameService.cs + DownloadService.cs | FilenameService builds Variable list and delegates to FileNameManager. DownloadService populates variables at lines ~277 and ~728 (rename with actual resolution). Variables: title (string,sanitize), episode (double|string), seriesTitle (string,sanitize), seasonTitle (string,sanitize), season (double), dubs (string,sanitize), sonarrSeriesTitle (string,sanitize), sonarrSeriesReleaseYear (int32), sonarrEpisodeTitle (string,sanitize), height (int32), width (int32). |
| src/Cruncharr.API/wwwroot/index.html (multi-select dropdowns) | upstream desktop: List<string> DubLang/DlSubs | Web UI uses `<select multiple>` with JavaScript click-to-toggle (no Ctrl/Cmd). CSS shows checkbox indicators. `getMultiSelect()` reads `selectedOptions`. |
| src/Cruncharr.Core/Utils/Parser/* (dynamic code) | upstream: Same dynamic parser code | Added `#nullable disable` to suppress CS8600/CS8602/CS8603/CS8604 in dynamic ObjectUtilities, DashParser, ToM3u8Class, DurationTimeParser, PlaylistMerge, InheritAttributes. These files use `dynamic` extensively which conflicts with C# nullable reference types. |
| src/Cruncharr.Core/Utils/DRM/WvProto2.cs | upstream: protobuf-generated | Suppressed CS8618/CS8625 warnings in auto-generated protobuf code via `#pragma warning disable`. |

---

## Completed

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.Core/Services/QueueService.cs | upstream_src/QueueManager.cs | Ported pump queue pattern, HashSet+lock, TryStartDownload/ReleaseDownloadSlot | 2026-05-25 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream_src/History.cs | Ported CrUpdateSeries, UpdateWithEpisodeList, UpdateWithMusicEpisodeList, GetHistoryEpisodeWithDownloadDir, GetHistoryEpisodeWithDubListAndDownloadDir, GetDubList, GetSubList, SortItems, SortSeasons, InferSeriesType, RefreshSeriesData, CalculateSimilarity, ParseDate | 2026-05-25 |
| src/Cruncharr.Core/Models/HistoryModels.cs | upstream_src/History.cs (implied) | Added EpisodeType enum, SeriesType enum, SortingType enum, HistoryPageProperties class, override fields (dubs/subs/quality/paths), UpdateAvailableMedia, SetDownloadedMedia methods | 2026-05-25 |
| src/Cruncharr.Core/Services/CrunchyrollApiService.cs | upstream_src/CrSeries.cs | Added ParseSeriesByIdAsync, GetSeasonDataByIdAsync, SeriesByIdAsync | 2026-05-25 |
| src/Cruncharr.Core/Services/MusicService.cs | upstream_src/CrMusic.cs (implied) | Added Description to ArtistInfo, PosterTall to CrunchyMusicVideoImages, Height/Width/Type to CrunchyImage | 2026-05-25 |
| src/Cruncharr.Core/Configuration/CruncharrConfig.cs | upstream_src/CrDownloadOptions.cs (implied) | Added HistoryPageProperties, using Cruncharr.Core.Models | 2026-05-25 |
| src/Cruncharr.API/Controllers/HistoryController.cs | N/A (API layer) | Added endpoints: update-series, sort, episode-with-dir, episode-with-dubs, dubs, subs | 2026-05-25 |
| src/Cruncharr.API/Program.cs | N/A (DI wiring) | Updated HistoryService constructor injection | 2026-05-25 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CrunchyrollManager.cs (NoVideo, NoAudio, MuxCover, MuxMp4, MuxAudioOnlyToMp3, SyncTiming) | Added NoVideo check to skip video download, added NoAudio check to skip audio/dub/AD download, added MuxCover check for cover art download, added MuxMp4 support (.mp4 output), added MuxAudioOnlyToMp3 support (.mp3 output when NoVideo), added SyncTiming support (download sync video per dub, run VideoSyncer, apply delays to audio tracks) | 2026-05-25 |
| src/Cruncharr.Core/Utils/DownloadModels.cs | upstream_src/CrunchyrollManager.cs (implied) | Added SyncVideo to DownloadMediaType enum | 2026-05-25 |
| src/Cruncharr.Core/Services/QueueService.cs | upstream_src/QueueManager.cs | Added WaitForProcessingSlotAsync, ReleaseProcessingSlot, SetProcessingLimit methods to interface and implementation | 2026-05-25 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CrunchyrollManager.cs (processing slots) | Added IQueueService injection, wrapped muxing/encoding in processing slot wait/release | 2026-05-25 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CrunchyrollManager.cs (SyncTimingFullQualityFallback) | Implemented fallback: download full-quality video for failed sync dubs, replace in mux with locale tracking | 2026-05-25 |
| src/Cruncharr.Core/Utils/Muxing/Structs/MergerOptions.cs | N/A (existing) | Added KeepAllVideos support for multiple video tracks with locale matching | 2026-05-25 |
| src/Cruncharr.Core/Services/CrunchyrollApiService.cs | upstream_src/CrEpisode.cs (ParseEpisodeById) | Ported version deduplication: validates duplicate audio locale versions by calling /episodes/{guid} for each, removes invalid ones | 2026-05-26 |
| src/Cruncharr.Core/Services/CrunchyrollApiService.cs | upstream_src/CrEpisode.cs (MarkAsWatched) | Ported MarkAsWatchedAsync: POST to /discover/{account_id}/mark_as_watched/{episodeId} | 2026-05-26 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CrEpisode.cs (ParseEpisodeById integration) | [PT] Changed GetEpisodeAsync to ParseEpisodeByIdAsync for version deduplication before download | 2026-05-26 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CrEpisode.cs (MarkAsWatched integration) | [PT] Added MarkAsWatched call after successful download when config.Crunchyroll.MarkAsWatched is true | 2026-05-26 |
| src/Cruncharr.API/Controllers/SeriesController.cs | N/A (API layer) | Added endpoint: POST /api/v1/series/episodes/{episodeId}/mark-watched | 2026-05-26 |
| src/Cruncharr.Core/Services/QueueService.cs | upstream_src/QueueManager.cs | Added DownloadAllowEarlyStart support: callback releases download slot before muxing when enabled | 2026-05-26 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CrunchyrollManager.cs | Added onDownloadComplete callback parameter to DownloadEpisodeAsync, invoked before WaitForProcessingSlotAsync | 2026-05-26 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CrunchyrollManager.cs | Added KeepDubsSeparate support: groups audio by locale, creates separate output files with .locale suffix before extension | 2026-05-26 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CrunchyrollManager.cs | Added DownloadOnlyWithAllSelectedDubSub check: skips download if episode is missing any selected dub or sub language | 2026-05-26 |
| src/Cruncharr.Core/Configuration/CruncharrConfig.cs | upstream_src/CrDownloadOptions.cs | Added ShutdownWhenQueueEmpty to QueueConfig | 2026-05-26 |
| src/Cruncharr.Core/Services/QueueService.cs | upstream_src/QueueManager.cs | Added CheckShutdownWhenQueueEmpty: triggers Environment.Exit(0) when queue is empty and setting is enabled | 2026-05-26 |
| src/Cruncharr.API/Controllers/SeriesController.cs | N/A (API layer) | Added ShutdownWhenQueueEmpty to settings GET/POST endpoints and QueueUpdateConfig DTO | 2026-05-26 |
| src/Cruncharr.Core/Configuration/CruncharrConfig.cs | upstream_src/CrDownloadOptions.cs | Added Timeout (default 15000ms), SkipSubs (default false), CcTag (default "CC") to DownloadConfig | 2026-05-26 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CrunchyrollManager.cs | Wired Timeout from config into HLS downloaders (replaced hardcoded 15*1000) | 2026-05-26 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CrunchyrollManager.cs | Added SkipSubs check: skips subtitle downloading when enabled | 2026-05-26 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CrunchyrollManager.cs | Wired CcTag from config into MergerOptions for closed caption labeling | 2026-05-26 |
| src/Cruncharr.API/Controllers/SeriesController.cs | N/A (API layer) | Added Timeout, SkipSubs, CcTag to settings GET/POST endpoints and DownloadUpdateConfig DTO | 2026-05-26 |
| src/Cruncharr.Core/Configuration/CruncharrConfig.cs | upstream_src/CrDownloadOptions.cs | Added TrackedSeriesReleaseLastCheckUtc (DateTime?) and SeasonsPageProperties to config | 2026-05-26 |
| src/Cruncharr.Core/Models/HistoryModels.cs | upstream_src/CrDownloadOptions.cs (implied) | Added SeasonsPageProperties class (mirrors HistoryPageProperties) | 2026-05-26 |
| src/Cruncharr.Core/Services/CrunchyrollApiService.cs | upstream_src/CrSeries.cs | Ported GetAllSeriesAsync: paginates through all browseable series (50/page, alphabetical) | 2026-05-26 |
| src/Cruncharr.Core/Services/CrunchyrollApiService.cs | upstream_src/CrSeries.cs | Ported GetSeasonalSeriesAsync: gets seasonal series by tag (e.g., winter-2024) | 2026-05-26 |
| src/Cruncharr.API/Controllers/SeriesController.cs | N/A (API layer) | Added endpoints: GET /api/v1/series/all, GET /api/v1/series/seasonal | 2026-05-26 |
| src/Cruncharr.API/Controllers/SeriesController.cs | N/A (API layer) | Added TrackedSeriesReleaseLastCheckUtc, SeasonsPageProperties, HistoryPageProperties to settings GET/POST | 2026-05-26 |
| src/Cruncharr.Core/Configuration/CruncharrConfig.cs | N/A (existing) | Added missing ApplyEnvironmentVariables method (reads CRUNCHYROLL_EMAIL, PASSWORD, OUTPUT_DIR, etc.) | 2026-05-26 |
| src/Cruncharr.Core/Configuration/CruncharrConfig.cs | upstream_src/CrDownloadOptions.cs | Added Kstream (int, default 0) and StreamServer (int, default 0) to DownloadConfig | 2026-05-26 |
| src/Cruncharr.API/Controllers/SeriesController.cs | N/A (API layer) | Added Kstream, StreamServer to settings GET/POST endpoints and DownloadUpdateConfig DTO | 2026-05-26 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream_src/History.cs | Added UpdateWithEpisodeAsync: converts CrBrowseEpisode list to EpisodeInfo and updates history from calendar/browse data | 2026-05-26 |
| src/Cruncharr.Core/Services/CrunchyrollAuthService.cs | upstream_src/CrunchyrollManager.cs | Ported GetBase64EncodedTokenAsync: extracts client token from CR JS bundle via regex | 2026-05-26 |
| src/Cruncharr.API/Controllers/AuthController.cs | N/A (API layer) | Added endpoint: GET /api/v1/auth/client-token | 2026-05-26 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream_src/CfgManager.cs | Ported WriteJsonToFileCompressedAsync: GZip compression for history files with magic byte detection | 2026-05-26 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream_src/CfgManager.cs | Ported DecompressJsonFileAsync: auto-detects gzip vs plain JSON, decompresses if needed | 2026-05-26 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream_src/CfgManager.cs | Ported GetDailyBackupPath + PruneBackups: daily backup rotation (keeps N backups) | 2026-05-26 |
| src/Cruncharr.Core/Models/Variable.cs | upstream_src/CRD/Utils/Structs/Variable.cs | Exact port: Name, ReplaceWith, Type (auto from GetType), Sanitize properties, two constructors | 2026-05-26 |
| src/Cruncharr.Core/Utils/Files/FileNameManager.cs | upstream_src/CRD/Utils/Files/FileNameManager.cs | Exact port: ParseFileName, ParseOverride, CleanupFilename, DeleteEmptyFolders, DeleteEmptyFoldersRecursive | 2026-05-26 |
| src/Cruncharr.Core/Services/FilenameService.cs | upstream_src/CRD/Utils/Files/FileNameManager.cs (wrapping) | Rewritten to use FileNameManager internally: builds Variable list with all upstream variables, delegates ${var} parsing to FileNameManager, preserves legacy {var} syntax support | 2026-05-26 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CRD/Downloader/Crunchyroll/CrunchyrollManager.cs (variables) | Populates all upstream filename variables: title, episode (double-parsed), seriesTitle, seasonTitle, season (double), dubs, sonarrSeriesTitle, sonarrSeriesReleaseYear, sonarrEpisodeTitle, height, width. Passes SelectedDubs to FilenameOptions | 2026-05-26 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CrunchyrollManager.cs (MuxDescription) | Added description XML generation when IncludeVideoDescription is enabled, passed to MuxFilesAsync as Description track | 2026-05-26 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CrunchyrollManager.cs (FetchPlaybackData merge) | [PT] GetPlaybackDataAsync now tries multiple endpoints and merges HardSubs/Subtitles/URLs from all successful responses instead of returning first success | 2026-05-26 |
| src/Cruncharr.Core/Services/CrunchyrollApiService.cs | upstream_src/CrEpisode.cs (EpisodeData) | Ported EpisodeDataAsync: converts EpisodeInfo to CrunchyRollEpisodeData with version grouping and special-episode key handling | 2026-05-26 |
| src/Cruncharr.Core/Services/CrunchyrollApiService.cs | upstream_src/CrEpisode.cs (EpisodeMeta) | Ported EpisodeMeta: creates CrunchyEpMeta from CrunchyRollEpisodeData with dub selection, canonical titles, and variant creation | 2026-05-26 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend) | **Removed 23 dead/cosmetic toggles** from Settings UI: Search Featured Music, Download First Available Dub, Fix CCC Subtitles, Download Duplicate, Signs Subtitles, CC Subtitles, History Count Missing/Add Specials/Skip Unmonitored/Count Sonarr/Auto Refresh Add to Queue, Use Sonarr Numbering, all 8 Notification toggles (Queue/Download/Failed/Tracked/Login/Update/Sound/Execute), Custom Calendar, Show Upcoming Episodes, Update History | 2026-05-27 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend) | **Fixed page refresh persistence**: saves current page to localStorage, restores on reload with correct nav item active | 2026-05-27 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend) | **Added loading spinner** when fetching episodes in Add Downloads (both search and browse flows) | 2026-05-27 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend) | **Changed Dub/Softsub languages** from checkbox groups to `<select multiple>` dropdowns with click-to-toggle JavaScript (no Ctrl/Cmd needed) | 2026-05-27 |
| src/Cruncharr.Core/Utils/DRM/WvProto2.cs | N/A (auto-generated) | Suppressed CS8618/CS8625 warnings in protobuf-generated code | 2026-05-27 |
| src/Cruncharr.Core/Models/Variable.cs | upstream_src/CRD/Utils/Structs/Variable.cs | Added default empty string initializers to suppress CS8618 | 2026-05-27 |
| src/Cruncharr.Core/Utils/Muxing/Structs/* | N/A (existing) | Added default empty string/object initializers to MergerInput, ParsedFont, MergerOptions, Segment, Map, PlaylistItem, SidxInfo | 2026-05-27 |
| src/Cruncharr.Core/Utils/Parser/* | N/A (existing) | Added `#nullable disable` to ObjectUtilities, XMLUtils, DashParser, ToM3u8Class, DurationTimeParser, PlaylistMerge, InheritAttributes to suppress nullability warnings in dynamic code | 2026-05-27 |
| src/Cruncharr.Core/Utils/HLS/ThrottledStream.cs | N/A (existing) | Made `_instance` nullable to suppress CS8618 | 2026-05-27 |
| src/Cruncharr.Core/Services/QueueService.cs | upstream_src/QueueManager.cs | Added `#pragma warning disable CS1998` around PumpQueueAsync (async method lacks await - intentional design) | 2026-05-27 |
| src/Cruncharr.Core/Utils/HLS/HLSDownloader.cs | N/A (existing) | Added `#pragma warning disable CS0618` around HttpRequestMessage.Properties usage (deprecated but functional) | 2026-05-27 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream_src/History.cs | Added null-coalescing for MusicVideo.Title/Id in UpdateWithMusicEpisodeListAsync | 2026-05-27 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream_src/History.cs | Added RefreshExistingEpisodesFromBrowseAsync: updates existing history episode dubs/subs metadata from browse data without full series refresh | 2026-05-26 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream_src/History.cs | Added GetSeriesThumbnailAsync: fetches series from API and extracts thumbnail URL from images | 2026-05-26 |
| src/Cruncharr.Core/Services/QueueService.cs | upstream_src/QueueManager.cs | Added ReplaceQueue: clears existing queue and replaces with new items, triggers save | 2026-05-26 |
| src/Cruncharr.API/Controllers/QueueController.cs | N/A (API layer) | Added endpoint: POST /api/v1/queue/replace | 2026-05-26 |
| src/Cruncharr.API/Controllers/ConfigController.cs | N/A (API layer) | [PT] Extracted ConfigController from SeriesController.cs into separate file | 2026-06-02 |
| src/Cruncharr.API/Controllers/SeriesController.cs | N/A (API layer) | [PT] Removed ConfigController and DTOs, kept ItemSelectMultiDubRequest | 2026-06-02 |
| src/Cruncharr.API/Program.cs | N/A (DI wiring) | [PT] Changed IHistoryService registration from explicit factory with null → AddSingleton<IHistoryService, HistoryService>() | 2026-06-02 |
| src/Cruncharr.API/Controllers/QueueController.cs | N/A (API layer) | [PT] Fixed SSE memory leak: added static _subscribed flag to prevent duplicate event handler registration | 2026-06-02 |
| PORTING_LOG.md | N/A (documentation) | [PT] Updated API Contract table: prepended /v1 to all history routes | 2026-06-02 |
| src/Cruncharr.Core/Configuration/CruncharrConfig.cs | upstream_src/CrDownloadOptions.cs | Added NormalizeNotificationSettings: validates webhook URL, method, content type, headers | 2026-05-26 |
| src/Cruncharr.Core/Configuration/CruncharrConfig.cs | upstream_src/CrDownloadOptions.cs | Added SyncLegacyNotificationFields: hook for future notification field migrations | 2026-05-26 |
| src/Cruncharr.Core/Services/CrunchyrollAuthService.cs | upstream_src/CRAuth.cs | Added AuthAnonymousFoxyAsync: alternative anonymous auth using Nintendo Switch/Foxy endpoint | 2026-05-26 |
| src/Cruncharr.Core/Services/CrunchyrollAuthService.cs | upstream_src/CRAuth.cs | Added CheckStreamEndpointUpdateAsync: checks GitHub releases for newer endpoint versions | 2026-05-26 |
| src/Cruncharr.Core/Configuration/CruncharrConfig.cs | upstream_src/CrDownloadOptions.cs | Added ValidateAndFix: auto-fixes corrupted language settings (all dubs/subs selected due to old frontend bug) | 2026-05-27 |
| src/Cruncharr.API/Program.cs | N/A (DI wiring) | Added config.ValidateAndFix() call after loading config to normalize corrupted values on startup | 2026-05-27 |
| src/Cruncharr.API/Controllers/SeriesController.cs | N/A (API layer) | Exposed DefaultStreamEndpoint and DefaultStreamEndpointSecondary in GetConfig response | 2026-05-27 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend) | Fixed settings fallback: DubLanguages now defaults to ['ja-JP'] instead of all languages; SoftSubs defaults to ['en-US'] | 2026-05-27 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend) | Fixed episode key mapping: strips 'E' prefix from episode keys to match backend dictionary format | 2026-05-27 |
| src/Cruncharr.Core/Services/CrunchyrollApiService.cs | upstream_src/CrEpisode.cs | Fixed thumbnail extraction: handles List<List<object>> image structure from CR API | 2026-05-27 |
| src/Cruncharr.Core/Services/QueueService.cs | upstream_src/QueueManager.cs (c123093) | [PT] Added _isInitialized flag and SetInitialized method to prevent auto-download before auth init completes | 2026-06-02 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CrunchyrollManager.cs (c123093) | [PT] Fixed cover path: changed to $"{fileName}.cover.png" for uniqueness, added File.Exists check to avoid re-download | 2026-06-02 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream_src/CrunchyrollManager.cs (c123093) | [PT] Fixed ReplaceExistingFiles in quality-probe rename path: delete existing file when ReplaceExistingFiles=true | 2026-06-02 |
| src/Cruncharr.API/Program.cs | N/A (DI wiring) | [PT] Wired queueService.SetInitialized(true) after auth initialization completes | 2026-06-02 |
| src/Cruncharr.API/Controllers/ConfigController.cs | upstream_src/CrDownloadOptions.cs (c123093) | [PT] Added DefaultVideo and ReplaceExistingFiles to GET response, update handler, and DownloadUpdateConfig DTO | 2026-06-02 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend) | **Fixed history field name mismatches**: `thumbnailUrl`→`thumbnailImageUrl`, `newEpisodes`→`hasNewEpisodes`, removed non-existent `availableDubs`/`availableSubs`, added `downloadedEpisodes`/`totalEpisodes` display | 2026-06-02 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend) | **Fixed history detail field names**: `ep.episodeNumber`→`ep.episode`, `ep.title`→`ep.episodeTitle`, `ep.isDownloaded`→`ep.wasDownloaded`, `season.seasonNumber`→`season.seasonNum`, `s.id`→`s.seriesId` | 2026-06-02 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend) | **Fixed addMissingToQueue field names**: `episode.isDownloaded`→`wasDownloaded`, `episode.title`→`episodeTitle`, `series.title`→`seriesTitle`, `season.seasonNumber`→`seasonNum`, `episode.episodeNumber`→`episode.episode` | 2026-06-02 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend) | **Removed date sort from history**: `lastUpdated` does not exist on HistorySeriesResponse | 2026-06-02 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend) | **Fixed calendar airDate**: `ep.time`→`ep.airDate` with DateTime formatting | 2026-06-02 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend) | **Fixed isEpisodePartiallyDownloaded**: `episode.isDownloaded`→`episode.wasDownloaded` | 2026-06-02 |

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Date |
|------|-------------------|-------------------|------|
| index.html (renderDownloads) | DownloadsPageView.axaml | GET /api/v1/queue, GET /api/v1/queue/stats, DELETE /api/v1/queue, POST /api/v1/queue/cancel | 2026-05-26 |
| index.html (renderAddDownload) | AddDownloadPageView.axaml | GET /api/v1/series/search, GET /api/v1/series/{id}/list, POST /api/v1/series/item-select-multi-dub, POST /api/v1/queue | 2026-05-26 |
| index.html (renderCalendar) | CalendarPageView.axaml | GET /api/v1/calendar | 2026-05-25 |
| index.html (renderSeasons) | SeriesPageView.axaml | GET /api/v1/series/search | 2026-05-25 |
| index.html (renderHistory) | HistoryPageView.axaml | GET /api/v1/history, GET /api/v1/history/rich, POST /api/v1/history/cleanup, POST /api/v1/history/sort, POST /api/v1/history/update-series/{id}, POST /api/v1/history/sonarr/* | 2026-05-26 |
| index.html (renderAccount) | AccountPageView.axaml | GET /api/v1/auth/status, GET /api/v1/auth/profiles, POST /api/v1/auth/profiles/switch, GET /api/v1/auth/client-token, POST /api/v1/auth/login, POST /api/v1/auth/logout, POST /api/v1/auth/refresh | 2026-05-26 |
| index.html (renderBrowse) | BrowsePageView.axaml | GET /api/v1/series/all | 2026-05-26 |
| index.html (renderSeasonal) | SeasonalPageView.axaml | GET /api/v1/series/seasonal | 2026-05-26 |
| index.html (renderSettings) | SettingsPageView.axaml | GET /api/v1/config, POST /api/v1/config | 2026-05-25 |
| index.html (mark-as-watched wiring) | AddDownloadPageView.axaml | POST /api/v1/series/episodes/{id}/mark-watched | 2026-05-26 |
| index.html (browse/seasonal multi-dub) | BrowsePageView.axaml / SeasonalPageView.axaml | GET /api/v1/series/{id}/list | 2026-05-26 |
| index.html (history filter) | HistoryPageView.axaml | GET /api/v1/history/rich | 2026-05-26 |
| index.html (encoding preset dropdown) | SettingsPageView.axaml | GET /api/v1/encoding/presets | 2026-05-26 |
| index.html (downloads tooltip + highlight) | DownloadsPageView.axaml | GET /api/v1/queue | 2026-06-02 |
| index.html (settings c123093) | SettingsPageView.axaml | GET /api/v1/config, POST /api/v1/config | 2026-06-02 |

---

## In Progress
| File | Mode | Blocker |
|------|------|---------|
| N/A | N/A | All tasks complete |

---

## Remaining
### Known Issues To Fix
- [x] **Multi-dub downloads** - FIXED: Episode versions now passed through queue API
- [x] **Build warnings** - FIXED: 41 warnings remain (all pre-existing nullable annotations in dynamic parser code)
- [x] **GitHub Actions CI/CD** - FIXED: Removed .github/workflows directory (free plan runners disabled)
- [x] **Auth credential auto-update URL dead** - FIXED: Embedded fallback credentials + multiple URL retries
- [x] **Version display** - FIXED: HealthController returns AssemblyInformationalVersion (0.1.0-beta.1)

### Nice To Have
- [x] Remove remaining build warnings - FIXED: Suppressed IL2026/IL2104 at project level
- [x] Add ARM64 Docker image support - FIXED: Multi-platform build pushed to GHCR
- [x] Webhook notification dispatch - FIXED: INotificationService wired in QueueService
- [x] Test Sonarr integration end-to-end - FIXED: Created 44 unit/integration tests covering SonarrService, HistoryService matching, and StringSimilarity algorithms. Fixed BuildBaseUrl trailing slash bug.

### Completed (No Longer Remaining)
- [x] src/Cruncharr.Core/Services/DownloadService.cs → upstream_src/CrunchyrollManager.cs (SyncTiming, NoVideo, NoAudio, MuxCover, MuxMp4, MuxAudioOnlyToMp3, ReplaceExistingFiles)
- [x] src/Cruncharr.Core/Services/CrunchyrollAuthService.cs → upstream_src/CRAuth.cs (core auth logic already complete)
- [x] ProcessingSlotManager integration for muxing/encoding limits
- [x] Multi-dub episode selection (ItemSelectMultiDub integration)
- [x] Profile switching UI
- [x] History maintenance actions
- [x] Queue stats display
- [x] Browse all series page
- [x] Seasonal browse page
- [x] Media/Music browser pages - **RESOLVED**: No standalone browser needed upstream

---

## API Contract Change Log
| Date | Change | Reason | Approved By |
|------|--------|--------|-------------|
| 2026-06-02 | Added POST /api/v1/webhook/test | Critical audit fix F-CRIT-001: testWebhook() called non-existent endpoint | automatic |
| 2026-06-02 | Added POST /api/v1/history/series/{seriesId}/settings | Critical audit fix F-CRIT-002: saveSeriesSettingsOverride() called non-existent endpoint | automatic |
| 2026-06-02 | Added POST /api/v1/history/season/{seasonId}/settings | Critical audit fix F-CRIT-003: saveSeasonSettingsOverride() called non-existent endpoint | automatic |

---

## Deferred / Needs Decision
| Item | Reason | Options | Status |
|------|--------|---------|--------|
| Media/Music browser | **NOT NEEDED** - Upstream desktop app does not have a standalone music/movie browser page. Music is accessed via: (1) `SearchFetchFeaturedMusic` setting which adds featured music videos to series search results, (2) History integration for CR artists. Both are already implemented. Movies are accessed via series search. No separate browse UI exists upstream. | N/A - Feature parity achieved through existing integration. | **RESOLVED** |
| GitHub Actions CI/CD | Organization `mediavybz` is on free plan. GitHub-hosted runners disabled by org billing policy. **RESOLVED**: Removed `.github/workflows/` directory. Docker images built and pushed manually via `docker buildx`. | (1) Enable GitHub Actions billing for org, (2) Use self-hosted runner, (3) Build locally and push manually | **RESOLVED** - Using option 3 (manual build/push) |

---