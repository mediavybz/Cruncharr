# Comprehensive Upstream vs Downstream Audit Report

**Date:** 2026-05-26
**Project:** Crunchy-Downloader (Desktop) -> Cruncharr (Docker + Web UI)
**Auditor:** AI Agent

---

## Executive Summary

| Category | Count |
|----------|-------|
| **CRITICAL Missing Items** | 3 |
| **HIGH Missing Items** | 7 |
| **MEDIUM Missing Items** | 15 |
| **LOW Missing Items** | 12 |
| **Partial Implementations** | 8 |
| **Known Bugs** | 2 |
| **Config Properties Missing** | 7 |

**Overall Assessment:** The downstream implementation covers ~85% of upstream functionality. The core download pipeline, auth, history, and queue management are substantially ported. The largest gaps are in: (1) multi-dub queue item construction (`ItemSelectMultiDub`, `ListSeriesId`), (2) notification settings normalization, (3) queue resume/retry state restoration, and (4) several edge-case download features (AD track duplication, Sonarr filename variables, Kstream selection).

---

## 1. Config Properties Audit

### upstream_src/CrDownloadOptions.cs vs src/Cruncharr.Core/Configuration/CruncharrConfig.cs

| # | Upstream Property | Downstream Location | Status | Priority | Notes |
|---|-------------------|---------------------|--------|----------|-------|
| 1 | `GhUpdatePrereleases` | **MISSING** | **LOW** | UI-only (GitHub prerelease checker). Not applicable to web UI. |
| 2 | `Force` ([JsonIgnore]) | **MISSING** | **LOW** | CLI flag. Used for forcing muxer tool. Not applicable to web UI. |
| 3 | `Override` ([JsonIgnore]) | **MISSING** | **LOW** | CLI flag list. Used for filename override. Not applicable to web UI. |
| 4 | `TrayIconEnabled` | **MISSING** | **LOW** | Desktop UI-only. Not applicable to web UI. |
| 5 | `StartMinimizedToTray` | **MISSING** | **LOW** | Desktop UI-only. Not applicable to web UI. |
| 6 | `MinimizeToTray` | **MISSING** | **LOW** | Desktop UI-only. Not applicable to web UI. |
| 7 | `MinimizeToTrayOnClose` | **MISSING** | **LOW** | Desktop UI-only. Not applicable to web UI. |
| 8 | `NotificationSettings` (class) | **PARTIAL** | **MEDIUM** | Downstream uses simplified `NotificationsConfig` (webhook-based) instead of upstream's `NotificationSettings` with providers (Sound, Execute, Webhook). Missing: `NormalizeNotificationSettings()`, `SyncLegacyNotificationFields()` methods. Legacy sound/execute notification wiring is absent. |
| 9 | `StreamEndpoint` / `StreamEndpointSecondSettings` | **PARTIAL** | **MEDIUM** | Downstream `StreamEndpointConfig` class is missing `UseDefault` property handling for auto-updating auth from GitHub releases (upstream lines 254-370 in CrunchyrollManager.cs). The downstream uses static defaults instead of the dynamic version-checking logic. |
| 10 | `SubsAddScaledBorder` (enum) | **PARTIAL** | **LOW** | Downstream uses `string` instead of `ScaledBorderAndShadowSelection` enum. Values work but type safety lost. |
| 11 | `HistoryAutoRefreshMode` (enum) | **PARTIAL** | **LOW** | Downstream uses `int` instead of `HistoryRefreshMode` enum. Values work but type safety lost. |
| 12 | `SimultaneousDownloads` | **DUPLICATE** | **LOW** | Exists in BOTH `DownloadConfig` and `QueueConfig`. Upstream only has it in root config. Minor inconsistency. |

**Config Verdict:** 7 properties missing (all LOW). 4 partial implementations (1 MEDIUM, 3 LOW). No critical config gaps.

---

## 2. Service Methods Audit

### 2.1 upstream_src/CrSeries.cs vs src/Cruncharr.Core/Services/CrunchyrollApiService.cs

| # | Upstream Method | Downstream Method | Status | Priority | Details |
|---|-----------------|-------------------|--------|----------|---------|
| 1 | `ItemSelectMultiDub(Dictionary<string, EpisodeAndLanguage>, List<string>, bool?, List<string>?)` | **MISSING** | **HIGH** | This is the core method that constructs `CrunchyEpMeta` queue items from episode variants. It handles: premium checks, history dub override, season title fallbacks, playback preference selection, and `DownloadQueueItemFactory` shell creation. Without this, multi-dub queue items cannot be properly constructed. Downstream `DownloadEpisodeAsync` works with single `EpisodeInfo` objects instead. |
| 2 | `ListSeriesId(string, string, CrunchyMultiDownload?, bool)` | **MISSING** | **HIGH** | Fetches all seasons for a series, groups episodes by key (S1E1, etc.), handles versions, sorts specials vs normal episodes, prints episode list to console, and returns `CrunchySeriesList`. This is the primary method used by the desktop UI to display series episode lists. Downstream has `GetEpisodesAsync` which returns flat `List<EpisodeInfo>` without version grouping or special episode handling. |
| 3 | `ParseSeriesById(string, string?, bool)` | `ParseSeriesByIdAsync` | **PORTED** | - | Returns `List<SeasonInfo>` instead of `CrSeriesSearch`. Functional equivalent. |
| 4 | `GetSeasonDataById(string, string?, bool, bool)` | `GetSeasonDataByIdAsync` | **PARTIAL** | **MEDIUM** | Downstream delegates to `GetSeasonEpisodesAsync` which does NOT pass `crLocale` or `forcedLang` parameters to the API request. The upstream explicitly adds `preferred_audio_language`, `locale`, and `force_locale` query params. This means forced locale is not supported in downstream. |
| 5 | `SeriesById(string, string?, bool)` | `SeriesByIdAsync` | **PORTED** | - | Delegates to `GetSeriesAsync`. Functional equivalent. |
| 6 | `Search(string, string?, bool)` | `SearchAsync` | **PORTED** | - | Functional equivalent. Note: upstream `n=6`, downstream uses `n=20`. |
| 7 | `GetAllSeries(string?)` | `GetAllSeriesAsync` | **PORTED** | - | Functional equivalent. |
| 8 | `GetSeasonalSeries(string, string, string?)` | `GetSeasonalSeriesAsync` | **PORTED** | - | Functional equivalent. |

