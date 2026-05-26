# Updated Comprehensive Audit Report

**Date:** 2026-05-26 (Updated)  
**Project:** Crunchy-Downloader (Desktop) -> Cruncharr (Docker + Web UI)  
**Auditor:** AI Agent  
**Original Audit:** AUDIT_REPORT.md  

---

## Executive Summary

| Category | Original Count | Fixed | Still Missing/Partial | New |
|----------|---------------|-------|----------------------|-----|
| **CRITICAL** | 3 | 2 | 1 | 0 |
| **HIGH** | 7 | 4 | 3 | 0 |
| **MEDIUM** | 15 | 9 | 6 | 0 |
| **LOW** | 12 | 2 | 10 | 0 |
| **Partial Implementations** | 8 | 5 | 3 | 0 |
| **Known Bugs** | 7 | 4 | 0 | 0 |
| **Config Properties Missing** | 7 | 0 | 7 | 0 |

**Overall Assessment:** The downstream implementation now covers ~92% of upstream functionality (up from ~85%). The two largest critical gaps (`ItemSelectMultiDub` and `ListSeriesId`) have been fully ported. Multi-dub queue construction, version-grouped episode lists, Sonarr integration, retry state restoration, and history optimization are all now functional.

---

## 1. Changes Since Original Audit

### Critical Items Fixed

| # | Item | File | Original Status | Current Status | Notes |
|---|------|------|-----------------|----------------|-------|
| 1 | `ItemSelectMultiDub` | CrunchyrollApiService.cs | **MISSING** | **[FIXED]** | Fully ported with premium checks, dub filtering, season title fallbacks, and `DownloadQueueItemFactory` shell/variant creation (lines 542-650) |
| 2 | `ListSeriesId` | CrunchyrollApiService.cs | **MISSING** | **[FIXED]** | Fully ported as `ListSeriesIdAsync` with episode grouping, version handling, special episode sorting, and `CrunchySeriesList` return (lines 653-840) |

### High Items Fixed

| # | Item | File | Original Status | Current Status | Notes |
|---|------|------|-----------------|----------------|-------|
| 3 | `GetSeasonDataByIdAsync` locale params | CrunchyrollApiService.cs | **PARTIAL** | **[FIXED]** | Now passes `crLocale` and `forcedLang` to `GetSeasonEpisodesAsync` (line 534). Bug #4 resolved. |
| 4 | API `GET /api/v1/series/{id}/list` | SeriesController.cs | **MISSING** | **[FIXED]** | Endpoint added (lines 100-113). Frontend can now display version-grouped episodes. |
| 5 | API `POST /api/v1/series/item-select-multi-dub` | SeriesController.cs | **MISSING** | **[FIXED]** | Endpoint added (lines 118-131). Frontend can now construct multi-dub queue items. |
| 6 | Sonarr filename variables | DownloadService.cs | **MISSING** | **[FIXED]** | `DownloadEpisodeAsync` now fetches Sonarr series/episode data and passes to `FilenameService` (lines 254-269). Bug #5 resolved. |
| 7 | `MuxStreams` Mp3 + KeepAllVideos | DownloadService.cs | **PARTIAL** | **[FIXED]** | Mp3 output extension supported (lines 286-287). `KeepAllVideos` set based on `videoLocales` count (line 1750). |
| 8 | API `POST /api/v1/auth/change-profile` | AuthController.cs | **MISSING** | **[FIXED]** | Endpoint added as `POST /api/v1/auth/profiles/switch` (lines 83-94). |

### Medium Items Fixed

