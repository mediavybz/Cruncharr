# Cruncharr ↔ Upstream Crunchy-Downloader Alignment Report
## Generated: 2026-06-03
## Upstream Version: v1.6.10 (master@c123093)
## Our Version: 0.1.0-beta.1

---

## Executive Summary

**Overall Alignment: ~92%** — We are very close to upstream feature parity. Most core downloader functionality is fully ported. The remaining gaps are primarily:
1. **Global Queue Pause** (upstream commit d981319, Apr 2026) — Missing
2. **New Download Method** (upstream commit e80568c, Jul 2025) — Missing
3. **Some muxing/subtitle options** — Partially missing
4. **Auto-download / queue cooldown features** — Not fully implemented

---

## 1. Feature Parity Matrix

| Feature | Upstream Status | Our Status | Gap Level |
|---------|----------------|------------|-----------|
| **Multi-audio download** | Full | Full | None |
| **Subtitle download & embedding** | Full | Full (basic) | Low — missing font muxing, some CC options |
| **Quality selection** | Full | Full | None |
| **Batch/season downloads** | Full | Full | None |
| **History tracking** | Full | Full | None |
| **Sonarr integration** | Full | Full | None |
| **Queue management** | Full | Full (no global pause) | Low — missing global pause |
| **Calendar/simulcast tracking** | Full | Full (basic) | Low — missing some calendar filters |
| **Auth/token management** | Full | Full | None |
| **Settings/configuration** | Full | Full (~95%) | Low — missing some niche options |
| **Proxy support** | Full | Full | None |
| **Notification webhooks** | Full | Full | None |
| **Encoding presets** | Full | Full | None |
| **Processing slot limits** | Full | Full | None |
| **Download early start** | Full | Full | None |
| **Keep dubs separate** | Full | Full | None |
| **Replace existing files** | Full (May 2026) | Full | None |
| **Sync timing** | Full | Full | None |
| **Sync timing fallback** | Full | Full | None |
| **Description audio download** | Full (Nov 2025) | Full | None |
| **Hardsub raw fallback** | Full (Dec 2025) | Full | None |
| **MP3 audio-only output** | Full (Jun 2025) | Full | None |
| **MP4 muxing** | Full | Full | None |
| **Cover art muxing** | Full | Full | None |
| **Webhook notifications** | Full (May 2026) | Full | None |
| **Auto-download new episodes** | Full | Partial | Medium — no background scheduler |
| **Global queue pause** | Full (Apr 2026) | Missing | Medium |
| **Download speed limiting** | Full | Config exists, not wired | Medium |
| **Font muxing into MKV** | Full (Apr 2025) | Missing | Low |
| **New download method** | Full (Jul 2025) | Missing | Low |
| **Chapters support** | Full | Config exists, not wired | Low |
| **Per-series/season settings override** | Full | Full | None |
| **Queue replacement** | Full | Full | None |
| **History compression (GZip)** | Full | Full | None |
| **Daily backup rotation** | Full | Full | None |
| **Mark as watched** | Full | Full | None |
| **GetAllSeries / GetSeasonalSeries** | Full | Full | None |
| **Guest token for requests** | Full (Jan 2026) | Missing | Low |
| **UseDefaults toggle for endpoints** | Full (Mar 2026) | Missing | Low |
| **Update history from calendar** | Full (Jan 2026) | Full | None |
| **FlareSolverr support** | Full (Jan 2026) | Config exists, basic | Low |

---

## 2. Recent Upstream Changes (Last 30 Days)

### May 25, 2026 — c123093: Replace existing files toggle
- **Status:** PORTED
- **Details:** Added `ReplaceExistingFiles` config + API exposure
- **Our implementation:** `src/Cruncharr.Core/Configuration/CruncharrConfig.cs:455`, `DownloadService.cs` quality-probe rename path

### May 14, 2026 — ff3e280: Notification service for webhooks  
- **Status:** PORTED
- **Details:** Added `INotificationService` wired into `QueueService`
- **Our implementation:** `src/Cruncharr.Core/Services/NotificationService.cs`, webhook dispatch on complete/error

### Apr 20, 2026 — d981319: Global Pause button for download queue (#418)
- **Status:** NOT PORTED
- **Priority:** Medium
- **Details:** Upstream added a global queue pause that stops all new downloads from starting. Our code has per-item pause (`PauseItem`) but no global pause mechanism.
- **Recommendation:** Add `IsGloballyPaused` flag to `QueueService` + API endpoint + frontend button