**CrSeries.cs Verdict:** 2 HIGH priority missing methods (`ItemSelectMultiDub`, `ListSeriesId`). 1 MEDIUM partial (`GetSeasonDataById` missing forced locale support).

---

### 2.2 upstream_src/CrEpisode.cs vs src/Cruncharr.Core/Services/CrunchyrollApiService.cs

| # | Upstream Method | Downstream Method | Status | Priority | Details |
|---|-----------------|-------------------|--------|----------|---------|
| 1 | `ParseEpisodeById(string, string, bool)` | `ParseEpisodeByIdAsync` | **PORTED** | - | Functional equivalent with version deduplication logic. |
| 2 | `EpisodeData(CrunchyEpisode, bool)` | **MISSING** | **MEDIUM** | Converts a single `CrunchyEpisode` to `CrunchyRollEpisodeData` with version grouping, special episode key handling (`SP` prefix), and console logging. Used when adding individual episodes to queue. Downstream bypasses this model and works directly with `EpisodeInfo`. |
| 3 | `EpisodeMeta(CrunchyRollEpisodeData, List<string>)` | **MISSING** | **MEDIUM** | Creates `CrunchyEpMeta` from `CrunchyRollEpisodeData` with dub filtering, season title fallbacks, and `DownloadQueueItemFactory` shell/variant creation. This is the bridge between API data and queue items. |
| 4 | `GetNewEpisodes(string?, int, DateTime?, bool)` | `GetNewEpisodesAsync` | **PORTED** | - | Functional equivalent. |
| 5 | `MarkAsWatched(string)` | `MarkAsWatchedAsync` | **PORTED** | - | Functional equivalent. |

**CrEpisode.cs Verdict:** 2 MEDIUM priority missing methods (`EpisodeData`, `EpisodeMeta`). These are convenience wrappers; the downstream handles the same data differently.

---

### 2.3 upstream_src/CrunchyrollManager.cs vs src/Cruncharr.Core/Services/DownloadService.cs + QueueService.cs

This is the largest file pair. The upstream `CrunchyrollManager.cs` is ~2000+ lines.

#### DownloadService.cs - MISSING Methods/Logic

| # | Upstream Method/Feature | Status | Priority | Details |
|---|------------------------|--------|----------|---------|
| 1 | `DownloadMediaList(CrunchyEpMeta, CrDownloadOptions)` | **PARTIAL** | **CRITICAL** | The downstream `DownloadEpisodeAsync` reimplements the core logic but with significant differences: (a) Missing Sonarr variable substitution (`sonarrSeriesTitle`, `sonarrEpisodeTitle`, `sonarrSeriesReleaseYear`) - breaks Sonarr-integrated filenames. (b) Missing `DownloadDescriptionAudio` duplication logic (lines 1306-1332 in upstream) - AD tracks are not auto-generated from main tracks. (c) Missing proper `FileNameManager.ParseFileName()` with all upstream variables - downstream uses simplified `FilenameService`. (d) Missing `WaitForDubDownloadDelayAsync()` - no delay between dub downloads. (e) Missing Kstream index selection (always uses quality preference instead). |
| 2 | `WaitForDubDownloadDelayAsync(CrunchyEpMeta, CrDownloadOptions)` | **MISSING** | **MEDIUM** | Semaphore + lock-based delay between downloading different dubs. Respects `DubDownloadDelaySeconds` config. Downstream downloads dubs concurrently without delay. |
| 3 | `MuxStreams(List<DownloadedMedia>, CrunchyMuxOptions, string, CrunchyEpMeta)` | **PARTIAL** | **HIGH** | Downstream `MuxFilesAsync` handles basic muxing but: (a) Missing `Mp3` output support (only checks extension). (b) Missing `muxToMp3` variable logic. (c) Missing `MuxDescription` handling (description XML muxing). (d) Missing `ForceMuxer` support. (e) Missing `DlVideoOnce` handling in mux options. (f) Missing `KeepAllVideos` locale-aware video matching (downstream sets `KeepAllVideos = true` blindly). |
| 4 | `TrySyncTimingFallbackAsync(DownloadResponse, CrunchyEpMeta, CrDownloadOptions, List<string>)` | **PARTIAL** | **MEDIUM** | Downstream has fallback video download in `DownloadEpisodeAsync` (lines 624-650) but: (a) Doesn't create a separate `DownloadResponse` with fallback options. (b) Doesn't remove old videos and replace them in the response data. (c) Doesn't set `SyncTiming = false` on remux. The logic is inline instead of extracted. |
| 5 | `FetchPlaybackData(CrAuth, string, string, bool, bool, CrAuthSettings)` | **PARTIAL** | **HIGH** | Downstream `GetPlaybackDataAsync` supports multiple endpoints (TV, Mobile, Firefox) but: (a) Missing the stream URL deduplication/merging logic when both endpoints return data (upstream lines 1506-1534). (b) Missing `data.Music` and `isAudioRoleDescription` parameters. (c) Missing explicit stream server selection. |
| 6 | `DownloadVideo(VideoItem, CrDownloadOptions, string, string, CrunchyEpMeta, string)` | **MISSING** | **MEDIUM** | Upstream has dedicated video download method with HLS/DASH handling, progress reporting, and resume support. Downstream uses `DownloadDashTracksAsync` + `HlsDownloader` but the integration is different. |
| 7 | `DownloadAudio(AudioItem, ...)` | **MISSING** | **MEDIUM** | Similar to above - dedicated audio download method. |
| 8 | `GetDownloadedDubs(CrunchyEpMeta, DownloadResponse, CrDownloadOptions)` | **MISSING** | **MEDIUM** | Helper to extract downloaded dub locales from response data. Used for history tracking. Downstream approximates this in `DownloadEpisodeAsync` but may miss edge cases (e.g., `DownloadMediaType.SyncVideo`). |
| 9 | `GetDownloadedSoftSubs(DownloadResponse)` | **MISSING** | **MEDIUM** | Helper to extract downloaded subtitle locales. Same as above. |
| 10 | `MoveFromTempFolder(Merger?, CrunchyEpMeta, CrDownloadOptions, string, List<SubtitleInput>, bool)` | **PARTIAL** | **LOW** | Downstream handles temp cleanup differently (deletes temp dir instead of moving files). The upstream moves files from temp to final destination with collision handling. |
| 11 | `MoveFile(string, string, string, CrDownloadOptions, bool)` | **MISSING** | **LOW** | Individual file move with path validation and collision handling. |
| 12 | `DeleteSyncVideoFiles(List<DownloadedMedia>)` | **PARTIAL** | **LOW** | Downstream deletes sync videos inline (lines 614-622) but doesn't handle `.new.resume` files. |
| 13 | `InitDownloadOptions()` / `InitOptions()` | **NOT NEEDED** | - | Downstream uses DI and config file loading instead. |
| 14 | `GetBase64EncodedTokenAsync()` | **PORTED** | - | Moved to `CrunchyrollAuthService`. |