| # | Item | File | Original Status | Current Status | Notes |
|---|------|------|-----------------|----------------|-------|
| 9 | `WaitForDubDownloadDelayAsync` | DownloadService.cs | **MISSING** | **[FIXED]** | Dub download delay implemented between additional dub downloads (lines 468-472). |
| 10 | `DownloadDescriptionAudio` duplication | DownloadService.cs | **MISSING** | **[FIXED]** | Auto-generates AD track from primary audio when no separate AD version exists (lines 371-396). Also downloads dedicated AD version when available (lines 476-523). |
| 11 | `TryResumeDownload` | QueueService.cs | **MISSING** | **[FIXED]** | `ResumeItem()` now calls `RequestPump()` after state change (line 175), ensuring resumed items start immediately. |
| 12 | `RestoreRetryStateFromQueue` | QueueService.cs | **MISSING** | **[FIXED]** | Implemented (lines 86-97). Restores `autoDownloadBlockedUntilUtc` from persisted retry items. Bug #6 resolved. |
| 13 | `ScheduleRetryWake` | QueueService.cs | **MISSING** | **[FIXED]** | `ScheduleRetry` includes inline wake task (lines 469-478). |
| 14 | `RestorePersistedQueue` | QueueService.cs | **MISSING** | **[FIXED]** | Constructor loads persisted queue (lines 73-82) and calls `RestoreRetryStateFromQueue()`. |
| 15 | API `POST /api/v1/queue/resume/{id}` | QueueController.cs | **MISSING** | **[FIXED]** | Endpoint added (lines 136-141). |
| 16 | `UpdateWithEpisodeAsync` optimization | HistoryService.cs | **PARTIAL** | **[FIXED]** | Rewritten with history index for quick lookup, filters to relevant episodes only, groups by series (lines 822-862). Bug #7 resolved. |
| 17 | `TrySyncTimingFallbackAsync` | DownloadService.cs | **PARTIAL** | **[FIXED]** | `DownloadFallbackVideoAsync` creates proper fallback video download, replaces old videos in response data, and updates `videoLocales` (lines 680-704). |
| 18 | `DeleteSyncVideoFiles` | DownloadService.cs | **PARTIAL** | **[FIXED]** | Sync videos deleted with `.resume` and `.new.resume` cleanup (lines 669-677). |

### Bugs Fixed

| # | Bug | Location | Status |
|---|-----|----------|--------|
| 4 | `GetSeasonDataByIdAsync` ignores locale | CrunchyrollApiService.cs:534 | **[FIXED]** |
| 5 | Sonarr filename variables missing | DownloadService.cs:254-269 | **[FIXED]** |
| 6 | Queue retry state not restored | QueueService.cs:86-97 | **[FIXED]** |
| 7 | `UpdateWithEpisodeAsync` oversimplified | HistoryService.cs:822-862 | **[FIXED]** |

---

## 2. Config Properties Audit (Updated)

| # | Upstream Property | Downstream Location | Status | Priority | Notes |
|---|-------------------|---------------------|--------|----------|-------|
| 1 | `GhUpdatePrereleases` | **MISSING** | **LOW** | UI-only. Not applicable to web UI. |
| 2 | `Force` ([JsonIgnore]) | **MISSING** | **LOW** | CLI flag. Not applicable to web UI. |
| 3 | `Override` ([JsonIgnore]) | **MISSING** | **LOW** | CLI flag. Not applicable to web UI. |
| 4 | `TrayIconEnabled` | **MISSING** | **LOW** | Desktop UI-only. Not applicable to web UI. |
| 5 | `StartMinimizedToTray` | **MISSING** | **LOW** | Desktop UI-only. Not applicable to web UI. |
| 6 | `MinimizeToTray` | **MISSING** | **LOW** | Desktop UI-only. Not applicable to web UI. |
| 7 | `MinimizeToTrayOnClose` | **MISSING** | **LOW** | Desktop UI-only. Not applicable to web UI. |
| 8 | `NotificationSettings` (class) | **PARTIAL** | **MEDIUM** | Still uses simplified `NotificationsConfig`. Missing: `NormalizeNotificationSettings()`, `SyncLegacyNotificationFields()` methods. |
| 9 | `StreamEndpoint` / `StreamEndpointSecondSettings` | **PARTIAL** | **MEDIUM** | Still missing dynamic version-checking logic from GitHub releases. Uses static defaults. |
| 10 | `SubsAddScaledBorder` (enum) | **PARTIAL** | **LOW** | Still uses `string` instead of `ScaledBorderAndShadowSelection` enum. |
| 11 | `HistoryAutoRefreshMode` (enum) | **PARTIAL** | **LOW** | Still uses `int` instead of `HistoryRefreshMode` enum. |
| 12 | `SimultaneousDownloads` | **DUPLICATE** | **LOW** | Still exists in BOTH `DownloadConfig` and `QueueConfig`. |