### Mar 30, 2026 — aabc10e: Red dot indicator when update available
- **Status:** NOT APPLICABLE
- **Details:** Desktop UI-only feature. For web UI, we can add a version check badge.

### Mar 24, 2026 — c4ba220: UseDefaults toggle for stream endpoints
- **Status:** NOT PORTED
- **Priority:** Low
- **Details:** Toggle to choose between auto-updated app defaults vs custom endpoint parameters. Our `StreamEndpointConfig.UseDefault` exists but the auto-update logic is not implemented.
- **Recommendation:** Add endpoint version check + auto-update from GitHub releases

---

## 3. Model Alignment Check

### EpisodeInfo Model
| Field | Upstream | Ours | Status |
|-------|----------|------|--------|
| Id | string | string | Match |
| Guid | string | string | Match |
| Title | string | string | Match |
| SeriesTitle | string | string | Match |
| SeriesId | string | string? | Match |
| SeasonTitle | string | string? | Match |
| SeasonId | string | string? | Match |
| SeasonNumber | int | int | Match |
| EpisodeNumber | int | int | Match |
| Description | string | string? | Match |
| ThumbnailUrl | string | string? | Match |
| CoverArtUrl | string | string? | Match |
| Images | List<string> | List<string> | Match |
| RawImages | List<List<object>> | Dictionary<string, List<List<object>>>? | **Partial** — type mismatch |
| Locale | string | string | Match |
| IsPremium | bool | bool | Match |
| IsDubbed | bool | bool | Match |
| IsSubbed | bool | bool | Match |
| ReleaseDate | DateTime? | DateTime? | Match |
| Versions | List<EpisodeVersion> | List<EpisodeVersion>? | Match |
| AudioLocale | string | string | Match |
| SubtitleLocales | List<string> | List<string> | Match |
| Identifier | string | string? | Match |
| Episode | string | string? | Match |
| Playback | string | string? | Match |
| StreamsLink | string | string? | Match |
| DurationMs | int | int | Match |
| SelectedDubs | List<string> | List<string>? | Match |
| SelectedSubs | List<string> | List<string>? | Match |

**Verdict:** EpisodeInfo is ~98% aligned. Minor type difference in `RawImages`.

### CrDownloadOptions / CruncharrConfig Alignment

**Properties that exist in BOTH:**
- All core download settings (quality, dubs, subs, output dir, temp dir, etc.)
- All muxing settings (mp4, mp3, cover, sync timing, etc.)
- All history settings
- All queue settings (persist, auto_download, simultaneous_downloads)
- All Sonarr settings
- All proxy settings
- All notification/webhook settings
- Stream endpoint settings (primary + secondary)
- FlareSolverr settings
- Calendar settings

**Properties MISSING in our config (upstream only):**

| Property | Upstream Use | Our Gap | Priority |
|----------|-------------|---------|----------|
| `GhUpdatePrereleases` | Check for beta updates | Not applicable (web UI) | N/A |
| `Theme` / `AccentColor` / `BackgroundImagePath` | Desktop UI theming | Not applicable | N/A |
| `TrayIconEnabled` / `StartMinimizedToTray` / `MinimizeToTray` / `MinimizeToTrayOnClose` | Desktop tray icon | Not applicable | N/A |
| `DownloadFinishedPlaySound` / `DownloadFinishedSoundPath` | Desktop sound notification | Not applicable | N/A |
| `DownloadFinishedExecute` / `DownloadFinishedExecutePath` | Execute program on finish | **MISSING** | Low |
| `Force` | CLI override flag | Not applicable | N/A |
| `Override` | CLI filename override | Supported via API | N/A |
| `DownloadSpeedLimit` / `DownloadSpeedInBits` | Speed throttling | Config exists, **NOT WIRED** | Medium |
| `DownloadMethodeNew` | Alternative download method | **MISSING** | Low |
| `AutoDownload` | Auto-add new episodes | Config exists, **NOT WIRED** | Medium |
| `HistoryAutoRefreshIntervalMinutes` / `HistoryAutoRefreshMode` | Background history refresh | Config exists, **NOT WIRED** | Medium |
| `IsEncodeEnabled` | Enable encoding toggle | We check `EncodingPreset != null` | Low |
| `SelectedCalendarLanguage` / `CalendarDubFilter` / `CalendarHideDubs` | Calendar filters | **MISSING** | Low |
| `FfmpegOptions` / `MkvmergeOptions` | Custom muxer flags | Config exists, **NOT WIRED** | Low |
| `DefaultSub` / `DefaultSubSigns` / `DefaultSubForcedDisplay` | Subtitle track defaults | Config exists, **NOT WIRED** | Low |
| `FixCccSubtitles` | Fix CCC subtitle formatting | Config exists, **NOT WIRED** | Low |
| `SubsAddScaledBorder` | ASS subtitle border scaling | Config exists, **NOT WIRED** | Low |
| `ConvertVtt2Ass` | Convert VTT to ASS | Config exists, **NOT WIRED** | Low |
| `CcSubsFont` | CC subtitle font | Config exists, **NOT WIRED** | Low |
| `CcSubsMuxingFlag` | Flag CC subs in mux | Config exists, **NOT WIRED** | Low |