#### QueueService.cs - MISSING Methods/Logic

| # | Upstream Method | Status | Priority | Details |
|---|-----------------|--------|----------|---------|
| 1 | `TryResumeDownload(CrunchyEpMeta)` | **MISSING** | **MEDIUM** | Resumes paused downloads. Downstream has `ResumeItem()` which changes state to Queued, but `TryResumeDownload` in upstream actually adds the item back to `activeOrStarting` and triggers download core. In downstream, resumed items rely on `PumpQueueAsync` to pick them up, which works but the explicit resume path is missing. |
| 2 | `RestoreRetryStateFromQueue()` | **MISSING** | **MEDIUM** | When queue is loaded from persistence, this restores retry states and sets `autoDownloadBlockedUntilUtc`. Downstream loads persisted queue in constructor but doesn't restore retry states. |
| 3 | `ScheduleRetryWake(CrunchyEpMeta, DateTimeOffset?, CancellationToken)` | **MISSING** | **MEDIUM** | Dedicated wake-up timer for retry items. Downstream `ScheduleRetry` sets a timer but the wake logic is simpler. |
| 4 | `RefreshQueue()` | **MISSING** | **LOW** | UI refresh trigger. Not needed for web UI (uses event-based updates). |
| 5 | `UpdateDownloadListItems()` | **MISSING** | **LOW** | Syncs download item models with queue. Not needed for web UI. |
| 6 | `ReplaceQueue(IEnumerable<CrunchyEpMeta>)` | **MISSING** | **MEDIUM** | Replaces entire queue with new items. Used for queue persistence restore. Downstream loads queue on startup but doesn't have a replace method. |
| 7 | `MarkDownloadFinished(CrunchyEpMeta, bool)` | **PARTIAL** | **LOW** | Downstream handles finish in `RunDownloadAsync` but doesn't have the explicit queue removal + UI refresh logic from upstream. |
| 8 | `RestorePersistedQueue()` | **MISSING** | **MEDIUM** | Explicit queue restore from persistence. Downstream does this in constructor but without retry state restoration. |
| 9 | `SaveQueueSnapshot()` | **MISSING** | **LOW** | Immediate queue save. Downstream uses `ScheduleSave()` which is deferred. |
| 10 | `GetQueueSnapshot()` | **MISSING** | **LOW** | Thread-safe queue snapshot for UI. Not needed for web UI. |

**CrunchyrollManager.cs Verdict:** 1 CRITICAL partial (`DownloadMediaList`). 7 HIGH/MEDIUM missing features. 6 MEDIUM/LOW queue features missing.

---

### 2.4 upstream_src/History.cs vs src/Cruncharr.Core/Services/HistoryService.cs