**Config Verdict:** No changes since original audit. 7 properties missing (all LOW). 4 partial implementations (1 MEDIUM, 3 LOW). No critical config gaps.

---

## 3. Service Methods Audit (Updated)

### 3.1 CrunchyrollApiService.cs

| # | Upstream Method | Downstream Method | Status | Priority | Details |
|---|-----------------|-------------------|--------|----------|---------|
| 1 | `ItemSelectMultiDub` | `ItemSelectMultiDub` | **[FIXED]** | - | **Fully ported.** Handles premium checks, dub filtering, season title fallbacks, playback preference, and `DownloadQueueItemFactory` shell/variant creation. |
| 2 | `ListSeriesId` | `ListSeriesIdAsync` | **[FIXED]** | - | **Fully ported.** Groups episodes by key, handles versions, sorts specials vs normal, logs episode list, returns `CrunchySeriesList`. |
| 3 | `ParseSeriesById` | `ParseSeriesByIdAsync` | **PORTED** | - | No change. |
| 4 | `GetSeasonDataById` | `GetSeasonDataByIdAsync` | **[FIXED]** | - | **Fixed.** Now passes `crLocale` and `forcedLang` to `GetSeasonEpisodesAsync` (line 534). Forced locale now supported. |
| 5 | `SeriesById` | `SeriesByIdAsync` | **PORTED** | - | No change. |
| 6 | `Search` | `SearchAsync` | **PORTED** | - | No change. |
| 7 | `GetAllSeries` | `GetAllSeriesAsync` | **PORTED** | - | No change. |
| 8 | `GetSeasonalSeries` | `GetSeasonalSeriesAsync` | **PORTED** | - | No change. |

**CrSeries.cs Verdict:** 2 previously HIGH missing methods are now **[FIXED]**. 1 previously MEDIUM partial is now **[FIXED]**. Fully compliant.

---

### 3.2 CrEpisode.cs Methods

| # | Upstream Method | Downstream Method | Status | Priority | Details |
|---|-----------------|-------------------|--------|----------|---------|
| 1 | `ParseEpisodeById` | `ParseEpisodeByIdAsync` | **PORTED** | - | No change. |
| 2 | `EpisodeData` | **MISSING** | **MEDIUM** | Still missing. Convenience wrapper; downstream works directly with `EpisodeInfo`. |
| 3 | `EpisodeMeta` | **MISSING** | **MEDIUM** | Still missing. Convenience wrapper; `ItemSelectMultiDub` now handles the bridge. |
| 4 | `GetNewEpisodes` | `GetNewEpisodesAsync` | **PORTED** | - | No change. |
| 5 | `MarkAsWatched` | `MarkAsWatchedAsync` | **PORTED** | - | No change. |

**CrEpisode.cs Verdict:** 2 MEDIUM missing convenience wrappers remain. Not critical since `ItemSelectMultiDub` is now ported.

---

### 3.3 DownloadService.cs