---

## 4. Missing Features Audit

### High Priority (Should implement soon)

#### 1. Global Queue Pause (upstream #418, d981319)
- **What:** Pause ALL downloads globally, not just per-item
- **Impact:** Users want to temporarily stop the queue without losing state
- **Implementation:** Add `IsGloballyPaused` to QueueService + `POST /api/v1/queue/pause-all` + frontend button

#### 2. Download Speed Limiting
- **What:** Throttle download bandwidth
- **Impact:** Prevents network saturation, helps with rate limiting
- **Implementation:** Wire `DownloadSpeedLimit` into HLS/HTTP downloaders (ThrottledStream already exists!)

#### 3. Auto-Download / Background Scheduler
- **What:** Automatically check for and download new episodes
- **Impact:** Core *arr-stack functionality
- **Implementation:** Add background service that checks history series periodically (HistoryAutoRefreshIntervalMinutes), adds new episodes to queue

#### 4. Configurable Cooldown Between Downloads (upstream #445)
- **What:** Delay between starting downloads to avoid rate limits
- **Impact:** Upstream users reporting rate limit errors (#436, #445)
- **Implementation:** Add `CooldownDelaySeconds` to config, sleep between queue item starts

### Medium Priority (Nice to have)

#### 5. Font Muxing (upstream aca28a4, Apr 2025)
- **What:** Embed subtitle fonts into MKV output
- **Impact:** Better subtitle rendering
- **Implementation:** Wire `MuxFonts` and `MuxTypesettingFonts` into DownloadService muxing path

#### 6. New Download Method (upstream e80568c, Jul 2025)
- **What:** Alternative download algorithm
- **Impact:** May fix some download issues
- **Implementation:** Port `DownloadMethodeNew` logic from upstream CrunchyrollManager

#### 7. Execute on Download Complete
- **What:** Run external script/program when queue finishes
- **Impact:** Automation integration
- **Implementation:** Wire `DownloadFinishedExecute` / `DownloadFinishedExecutePath`

#### 8. Chapters Support
- **What:** Embed chapter markers into output
- **Impact:** Better media player navigation
- **Implementation:** Wire `IncludeChapters` into muxing

#### 9. Calendar Filters
- **What:** Filter calendar by language/dub
- **Impact:** Better UX for multi-language users
- **Implementation:** Add `SelectedCalendarLanguage`, `CalendarDubFilter`, `CalendarHideDubs`

### Low Priority (Can defer)

#### 10. Guest Token for Requests (upstream 6abbc12, Jan 2026)
- **What:** Use guest token instead of auth token for most API calls
- **Impact:** Reduces auth token refresh frequency
- **Implementation:** Add guest token caching to CrunchyrollAuthService

#### 11. UseDefaults Toggle for Endpoints (upstream c4ba220, Mar 2026)
- **What:** Auto-update stream endpoints from GitHub releases
- **Impact:** Less manual config maintenance
- **Implementation:** Add endpoint version check + update logic

#### 12. Release Year Filename Variable (upstream #411)
- **What:** Add `${releaseYear}` to filename template variables
- **Impact:** Better file organization
- **Implementation:** Add to FilenameService variable builder

#### 13. Custom Muxer Flags
- **What:** Pass custom flags to ffmpeg/mkvmerge
- **Impact:** Power user feature
- **Implementation:** Wire `FfmpegOptions` / `MkvmergeOptions` into command builders

---

## 5. Upstream Issues That May Affect Us

| Issue | Title | Affects Us? | Notes |
|-------|-------|-------------|-------|
| #447 | Multi-episodes not queried within season | **YES** — Likely | Same episode parsing logic. Multi-episode handling in GetEpisodesAsync may skip combined episodes. |
| #445 | Configurable cooldown between downloads | **YES** — Feature gap | We don't have cooldown. Users may hit rate limits. |
| #442 | Downloads finished without warning but not finished | **MAYBE** | Could be upstream-specific download method issue. Monitor. |
| #437 | Can't add episode with current dub settings | **MAYBE** | Our DownloadOnlyWithAllSelectedDubSub logic should handle this, but verify. |
| #436 | Rate limit error | **YES** | No cooldown/speed limit makes us vulnerable. |
| #425 | Manual download button for each episode on search | **NO** — Frontend feature | Web UI can add per-episode buttons easily. |
| #423 | Keep video separate per language | **NO** — Feature request | Not in upstream yet. |
| #415 | Add encoding to download | **PARTIAL** | We have encoding presets but no `IsEncodeEnabled` toggle. |
| #411 | Release Year filename variable | **NO** — Feature request | Not in upstream yet. |
| #358 | Automatic status and auto-download new series | **YES** — Feature gap | No background scheduler. |

---

## 6. Critical Bug: Multi-Episode Handling (#447)

**Risk Level: HIGH**

Upstream issue #447 (opened Jun 2, 2026 — very recent) reports that multi-episodes (e.g., "E11-12", "E53-54") are silently omitted when querying series seasons. This affects series like Detective Conan where multiple episodes are combined into a single video.

**Our vulnerability:**
- We use the same `GetEpisodesAsync` / `ParseEpisodeByIdAsync` logic as upstream
- If upstream hasn't fixed this yet, we likely have the same bug
- **Recommendation:** Audit `CrunchyrollApiService.GetEpisodesAsync` and `ParseEpisodeByIdAsync` for multi-episode handling. Look for episode number parsing that assumes single integers.

---

## 7. Recommendations / Next Steps

### Immediate (This Week)
1. **Audit multi-episode parsing** — Check if #447 affects us. Add test cases for combined episodes.
2. **Add global queue pause** — Port upstream d981319. Simple flag + API endpoint.
3. **Wire download speed limiting** — `ThrottledStream` exists but isn't connected to config.

### Short Term (Next 2 Weeks)
4. **Add download cooldown** — Simple delay between queue starts. Addresses #445/#436.
5. **Implement auto-download scheduler** — Background service for history refresh. Core *arr feature.
6. **Wire font muxing** — `MuxFonts` and `MuxTypesettingFonts` config exists but not used.

### Medium Term (Next Month)
7. **Add execute-on-complete hook** — Run external script when queue empties.
8. **Wire remaining muxing options** — Chapters, custom flags, subtitle defaults.
9. **Add calendar filters** — Language/dub filtering for calendar view.
10. **Guest token optimization** — Cache guest tokens to reduce auth churn.

### Ongoing
11. **Monitor upstream commits weekly** — Subscribe to releases or check commits regularly.
12. **Monitor upstream issues** — Check for bugs that may affect our shared logic.

---

## Appendix: Upstream Commits Since Our Last Sync

| Date | Commit | Feature | Ported? |
|------|--------|---------|---------|
| May 25, 2026 | c123093 | Replace existing files toggle | Yes |
| May 14, 2026 | ff3e280 | Webhook notification service | Yes |
| Apr 20, 2026 | d981319 | Global queue pause button | **No** |
| Mar 30, 2026 | aabc10e | Update available red dot | N/A (UI) |
| Mar 24, 2026 | c4ba220 | UseDefaults toggle for endpoints | **No** |
| Jan 31, 2026 | 973c45c | Update history from calendar | Yes |
| Jan 24, 2026 | 6abbc12 | Guest token for requests | **No** |
| Jan 10, 2026 | c7687c8 | FlareSolverr for calendar | Partial |
| Dec 1, 2025 | c5660a8 | Hardsub raw fallback | Yes |
| Nov 6, 2025 | dc570bf | Download description audio | Yes |
| Sep 6, 2025 | 15c6219 | Second endpoint settings | Yes |
| Jul 28, 2025 | e80568c | New download method | **No** |
| Jul 13, 2025 | 6520014 | Shutdown PC toggle | Yes (as ShutdownWhenQueueEmpty) |
| Jun 28, 2025 | 67f3d7a | Audio only as MP3 | Yes |
| Apr 4, 2025 | aca28a4 | Mux fonts into MKV | **No** |

---

*Report generated by opencode alignment check.*