| # | Upstream Method | Downstream Method | Status | Priority | Details |
|---|-----------------|-------------------|--------|----------|---------|
| 1 | `CrUpdateSeries(string?, string?)` | `CrUpdateSeriesAsync` | **PARTIAL** | **MEDIUM** | Downstream simplified: (a) Doesn't check season versions for original GUID candidates (upstream lines 68-84). (b) Only uses `season.Id` as candidate, missing version GUIDs. (c) Missing `RemoveUnavailableEpisodes` call after update. (d) Doesn't handle empty season removal. |
| 2 | `UpdateWithMusicEpisodeList(List<CrunchyMusicVideo>)` | `UpdateWithMusicEpisodeListAsync` | **PARTIAL** | **LOW** | Downstream simplified - groups all music videos together without artist grouping. Loses artist-specific organization. |
| 3 | `UpdateWithEpisodeList(List<CrunchyEpisode>)` | `UpdateWithEpisodeListAsync` | **PORTED** | - | Functional equivalent. |
| 4 | `UpdateWithEpisode(List<CrBrowseEpisode>)` | `UpdateWithEpisodeAsync` | **PARTIAL** | **MEDIUM** | Downstream significantly simplified. Missing: (a) Original ID detection from versions. (b) `IsOriginalItem` check. (c) `allOriginalsInHistory` optimization. (d) `HasAllSeriesEpisodesInHistory` check. (e) `RefreshExistingEpisodesFromBrowse` for metadata refresh. (f) Falls back to `CrUpdateSeries` for every series instead of smart checking. This means calendar updates will trigger full series refreshes much more often, causing unnecessary API calls. |
| 5 | `RefreshExistingEpisodesFromBrowse(IEnumerable<CrBrowseEpisode>)` | **MISSING** | **MEDIUM** | Updates existing history episode metadata (dubs/subs availability) from browse data without full series refresh. Downstream doesn't have this optimization. |
| 6 | `TryGetOriginalId(CrBrowseEpisode)` | **MISSING** | **LOW** | Internal helper. |
| 7 | `TryGetOriginalSeasonId(CrBrowseEpisode)` | **MISSING** | **LOW** | Internal helper. |
| 8 | `IsOriginalItem(CrBrowseEpisode)` | **MISSING** | **LOW** | Internal helper. |
| 9 | `IsOriginalInHistory(...)` | **MISSING** | **LOW** | Internal helper. |
| 10 | `HasAllSeriesEpisodesInHistory(...)` | **MISSING** | **LOW** | Internal helper. |
| 11 | `UpdateWithSeasonData(List<IHistorySource>, bool)` | `UpdateWithSeasonDataAsync` | **PARTIAL** | **LOW** | Downstream simplified: (a) Missing `matchSonarr` parameter - always matches Sonarr. (b) Missing `SortItems()` call after update. (c) Missing `SortSeasons()` call. (d) Missing `HistorySeriesAddDate` initialization. (e) Missing `SeriesStreamingService` assignment. (f) Missing `SeriesType` inference. (g) Missing `LoadImage()` call. (h) Missing `Init()` call for new series. |
| 12 | `MatchHistorySeriesWithSonarr(bool)` | `MatchHistorySeriesWithSonarrAsync` | **PORTED** | - | Functional equivalent. |
| 13 | `MatchSingleHistorySeriesWithSonarr(HistorySeries)` | **MISSING** | **LOW** | Inlined in downstream. |
| 14 | `SetAsDownloaded(...)` | `SetAsDownloadedAsync` | **PORTED** | - | Functional equivalent. |
| 15 | `GetHistoryEpisode(...)` | `GetHistoryEpisodeAsync` | **PORTED** | - | Functional equivalent. |
| 16 | `GetHistoryEpisodeWithDownloadDir(...)` | `GetHistoryEpisodeWithDownloadDirAsync` | **PORTED** | - | Functional equivalent. |
| 17 | `GetHistoryEpisodeWithDubListAndDownloadDir(...)` | `GetHistoryEpisodeWithDubListAndDownloadDirAsync` | **PORTED** | - | Functional equivalent. |
| 18 | `GetDubList(...)` | `GetDubListAsync` | **PORTED** | - | Functional equivalent. |
| 19 | `GetSubList(...)` | `GetSubListAsync` | **PORTED** | - | Functional equivalent. |
| 20 | `RefreshSeriesData(string, HistorySeries)` | `RefreshSeriesDataAsync` | **PARTIAL** | **MEDIUM** | Downstream simplified: (a) Missing series data cache hit handling (always fetches from API). (b) Missing artist type handling (only handles Series/Movie). (c) Missing `AudioLocales` and `SubtitleLocales` population from series data. (d) Missing thumbnail image URL extraction. |
| 21 | `SortSeasons(HistorySeries)` | `SortSeasons` (static) | **PORTED** | - | Functional equivalent. |
| 22 | `SortItems()` | `SortItemsAsync` | **PORTED** | - | Functional equivalent. |
| 23 | `ParseDate(string, DateTime)` | `ParseDate` | **PORTED** | - | Functional equivalent. |
| 24 | `InferSeriesType(HistorySeries?)` | `InferSeriesType` (static) | **PORTED** | - | Functional equivalent. |
| 25 | `GetSeriesThumbnail(CrSeriesBase)` | **MISSING** | **MEDIUM** | Extracts thumbnail from series images. Used for history display. Downstream doesn't populate history series thumbnails from API. |
| 26 | `RemoveUnavailableEpisodes(HistorySeries)` | `RemoveUnavailableEpisodesFromSeries` | **PORTED** | - | Functional equivalent (private method). |
| 27 | `NewHistorySeason(...)` | `CreateHistorySeason` | **PORTED** | - | Functional equivalent (private method). |
| 28 | `MatchHistoryEpisodesWithSonarr(bool, HistorySeries)` | `MatchHistoryEpisodesWithSonarrAsync` | **PORTED** | - | Functional equivalent. |
| 29 | `GetNextAirDate(List<SonarrEpisode>)` | `GetNextAirDate` (static) | **PORTED** | - | Functional equivalent. |
| 30 | `FindClosestMatch(string)` | `FindClosestMatch` (static) | **PORTED** | - | Functional equivalent. |
| 31 | `FindClosestMatchEpisodes(List<SonarrEpisode>, string)` | **MISSING** | **LOW** | Wrapper around `FindClosestMatchEpisodeWithScore`. Not used directly in downstream. |
| 32 | `FindClosestMatchEpisodeWithScore(...)` | `FindClosestMatchEpisodeWithScore` (static) | **PORTED** | - | Functional equivalent. |
| 33 | `FindClosestMatchCrSeries(...)` | **MISSING** | **LOW** | Used for Crunchyroll series matching. Not needed in current downstream. |
| 34 | `CalculateSimilarity(string, string)` | `CalculateSimilarity` | **PORTED** | - | Delegates to `StringSimilarity`. |
| 35 | `LevenshteinDistance(string, string)` | **MISSING** | **LOW** | Moved to `StringSimilarity` class. Not missing, just relocated. |

**History.cs Verdict:** 1 MEDIUM partial (`UpdateWithEpisode` significantly simplified). 2 MEDIUM missing (`RefreshExistingEpisodesFromBrowse`, `GetSeriesThumbnail`). 7 LOW missing (mostly internal helpers).

---

### 2.5 upstream_src/QueueManager.cs vs src/Cruncharr.Core/Services/QueueService.cs