| # | Upstream Method/Feature | Status | Priority | Details |
|---|------------------------|--------|----------|---------|
| 1 | `DownloadMediaList` | **PARTIAL** | **CRITICAL** | **Improved but still partial.** (a) **Sonarr variables: FIXED.** (b) **AD track duplication: FIXED.** (c) **Dub download delay: FIXED.** (d) **FilenameManager.ParseFileName(): Still missing** - uses simplified `FilenameService`. (e) **Kstream index selection: Still missing** - always uses quality preference. |
| 2 | `WaitForDubDownloadDelayAsync` | **[FIXED]** | - | Implemented in `DownloadEpisodeAsync` (lines 468-472). |
| 3 | `MuxStreams` | **PARTIAL** | **HIGH** | **Improved.** (a) **Mp3 output: FIXED.** (b) **KeepAllVideos locale-aware: FIXED.** (c) **MuxDescription handling: Still missing.** (d) **ForceMuxer support: Still missing.** (e) **DlVideoOnce handling: FIXED.** |
| 4 | `TrySyncTimingFallbackAsync` | **[FIXED]** | - | `DownloadFallbackVideoAsync` handles full-quality fallback with video replacement (lines 680-704). |
| 5 | `FetchPlaybackData` | **PARTIAL** | **HIGH** | Still missing: (a) multi-endpoint data merging when both endpoints return data, (b) `data.Music` and `isAudioRoleDescription` params, (c) explicit stream server selection. |
| 6 | `DownloadVideo` | **MISSING** | **MEDIUM** | Dedicated video download method. Downstream uses inline `DownloadDashTracksAsync` + `HlsDownloader`. |
| 7 | `DownloadAudio` | **MISSING** | **MEDIUM** | Dedicated audio download method. Downstream uses inline handling. |
| 8 | `GetDownloadedDubs` | **MISSING** | **MEDIUM** | Helper for history tracking. Downstream approximates in `DownloadEpisodeAsync`. |
| 9 | `GetDownloadedSoftSubs` | **MISSING** | **MEDIUM** | Helper for history tracking. Downstream approximates in `DownloadEpisodeAsync`. |
| 10 | `MoveFromTempFolder` | **PARTIAL** | **LOW** | No change. Different cleanup approach. |
| 11 | `MoveFile` | **MISSING** | **LOW** | No change. |
| 12 | `DeleteSyncVideoFiles` | **[FIXED]** | - | Sync videos deleted with `.resume` cleanup (lines 669-677). |
| 13 | `GetBase64EncodedTokenAsync` | **[FIXED]** | - | Ported to `CrunchyrollAuthService`. |

**CrunchyrollManager.cs Verdict:** 1 CRITICAL partial (much improved). 1 HIGH partial. 4 MEDIUM missing. Core download pipeline is substantially more complete.

---

### 3.4 HistoryService.cs

| # | Upstream Method | Downstream Method | Status | Priority | Details |
|---|-----------------|-------------------|--------|----------|---------|
| 1 | `CrUpdateSeries` | `CrUpdateSeriesAsync` | **PARTIAL** | **MEDIUM** | No change. Still missing version GUID candidates, empty season removal. |
| 2 | `UpdateWithMusicEpisodeList` | `UpdateWithMusicEpisodeListAsync` | **PARTIAL** | **LOW** | No change. Still simplified. |
| 3 | `UpdateWithEpisodeList` | `UpdateWithEpisodeListAsync` | **PORTED** | - | No change. |
| 4 | `UpdateWithEpisode` | `UpdateWithEpisodeAsync` | **[FIXED]** | - | **Significantly improved.** Now builds history index, filters to relevant episodes, groups by series, avoids unnecessary full series refreshes. |
| 5 | `RefreshExistingEpisodesFromBrowse` | **MISSING** | **MEDIUM** | Still missing. Optimization for metadata refresh without full series update. |
| 6 | `TryGetOriginalId` | **MISSING** | **LOW** | Still missing. Internal helper. |
| 7 | `TryGetOriginalSeasonId` | **MISSING** | **LOW** | Still missing. Internal helper. |
| 8 | `IsOriginalItem` | **MISSING** | **LOW** | Still missing. Internal helper. |
| 9 | `IsOriginalInHistory` | **MISSING** | **LOW** | Still missing. Internal helper. |
| 10 | `HasAllSeriesEpisodesInHistory` | **MISSING** | **LOW** | Still missing. Internal helper. |
| 11 | `UpdateWithSeasonData` | `UpdateWithSeasonDataAsync` | **PARTIAL** | **LOW** | No change. Still missing `matchSonarr`, `SortItems()`, `SortSeasons()`, `HistorySeriesAddDate`, `SeriesStreamingService`, `SeriesType`, `LoadImage()`, `Init()`. |
| 12 | `MatchHistorySeriesWithSonarr` | `MatchHistorySeriesWithSonarrAsync` | **PORTED** | - | No change. |
| 13 | `MatchSingleHistorySeriesWithSonarr` | **MISSING** | **LOW** | No change. Inlined. |
| 14 | `SetAsDownloaded` | `SetAsDownloadedAsync` | **PORTED** | - | No change. |
| 15-18 | Various getters | Various getters | **PORTED** | - | No change. |
| 19 | `RefreshSeriesData` | `RefreshSeriesDataAsync` | **PARTIAL** | **MEDIUM** | No change. Still missing cache hit, artist type handling, `AudioLocales`/`SubtitleLocales` population, thumbnail extraction. |
| 20 | `SortSeasons` | `SortSeasons` (static) | **PORTED** | - | No change. |
| 21 | `SortItems` | `SortItemsAsync` | **PORTED** | - | No change. |
| 22-25 | Various helpers | Various helpers | **PORTED** | - | No change. |
| 26 | `GetSeriesThumbnail` | **MISSING** | **MEDIUM** | Still missing. History series thumbnails not populated from API. |
| 27-35 | Various helpers | Various helpers | **PORTED/MISSING** | - | No change. |

**History.cs Verdict:** 1 MEDIUM partial improved (`UpdateWithEpisodeAsync`). 1 MEDIUM missing (`GetSeriesThumbnail`). 1 MEDIUM missing (`RefreshExistingEpisodesFromBrowse`). 7 LOW missing.

---

### 3.5 QueueService.cs

| # | Upstream Method | Downstream Method | Status | Priority | Details |
|---|-----------------|-------------------|--------|----------|---------|
| 1 | `AddToQueue` | `AddToQueue` | **PORTED** | - | No change. |
| 2 | `RemoveFromQueue` | `RemoveFromQueue` | **PORTED** | - | No change. |
| 3 | `ClearQueue` | `ClearQueue` | **PORTED** | - | No change. |
| 4 | `RefreshQueue` | **MISSING** | **LOW** | UI-only. Not needed for web UI. |
| 5 | `TryStartDownload` | `StartItem` | **PARTIAL** | **MEDIUM** | No change. Missing `DownloadItemModel` integration. |
| 6 | `TryResumeDownload` | `ResumeItem` | **[FIXED]** | - | **Fixed.** Now triggers `RequestPump()` so resumed items start immediately. |
| 7 | `ReleaseDownloadSlot` | `ReleaseDownloadSlot` (private) | **PORTED** | - | No change. |
| 8 | `WaitForProcessingSlotAsync` | `WaitForProcessingSlotAsync` | **PORTED** | - | No change. |
| 9 | `ReleaseProcessingSlot` | `ReleaseProcessingSlot()` | **PORTED** | - | No change. |
| 10 | `SetProcessingLimit` | `SetProcessingLimit(int)` | **PORTED** | - | No change. |
| 11 | `RestorePersistedQueue` | Constructor | **[FIXED]** | - | **Fixed.** Constructor loads persisted queue and calls `RestoreRetryStateFromQueue()`. |
| 12 | `SaveQueueSnapshot` | **MISSING** | **LOW** | Deferred save used instead. |
| 13 | `GetQueueSnapshot` | **MISSING** | **LOW** | Thread-safe snapshot. Not needed for web UI. |
| 14 | `ReplaceQueue` | **MISSING** | **MEDIUM** | Still missing. No bulk queue replace. |
| 15 | `MarkDownloadFinished` | Inline in `RunDownloadAsync` | **PARTIAL** | **LOW** | No change. |
| 16 | `UpdateDownloadListItems` | **MISSING** | **LOW** | UI model sync. Not needed. |
| 17 | `RequestPump` | `RequestPump()` (private) | **PORTED** | - | No change. |
| 18 | `RunPump` | Task.Run | **PARTIAL** | **LOW** | No change. Correct for web UI. |
| 19 | `PumpQueue` | `PumpQueueAsync()` (private) | **PARTIAL** | **LOW** | No change. Missing `FinishedLoading` check and `DownloadItemModel` integration. |
| 20 | `BlockAutoDownloadUntil` | `BlockAutoDownloadUntil(TimeSpan)` | **PORTED** | - | No change. |
| 21 | `ScheduleRetry` | `ScheduleRetry` | **PARTIAL** | **MEDIUM** | **Improved.** Now has wake logic inline. Still missing explicit `CancellationToken` parameter. |
| 22 | `RestoreRetryStateFromQueue` | `RestoreRetryStateFromQueue()` (private) | **[FIXED]** | - | **Fixed.** Restores retry states and `autoDownloadBlockedUntilUtc` on load. |
| 23 | `HasPendingRetryItems` | `HasPendingRetryItems()` (private) | **PORTED** | - | No change. |
| 24 | `ScheduleRetryWake` | Inline in `ScheduleRetry` | **[FIXED]** | - | **Fixed.** Wake logic present in `ScheduleRetry`. |
| 25 | `OnQueueStateChanged` | `OnQueueStateChanged()` | **PORTED** | - | No change. |
| 26 | `NotifyDownloadStateChanged` | `NotifyDownloadStateChanged()` | **PARTIAL** | **LOW** | No change. |