| # | Upstream Method | Downstream Method | Status | Priority | Details |
|---|-----------------|-------------------|--------|----------|---------|
| 1 | `AddToQueue(CrunchyEpMeta)` | `AddToQueue(EpisodeInfo)` | **PORTED** | - | Different signature (uses `EpisodeInfo` instead of `CrunchyEpMeta`). Functional equivalent. |
| 2 | `RemoveFromQueue(CrunchyEpMeta)` | `RemoveFromQueue(string)` | **PORTED** | - | Uses ID instead of object reference. Safer for web UI. |
| 3 | `ClearQueue()` | `ClearQueue()` | **PORTED** | - | Functional equivalent. |
| 4 | `RefreshQueue()` | **MISSING** | **LOW** | UI refresh method. Not needed for web UI (event-driven). |
| 5 | `TryStartDownload(DownloadItemModel)` | `StartItem(string)` | **PARTIAL** | **MEDIUM** | Downstream `StartItem` gets queue item by ID and starts download. Missing: integration with `DownloadItemModel` (not needed in web UI). The pump loop handles auto-start. |
| 6 | `TryResumeDownload(CrunchyEpMeta)` | **MISSING** | **MEDIUM** | Explicit resume path for paused items. Downstream `ResumeItem` only changes state; the item won't start downloading until pump picks it up. This may cause resumed items to sit in queue if auto-download is off. |
| 7 | `ReleaseDownloadSlot(CrunchyEpMeta)` | `ReleaseDownloadSlot` (private) | **PORTED** | - | Functional equivalent. |
| 8 | `WaitForProcessingSlotAsync(CancellationToken)` | `WaitForProcessingSlotAsync` | **PORTED** | - | Functional equivalent. |
| 9 | `ReleaseProcessingSlot()` | `ReleaseProcessingSlot()` | **PORTED** | - | Functional equivalent. |
| 10 | `SetProcessingLimit(int)` | `SetProcessingLimit(int)` | **PORTED** | - | Functional equivalent. |
| 11 | `RestorePersistedQueue()` | **MISSING** | **MEDIUM** | Explicit restore method. Downstream loads queue in constructor but doesn't restore retry states. |
| 12 | `SaveQueueSnapshot()` | **MISSING** | **LOW** | Immediate save. Downstream uses deferred save. |
| 13 | `GetQueueSnapshot()` | **MISSING** | **LOW** | Thread-safe snapshot. Not needed for web UI. |
| 14 | `ReplaceQueue(IEnumerable<CrunchyEpMeta>)` | **MISSING** | **MEDIUM** | Replace entire queue. Used for persistence restore and bulk operations. |
| 15 | `MarkDownloadFinished(CrunchyEpMeta, bool)` | **PARTIAL** | **LOW** | Handled in `RunDownloadAsync` but lacks explicit queue removal logic when `RemoveFinishedDownload` is true. |
| 16 | `UpdateDownloadListItems()` | **MISSING** | **LOW** | UI model sync. Not needed for web UI. |
| 17 | `RequestPump()` | `RequestPump()` (private) | **PORTED** | - | Functional equivalent. |
| 18 | `RunPump()` | **PARTIAL** | **LOW** | Downstream uses `Task.Run` instead of Avalonia `Dispatcher.UIThread.Post`. This is correct for web UI. |
| 19 | `PumpQueue()` | `PumpQueueAsync()` (private) | **PARTIAL** | **LOW** | Downstream missing: (a) `ProgramManager.Instance.FinishedLoading` check. (b) Resume state handling (`toResume` list). (c) `DownloadItemModel` integration. |
| 20 | `BlockAutoDownloadUntil(TimeSpan, CancellationToken)` | `BlockAutoDownloadUntil(TimeSpan)` | **PORTED** | - | Functional equivalent. |
| 21 | `ScheduleRetry(CrunchyEpMeta, TimeSpan, string, CancellationToken)` | `ScheduleRetry(string, TimeSpan, string)` | **PARTIAL** | **MEDIUM** | Downstream missing `CancellationToken` parameter and doesn't call `RefreshQueue()` / `UpdateDownloadListItems()` after scheduling. The wake-up logic is also simpler. |
| 22 | `RestoreRetryStateFromQueue()` | **MISSING** | **MEDIUM** | Restores retry state when queue is loaded. Critical for persisted queue with retry items. |
| 23 | `HasPendingRetryItems()` | `HasPendingRetryItems()` (private) | **PORTED** | - | Functional equivalent. |
| 24 | `ScheduleRetryWake(CrunchyEpMeta, DateTimeOffset?, CancellationToken)` | **MISSING** | **MEDIUM** | Dedicated retry wake task. Downstream handles this inline in `ScheduleRetry` but doesn't check `cancellationToken.IsCancellationRequested`. |
| 25 | `OnQueueStateChanged()` | `OnQueueStateChanged()` | **PORTED** | - | Functional equivalent. |
| 26 | `NotifyDownloadStateChanged()` | `NotifyDownloadStateChanged()` | **PARTIAL** | **LOW** | Downstream doesn't update `ActiveDownloads` / `HasActiveDownloads` observable properties (not needed in web UI). |

**QueueManager.cs Verdict:** 4 MEDIUM missing methods (`TryResumeDownload`, `RestorePersistedQueue`, `ReplaceQueue`, `RestoreRetryStateFromQueue`, `ScheduleRetryWake`). 6 LOW missing. Core queue pump logic is ported.

---

### 2.6 upstream_src/CRAuth.cs vs src/Cruncharr.Core/Services/CrunchyrollAuthService.cs

| # | Upstream Method | Downstream Method | Status | Priority | Details |
|---|-----------------|-------------------|--------|----------|---------|
| 1 | `Init()` | `Init()` | **PORTED** | - | Functional equivalent. |
| 2 | `GetTokenFilePath()` | `GetTokenFilePath()` | **PORTED** | - | Functional equivalent. |
| 3 | `Auth()` | `AuthenticateAsync()` | **PARTIAL** | **LOW** | Downstream doesn't check `EndpointEnum` for guest auth. Falls back to anonymous if token login fails. |
| 4 | `SetETPCookie(string)` | `SetETPCookie(string)` | **PORTED** | - | Functional equivalent. |
| 5 | `AuthAnonymous()` | `AuthAnonymousAsync()` | **PORTED** | - | Functional equivalent. |
| 6 | `JsonTokenToFileAndVariable(string, string)` | `JsonTokenToFileAndVariable(string, string)` | **PORTED** | - | Functional equivalent. |
| 7 | `AuthOld(AuthData)` | `LoginAsync(string, string, ...)` | **PORTED** | - | Functional equivalent. No Toast messages in downstream (correct for backend). |
| 8 | `ChangeProfile(string)` | `ChangeProfileAsync(string, ...)` | **PORTED** | - | Functional equivalent. |
| 9 | `GetProfile()` | `GetProfileAsync()` | **PORTED** | - | Private in downstream. Called by `GetMultiProfileAsync`. |
| 10 | `GetSubscription()` | `GetSubscriptionAsync()` | **PORTED** | - | Private in downstream. Called by `GetMultiProfileAsync`. |
| 11 | `GetMultiProfile()` | `GetMultiProfileAsync()` | **PORTED** | - | Functional equivalent. |
| 12 | `LoginWithToken()` | `LoginWithTokenAsync()` | **PORTED** | - | Functional equivalent. |
| 13 | `RefreshToken(bool)` | `RefreshTokenAsync()` | **PORTED** | - | **BUG FIXED:** The incorrect `if (Profile.Username == "???") return false;` check was removed. See Bug #1 below. |
| 14 | `AuthAnonymousFoxy()` | **MISSING** | **LOW** | Guest auth with Foxy endpoint. Downstream uses `AuthAnonymousAsync` for all anonymous auth. The Foxy-specific guest endpoint may not be needed. |

**CRAuth.cs Verdict:** Nearly complete. 1 LOW missing method (`AuthAnonymousFoxy`). The critical login bug was fixed.

---

## 3. Known Bugs

### Bug #1: FIXED - RefreshTokenAsync Incorrect Early Return

**Location:** `src/Cruncharr.Core/Services/CrunchyrollAuthService.cs:RefreshTokenAsync`

**Upstream Code (CRAuth.cs:432-434):**
```csharp
if (Profile.Username == "???"){
    return;
}
```

**Issue:** This check exists in the upstream source. When a user's token expires and needs refresh, if `Profile.Username` was "???" (e.g., after anonymous auth or before profile fetch), the refresh would silently fail. In a web UI where the profile might not be fetched immediately, this broke login persistence.

**Fix Status:** FIXED. The downstream `RefreshTokenAsync` no longer has this early return. It proceeds with the refresh regardless of profile state.

**Verification:** Checked current downstream code - the `if (Profile.Username == "???")` check is NOT present in `RefreshTokenAsync`.

---

### Bug #2: POTENTIAL - Similar "???" Check in ChangeProfileAsync

**Location:** `src/Cruncharr.Core/Services/CrunchyrollAuthService.cs:ChangeProfileAsync` (line 470)

**Code:**
```csharp
if (Profile.Username == "???"){
    return false;
}
```

**Assessment:** This is CORRECT behavior. `ChangeProfile` requires an active user session. If the user is not logged in (Username == "???"), changing profiles is impossible. This is not a bug.

---

### Bug #3: POTENTIAL - AuthenticateAsync Doesn't Handle Password Login

**Location:** `src/Cruncharr.Core/Services/CrunchyrollAuthService.cs:AuthenticateAsync`

**Issue:** If a token file exists but the refresh token is expired, `AuthenticateAsync` falls back to `AuthAnonymousAsync` and returns `false`. It does NOT attempt password login even if `config.Crunchyroll.Email` and `Password` are set.

**Upstream Behavior:** Upstream `Auth()` only tries token-based login. Password login is a separate UI action. So this matches upstream.

**Assessment:** NOT A BUG. Matches upstream behavior. However, for Docker deployments with env vars set, it might be nice to auto-login with credentials if token refresh fails. This would be an enhancement, not a bug.

---

### Bug #4: POTENTIAL - GetSeasonDataByIdAsync Ignores Locale Parameters

**Location:** `src/Cruncharr.Core/Services/CrunchyrollApiService.cs:GetSeasonDataByIdAsync` (line 509-511)

**Code:**
```csharp
public async Task<List<EpisodeInfo>> GetSeasonDataByIdAsync(string seasonId, string? crLocale, bool forcedLang = false, CancellationToken cancellationToken = default){
    return await GetSeasonEpisodesAsync(seasonId, true, cancellationToken);
}
```

**Issue:** The `crLocale` and `forcedLang` parameters are completely ignored. The upstream method (CrSeries.cs:334-387) explicitly adds these to the query string:
```csharp
query["preferred_audio_language"] = "ja-JP";
if (!string.IsNullOrEmpty(crLocale)){
    query["locale"] = crLocale;
    if (forcedLang){
        query["force_locale"] = crLocale;
    }
}
```

**Impact:** Forced locale functionality is broken. Users cannot force a specific locale for season data.

**Priority:** MEDIUM

---

### Bug #5: POTENTIAL - DownloadEpisodeAsync Missing Sonarr Variables

**Location:** `src/Cruncharr.Core/Services/DownloadService.cs:DownloadEpisodeAsync`

**Issue:** The upstream `DownloadMediaList` (lines 1296-1304) fetches Sonarr episode data and adds variables:
- `sonarrSeriesTitle`
- `sonarrSeriesReleaseYear`
- `sonarrEpisodeTitle`

The downstream `DownloadEpisodeAsync` does not fetch Sonarr data or populate these variables. Filename templates using these variables will produce empty values.

**Impact:** Sonarr-integrated filename templates don't work.

**Priority:** MEDIUM

---

### Bug #6: POTENTIAL - QueueService Doesn't Restore Retry State

**Location:** `src/Cruncharr.Core/Services/QueueService.cs` constructor

**Issue:** When loading persisted queue, retry states are not restored. Items that were waiting for retry when the app shut down will be loaded with `State = Queued` instead of `State = WaitingForRetry`. The `autoDownloadBlockedUntilUtc` is also not set.

**Impact:** Retry items may immediately start downloading again after restart, or the auto-download pump may not wait for retry delays.

**Priority:** MEDIUM

---

### Bug #7: POTENTIAL - HistoryService.UpdateWithEpisodeAsync Over-simplified

**Location:** `src/Cruncharr.Core/Services/HistoryService.cs:UpdateWithEpisodeAsync` (line 822-862)

**Issue:** The downstream version converts all browse episodes and calls `UpdateWithSeasonDataAsync`. It does NOT:
1. Check if original items are already in history
2. Skip series where all originals are already tracked
3. Refresh existing episode metadata without full series update
4. Handle version mismatch detection

**Impact:** Calendar updates will trigger unnecessary full series refreshes, causing excessive API calls and slower updates.

**Priority:** MEDIUM

---

## 4. API Endpoints Audit

### Endpoints That Should Exist But Don't

| # | Endpoint | Wraps | Status | Priority | Details |
|---|----------|-------|--------|----------|---------|
| 1 | `POST /api/v1/queue/resume/{queueItemId}` | `QueueService.ResumeItem()` + `TryResumeDownload()` | **MISSING** | **MEDIUM** | Downstream has `ResumeItem()` which changes state to Queued, but there's no API endpoint to trigger it. The frontend cannot resume paused downloads. |
| 2 | `POST /api/v1/queue/start/{queueItemId}` | `QueueService.StartItem()` | **EXISTS** | - | Already exists. |
| 3 | `POST /api/v1/queue/replace` | `QueueService.ReplaceQueue()` | **MISSING** | **LOW** | For bulk queue operations. Not critical for basic functionality. |
| 4 | `GET /api/v1/series/{id}/list` | `CrSeries.ListSeriesId()` | **MISSING** | **HIGH** | Returns grouped episode list with versions. The frontend currently uses `GetEpisodesAsync` which returns flat episodes. The web UI cannot display version-grouped episode lists like the desktop app. |
| 5 | `POST /api/v1/series/item-select-multi-dub` | `CrSeries.ItemSelectMultiDub()` | **MISSING** | **HIGH** | Constructs multi-dub queue items. Without this, the frontend cannot properly construct queue items for multi-dub downloads with all the upstream logic (premium checks, history overrides, etc.). |
| 6 | `GET /api/v1/auth/profile` | `CrAuth.GetProfile()` | **MISSING** | **LOW** | Profile info is available via `/api/v1/auth/status`. Not critical. |
| 7 | `POST /api/v1/auth/change-profile` | `CrAuth.ChangeProfile()` | **MISSING** | **MEDIUM** | Multi-profile support exists in auth service but no API endpoint exposes it. |
| 8 | `GET /api/v1/history/series-thumbnail/{seriesId}` | `History.GetSeriesThumbnail()` | **MISSING** | **LOW** | History series thumbnails are not populated from API. |