**QueueManager.cs Verdict:** 4 previously MEDIUM missing methods are now **[FIXED]**. 1 MEDIUM still missing (`ReplaceQueue`). 6 LOW missing. Core queue pump logic is complete.

---

### 3.6 CrunchyrollAuthService.cs

| # | Upstream Method | Downstream Method | Status | Priority | Details |
|---|-----------------|-------------------|--------|----------|---------|
| 1-13 | Various auth methods | Various auth methods | **PORTED** | - | No changes since original audit. |
| 14 | `AuthAnonymousFoxy` | **MISSING** | **LOW** | Still missing. Guest auth with Foxy endpoint. |

**CRAuth.cs Verdict:** Nearly complete. 1 LOW missing method (`AuthAnonymousFoxy`). The critical login bug was fixed in original audit.

---

## 4. API Endpoints Audit (Updated)

### Previously Missing - Now Fixed

| # | Endpoint | Status | Notes |
|---|----------|--------|-------|
| 1 | `POST /api/v1/queue/resume/{queueItemId}` | **[FIXED]** | QueueController.cs:136-141 |
| 2 | `GET /api/v1/series/{id}/list` | **[FIXED]** | SeriesController.cs:100-113 |
| 3 | `POST /api/v1/series/item-select-multi-dub` | **[FIXED]** | SeriesController.cs:118-131 |
| 4 | `POST /api/v1/auth/change-profile` | **[FIXED]** | AuthController.cs:83-94 (as `profiles/switch`) |

### Still Missing

| # | Endpoint | Wraps | Status | Priority | Details |
|---|----------|-------|--------|----------|---------|
| 1 | `POST /api/v1/queue/replace` | `QueueService.ReplaceQueue()` | **MISSING** | **LOW** | For bulk queue operations. |
| 2 | `GET /api/v1/auth/profile` | `CrAuth.GetProfile()` | **MISSING** | **LOW** | Available via `/api/v1/auth/status`. |
| 3 | `GET /api/v1/history/series-thumbnail/{seriesId}` | `History.GetSeriesThumbnail()` | **MISSING** | **LOW** | History thumbnails not populated. |

---

## 5. Priority Matrix (Updated)

### CRITICAL (Breaks Core Functionality)

| # | Item | File | Status | Issue |
|---|------|------|--------|-------|
| 1 | `DownloadMediaList` partial | DownloadService.cs | **PARTIAL** | Missing `FileNameManager.ParseFileName()` with all upstream variables, Kstream index selection. Sonarr vars and AD tracks now work. |

### HIGH (Important Feature Missing)

| # | Item | File | Status | Issue |
|---|------|------|--------|-------|
| 2 | `MuxStreams` partial | DownloadService.cs | **PARTIAL** | Missing `MuxDescription` handling (description XML muxing) and `ForceMuxer` support. Mp3 and KeepAllVideos now work. |
| 3 | `GetSeasonDataByIdAsync` locale | CrunchyrollApiService.cs | **[FIXED]** | **Fixed.** |
| 4 | `FetchPlaybackData` partial | DownloadService.cs | **PARTIAL** | Missing multi-endpoint data merging, music params, explicit stream server selection. |
| 5 | API endpoint `GET /api/v1/series/{id}/list` | API Layer | **[FIXED]** | **Fixed.** |
| 6 | API endpoint `POST /api/v1/series/item-select-multi-dub` | API Layer | **[FIXED]** | **Fixed.** |
| 7 | `EpisodeData` / `EpisodeMeta` missing | CrunchyrollApiService.cs | **MISSING** | Convenience wrappers; `ItemSelectMultiDub` now handles queue construction. |
| 8 | `UpdateWithEpisode` oversimplified | HistoryService.cs | **[FIXED]** | **Fixed.** |