---

## 5. Priority Matrix

### CRITICAL (Breaks Core Functionality)

| # | Item | File | Issue |
|---|------|------|-------|
| 1 | `DownloadMediaList` partial implementation | DownloadService.cs | Missing Sonarr variables, AD track duplication, FileNameManager variables, dub download delays |
| 2 | `ItemSelectMultiDub` missing | CrunchyrollApiService.cs | Cannot construct proper multi-dub queue items |
| 3 | `ListSeriesId` missing | CrunchyrollApiService.cs | Cannot display version-grouped episode lists |

### HIGH (Important Feature Missing)

| # | Item | File | Issue |
|---|------|------|-------|
| 4 | `MuxStreams` partial | DownloadService.cs | Missing Mp3 muxing, description muxing, force muxer, KeepAllVideos locale matching |
| 5 | `GetSeasonDataByIdAsync` ignores locale | CrunchyrollApiService.cs | Forced locale not supported |
| 6 | `FetchPlaybackData` partial | DownloadService.cs | Missing multi-endpoint data merging |
| 7 | API endpoint `GET /api/v1/series/{id}/list` | API Layer | Frontend cannot show grouped episodes |
| 8 | API endpoint `POST /api/v1/series/item-select-multi-dub` | API Layer | Frontend cannot build multi-dub queue items |
| 9 | `EpisodeData` / `EpisodeMeta` missing | CrunchyrollApiService.cs | Bridge between API and queue items missing |
| 10 | `UpdateWithEpisode` oversimplified | HistoryService.cs | Excessive API calls on calendar update |

### MEDIUM (Nice to Have / Optimization)

| # | Item | File | Issue |
|---|------|------|-------|
| 11 | `WaitForDubDownloadDelayAsync` missing | DownloadService.cs | No delay between dub downloads |
| 12 | `DownloadDescriptionAudio` duplication | DownloadService.cs | AD tracks not auto-generated |
| 13 | Sonarr filename variables | DownloadService.cs | `sonarrSeriesTitle` etc. not populated |
| 14 | `RefreshExistingEpisodesFromBrowse` missing | HistoryService.cs | Metadata refresh optimization missing |
| 15 | `GetSeriesThumbnail` missing | HistoryService.cs | History thumbnails not populated |
| 16 | `TryResumeDownload` missing | QueueService.cs | Explicit resume path missing |
| 17 | `RestoreRetryStateFromQueue` missing | QueueService.cs | Retry state not restored on load |
| 18 | `ScheduleRetryWake` missing | QueueService.cs | Retry wake logic incomplete |
| 19 | `RestorePersistedQueue` missing | QueueService.cs | Explicit restore method missing |
| 20 | `ReplaceQueue` missing | QueueService.cs | Bulk queue replace missing |
| 21 | Notification settings normalization | CruncharrConfig.cs | `NormalizeNotificationSettings` missing |
| 22 | `AuthAnonymousFoxy` missing | CrunchyrollAuthService.cs | Guest auth endpoint variation |
| 23 | Stream endpoint auto-update | CrunchyrollAuthService.cs | Doesn't check GitHub for newer auth versions |
| 24 | API endpoint `POST /api/v1/queue/resume/{id}` | API Layer | Cannot resume paused items |
| 25 | API endpoint `POST /api/v1/auth/change-profile` | API Layer | Multi-profile not exposed |

### LOW (UI-Only / Rarely Used)

| # | Item | File | Issue |
|---|------|------|-------|
| 26 | `GhUpdatePrereleases` | CruncharrConfig.cs | GitHub update checker |
| 27 | Tray icon properties (4) | CruncharrConfig.cs | Desktop UI only |
| 28 | `Force` / `Override` CLI flags | CruncharrConfig.cs | CLI-only |
| 29 | `Kstream` index selection | DownloadService.cs | Always uses quality preference |
| 30 | `StreamServer` selection | DownloadService.cs | Commented out in upstream anyway |
| 31 | `MoveFile` / `MoveFromTempFolder` | DownloadService.cs | Temp cleanup handled differently |
| 32 | Queue UI methods | QueueService.cs | RefreshQueue, UpdateDownloadListItems, etc. |
| 33 | `FindClosestMatchCrSeries` | HistoryService.cs | Crunchyroll series matching |
| 34 | `GetQueueSnapshot` | QueueService.cs | Thread-safe snapshot |
| 35 | `SaveQueueSnapshot` | QueueService.cs | Immediate save |
| 36 | API endpoint `POST /api/v1/queue/replace` | API Layer | Bulk replace |
| 37 | API endpoint `GET /api/v1/history/series-thumbnail/{id}` | API Layer | Thumbnail fetch |

---

## 6. Recommendations

### Immediate (Do First)

1. **Fix `GetSeasonDataByIdAsync`** to pass `crLocale` and `forcedLang` to the API request. This is a 5-line fix.

2. **Add API endpoint** `POST /api/v1/queue/resume/{queueItemId}` so the frontend can resume paused downloads.

3. **Implement `RestoreRetryStateFromQueue`** in `QueueService` constructor so persisted retry items work correctly after restart.

### Short Term (This Week)

4. **Port `ItemSelectMultiDub`** and `ListSeriesId` from upstream. These are the biggest functional gaps for multi-dub support.

5. **Add API endpoints** for the above: `GET /api/v1/series/{id}/list` and `POST /api/v1/series/item-select-multi-dub`.

6. **Fix `DownloadEpisodeAsync`** to include Sonarr variable fetching when `SonarrConfig.Enabled` is true.

### Medium Term (Next Sprint)