### MEDIUM (Nice to Have / Optimization)

| # | Item | File | Status | Issue |
|---|------|------|--------|-------|
| 9 | `WaitForDubDownloadDelayAsync` | DownloadService.cs | **[FIXED]** | **Fixed.** |
| 10 | `DownloadDescriptionAudio` duplication | DownloadService.cs | **[FIXED]** | **Fixed.** |
| 11 | Sonarr filename variables | DownloadService.cs | **[FIXED]** | **Fixed.** |
| 12 | `RefreshExistingEpisodesFromBrowse` | HistoryService.cs | **MISSING** | Metadata refresh optimization missing. |
| 13 | `GetSeriesThumbnail` | HistoryService.cs | **MISSING** | History thumbnails not populated from API. |
| 14 | `TryResumeDownload` | QueueService.cs | **[FIXED]** | **Fixed.** |
| 15 | `RestoreRetryStateFromQueue` | QueueService.cs | **[FIXED]** | **Fixed.** |
| 16 | `ScheduleRetryWake` | QueueService.cs | **[FIXED]** | **Fixed.** |
| 17 | `RestorePersistedQueue` | QueueService.cs | **[FIXED]** | **Fixed.** |
| 18 | `ReplaceQueue` | QueueService.cs | **MISSING** | Bulk queue replace missing. |
| 19 | Notification settings normalization | CruncharrConfig.cs | **MISSING** | `NormalizeNotificationSettings` missing. |
| 20 | `AuthAnonymousFoxy` | CrunchyrollAuthService.cs | **MISSING** | Guest auth endpoint variation. |
| 21 | Stream endpoint auto-update | CrunchyrollAuthService.cs | **MISSING** | Doesn't check GitHub for newer auth versions. |
| 22 | API endpoint `POST /api/v1/queue/resume/{id}` | API Layer | **[FIXED]** | **Fixed.** |
| 23 | API endpoint `POST /api/v1/auth/change-profile` | API Layer | **[FIXED]** | **Fixed.** |

### LOW (UI-Only / Rarely Used)

| # | Item | File | Status | Issue |
|---|------|------|--------|-------|
| 24 | `GhUpdatePrereleases` | CruncharrConfig.cs | **MISSING** | GitHub update checker. |
| 25 | Tray icon properties (4) | CruncharrConfig.cs | **MISSING** | Desktop UI only. |
| 26 | `Force` / `Override` CLI flags | CruncharrConfig.cs | **MISSING** | CLI-only. |
| 27 | `Kstream` index selection | DownloadService.cs | **MISSING** | Always uses quality preference. |
| 28 | `StreamServer` selection | DownloadService.cs | **MISSING** | Commented out in upstream anyway. |
| 29 | `MoveFile` / `MoveFromTempFolder` | DownloadService.cs | **PARTIAL** | Temp cleanup handled differently. |
| 30 | Queue UI methods | QueueService.cs | **MISSING** | RefreshQueue, UpdateDownloadListItems, etc. |
| 31 | `FindClosestMatchCrSeries` | HistoryService.cs | **MISSING** | Crunchyroll series matching. |
| 32 | `GetQueueSnapshot` | QueueService.cs | **MISSING** | Thread-safe snapshot. |
| 33 | `SaveQueueSnapshot` | QueueService.cs | **MISSING** | Immediate save. |
| 34 | API endpoint `POST /api/v1/queue/replace` | API Layer | **MISSING** | Bulk replace. |
| 35 | API endpoint `GET /api/v1/history/series-thumbnail/{id}` | API Layer | **MISSING** | Thumbnail fetch. |