7. **Refine `UpdateWithEpisodeAsync`** to match upstream logic for original ID detection and history optimization.

8. **Add `WaitForDubDownloadDelayAsync`** to respect `DubDownloadDelaySeconds` config.

9. **Implement `DownloadDescriptionAudio` duplication** logic for AD tracks.

10. **Add `ChangeProfile` API endpoint** for multi-profile support.

### Low Priority (Backlog)

11. Tray icon properties (not applicable to web UI).
12. `Kstream` / `StreamServer` selection (upstream has these commented out or rarely used).
13. `GhUpdatePrereleases` (not applicable to web UI).
14. Queue UI sync methods (not applicable to web UI).

---

## 7. Method Mapping Quick Reference

### Fully Ported Methods (No Issues)

- `CrSeries.ParseSeriesById` -> `ParseSeriesByIdAsync`
- `CrSeries.SeriesById` -> `SeriesByIdAsync`
- `CrSeries.Search` -> `SearchAsync`
- `CrSeries.GetAllSeries` -> `GetAllSeriesAsync`
- `CrSeries.GetSeasonalSeries` -> `GetSeasonalSeriesAsync`
- `CrEpisode.ParseEpisodeById` -> `ParseEpisodeByIdAsync`
- `CrEpisode.MarkAsWatched` -> `MarkAsWatchedAsync`
- `CrEpisode.GetNewEpisodes` -> `GetNewEpisodesAsync`
- `History.CrUpdateSeries` -> `CrUpdateSeriesAsync`
- `History.UpdateWithEpisodeList` -> `UpdateWithEpisodeListAsync`
- `History.UpdateWithMusicEpisodeList` -> `UpdateWithMusicEpisodeListAsync`
- `History.SetAsDownloaded` -> `SetAsDownloadedAsync`
- `History.GetHistoryEpisode` -> `GetHistoryEpisodeAsync`
- `History.GetHistoryEpisodeWithDownloadDir` -> `GetHistoryEpisodeWithDownloadDirAsync`
- `History.GetHistoryEpisodeWithDubListAndDownloadDir` -> `GetHistoryEpisodeWithDubListAndDownloadDirAsync`
- `History.GetDubList` -> `GetDubListAsync`
- `History.GetSubList` -> `GetSubListAsync`
- `History.SortItems` -> `SortItemsAsync`
- `History.MatchHistorySeriesWithSonarr` -> `MatchHistorySeriesWithSonarrAsync`
- `History.MatchHistoryEpisodesWithSonarr` -> `MatchHistoryEpisodesWithSonarrAsync`
- `QueueManager.AddToQueue` -> `AddToQueue`
- `QueueManager.RemoveFromQueue` -> `RemoveFromQueue`
- `QueueManager.ClearQueue` -> `ClearQueue`
- `QueueManager.ReleaseDownloadSlot` -> `ReleaseDownloadSlot`
- `QueueManager.WaitForProcessingSlotAsync` -> `WaitForProcessingSlotAsync`
- `QueueManager.ReleaseProcessingSlot` -> `ReleaseProcessingSlot`
- `QueueManager.SetProcessingLimit` -> `SetProcessingLimit`
- `QueueManager.BlockAutoDownloadUntil` -> `BlockAutoDownloadUntil`
- `QueueManager.PumpQueue` -> `PumpQueueAsync`
- `CrAuth.Init` -> `Init`
- `CrAuth.AuthAnonymous` -> `AuthAnonymousAsync`
- `CrAuth.AuthOld` -> `LoginAsync`
- `CrAuth.ChangeProfile` -> `ChangeProfileAsync`
- `CrAuth.GetProfile` -> `GetProfileAsync`
- `CrAuth.GetSubscription` -> `GetSubscriptionAsync`
- `CrAuth.GetMultiProfile` -> `GetMultiProfileAsync`
- `CrAuth.LoginWithToken` -> `LoginWithTokenAsync`
- `CrAuth.RefreshToken` -> `RefreshTokenAsync` (bug fixed)
- `CrunchyrollManager.GetBase64EncodedTokenAsync` -> `GetBase64EncodedTokenAsync`

### Partially Ported Methods (Logic Missing)

- `CrSeries.GetSeasonDataById` -> `GetSeasonDataByIdAsync` (missing locale params)
- `CrunchyrollManager.DownloadEpisode` -> `DownloadEpisodeAsync` + `QueueService` (missing Sonarr vars, AD duplication, dub delays)
- `CrunchyrollManager.DownloadMediaList` -> `DownloadEpisodeAsync` (missing many features)
- `CrunchyrollManager.MuxStreams` -> `MuxFilesAsync` (missing Mp3, description, force muxer)
- `CrunchyrollManager.FetchPlaybackData` -> `GetPlaybackDataAsync` (missing endpoint merging)
- `CrunchyrollManager.TrySyncTimingFallbackAsync` -> Inline in `DownloadEpisodeAsync` (simplified)
- `History.UpdateWithEpisode` -> `UpdateWithEpisodeAsync` (oversimplified)
- `History.RefreshSeriesData` -> `RefreshSeriesDataAsync` (missing cache, artist handling)
- `QueueManager.TryStartDownload` -> `StartItem` (different model integration)
- `QueueManager.ScheduleRetry` -> `ScheduleRetry` (missing wake logic)
- `QueueManager.PumpQueue` -> `PumpQueueAsync` (missing resume handling)

### Missing Methods (Not Ported)

- `CrSeries.ItemSelectMultiDub`
- `CrSeries.ListSeriesId`
- `CrEpisode.EpisodeData`
- `CrEpisode.EpisodeMeta`
- `CrunchyrollManager.WaitForDubDownloadDelayAsync`
- `CrunchyrollManager.DownloadVideo`
- `CrunchyrollManager.DownloadAudio`
- `CrunchyrollManager.GetDownloadedDubs`
- `CrunchyrollManager.GetDownloadedSoftSubs`
- `History.RefreshExistingEpisodesFromBrowse`
- `History.GetSeriesThumbnail`
- `QueueManager.TryResumeDownload`
- `QueueManager.RestoreRetryStateFromQueue`
- `QueueManager.ScheduleRetryWake`
- `QueueManager.ReplaceQueue`
- `QueueManager.RestorePersistedQueue`
- `CrAuth.AuthAnonymousFoxy`

---

*End of Audit Report*