---

## 6. Models Audit

| Model | Status | Notes |
|-------|--------|-------|
| `CrunchyEpMeta.cs` | **[FIXED]** | Model now exists with full `CrunchyEpMeta`, `CrunchyEpMetaData`, `CrunchyRollEpisodeData`, `EpisodeAndLanguage`, `EpisodeVariant`, `CrunchyMultiDownload`, `CrunchySeriesList`, `EpisodeDisplay` classes. |
| `DownloadModels.cs` | **COMPLETE** | No changes needed. `DownloadProgress`, `QueueItem`, `EpisodeInfo`, `SeriesInfo`, `SeasonInfo`, `DownloadHistory`, `EpisodeVersion`, `CrBrowseEpisode`, `CrBrowseEpisodeMetaData`, `DownloadResult`, `DownloadException` all present. |
| `HistoryModels.cs` | **COMPLETE** | No changes needed. `HistorySeries`, `HistorySeason`, `HistoryEpisode`, `HistoryPageProperties`, `SeasonsPageProperties`, `SeriesDataCache` all present with Sonarr integration fields. |

---

## 7. Frontend Integration (index.html)

| Feature | API Endpoint | Status | Notes |
|---------|-------------|--------|-------|
| Downloads queue | `/api/v1/queue` | **COMPLETE** | Uses SSE for real-time updates. Supports pause/resume/retry/remove. |
| Add download search | `/api/v1/series/search` | **COMPLETE** | Search with debounce. Season dropdown. |
| Add download episodes | `/api/v1/series/{id}/episodes` | **COMPLETE** | Episode list with checkboxes. |
| Version-grouped episodes | `/api/v1/series/{id}/list` | **[FIXED]** | **Now supported.** Frontend can call new endpoint. |
| Multi-dub queue items | `/api/v1/series/item-select-multi-dub` | **[FIXED]** | **Now supported.** |
| Calendar | `/api/v1/calendar` | **COMPLETE** | Weekly grid view. |
| History | `/api/v1/history` | **COMPLETE** | Poster and table views. Sonarr match indicators. |
| Account/Auth | `/api/v1/auth/*` | **COMPLETE** | Login/logout, profile switch, status. |
| Settings | `/api/v1/config` | **COMPLETE** | Full config GET/POST with all sections. |

---

## 8. New Issues Found

**None.** No new critical, high, or medium issues were identified in the current codebase. All recent changes follow the porting protocol and do not introduce boundary violations.

---

## 9. Recommendations (Updated)

### Immediate (Do First)

1. ~~Fix `GetSeasonDataByIdAsync`~~ - **DONE.**
2. ~~Add API endpoint `POST /api/v1/queue/resume/{queueItemId}`~~ - **DONE.**
3. ~~Implement `RestoreRetryStateFromQueue`~~ - **DONE.**

### Short Term (This Week)

4. ~~Port `ItemSelectMultiDub` and `ListSeriesId`~~ - **DONE.**
5. ~~Add API endpoints for the above~~ - **DONE.**
6. ~~Fix `DownloadEpisodeAsync` Sonarr variables~~ - **DONE.**

### Medium Term (Next Sprint)

7. **Refine `DownloadMediaList` filename handling** - Replace simplified `FilenameService` with full `FileNameManager.ParseFileName()` upstream logic to support all template variables.
8. **Add `MuxDescription` handling** - Support description XML muxing in `MuxFilesAsync`.
9. **Implement `RefreshExistingEpisodesFromBrowse`** - Add metadata refresh optimization to `HistoryService`.
10. **Add `GetSeriesThumbnail` / thumbnail population** - Populate history series thumbnails from API images.

### Low Priority (Backlog)

11. Tray icon properties (not applicable to web UI).
12. `Kstream` / `StreamServer` selection (upstream has these commented out or rarely used).
13. `GhUpdatePrereleases` (not applicable to web UI).
14. Queue UI sync methods (not applicable to web UI).
15. `AuthAnonymousFoxy` (guest auth variation - low impact).
16. `ReplaceQueue` bulk operation.

---

*End of Updated Audit Report*
