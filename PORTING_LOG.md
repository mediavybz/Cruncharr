# Porting Log
## Project: Crunchy-Downloader → Docker + Web UI
## Desktop Source Version: upstream/master 245cf78 (synced 2026-06-12)
## Last Updated: 2026-07-17 (Round 41 in progress: canonical Sonarr naming reliability)

---

## Round 41 — Canonical Sonarr Naming Reliability (2026-07-17)

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.Core.Tests/SonarrServiceTests.cs | Existing Sonarr v3 series/episode/naming read contracts | [PT] Added generic guards for normalized CleanTitle matching, concurrent series-read coalescing, transient connection-reset retry, episode-list cache reuse, and the existing `/api/v3/config/naming` response; cases use canonical IDs/titles rather than episode-specific substitutions | 2026-07-17 |
| src/Cruncharr.Core.Tests/PortedGapTests.cs | Sonarr `FileNameBuilder` title replacement plus existing desktop Sonarr filename/folder identity | [PT] Added generic guards for all built-in colon replacement modes, Sonarr bad-character behavior, arbitrary repeated hyphen/dot/underscore collapse, matched series/episode identity, no-silent-fallback handling for saved matches, the explicit Crunchyroll-series alias, configured season/special folder formats, and long-name suffix preservation after title transformation | 2026-07-17 |
| src/Cruncharr.Core/Services/SonarrService.cs | Existing Sonarr v3 series/episode reads + Sonarr `NamingConfigResource`/`FileNameBuilder` contracts | [PT] Coalesces metadata cache misses, retries only transient transport/408/429/5xx failures, reuses fresh episode-list identities and last-known-good metadata, normalizes punctuation for exact title matching, and reads the existing naming configuration so concurrent downloads cannot silently lose canonical Sonarr identity after a connection reset | 2026-07-17 |
| src/Cruncharr.Core/Services/FilenameService.cs | Sonarr `FileNameBuilder.CleanFileName` + existing desktop Sonarr filename variables | [PT] Matched `{Series Title}`, `{Episode Title}`, and explicit Sonarr-title tokens now use canonical Sonarr identity, its configured illegal-character/colon replacements, and repeated-separator collapse; `{crSeriesTitle}` preserves an explicit Crunchyroll-title choice, and unmatched/disabled flows remain unchanged | 2026-07-17 |
| src/Cruncharr.Core/Services/DownloadService.cs | Existing saved Sonarr episode resolution and Sonarr/Plex output organization | [PT] Carries the fetched naming configuration through initial/resolution-corrected filenames, passes the transformed episode title to length limiting so quality suffixes survive, uses Sonarr's configured season/special folder format with its canonical series path, and applies a nullable-safe guarded retryable deferral instead of silently writing a fallback name when a saved Sonarr identity cannot be resolved | 2026-07-17 |
| src/Cruncharr.API/Cruncharr.API.csproj | N/A (testing release metadata) | [PT] Bumped assembly, file, and package version 1.0.56 → 1.0.57 for the canonical Sonarr-naming testing image; framework and dependencies unchanged | 2026-07-17 |
| src/Cruncharr.API/Controllers/HealthController.cs | Existing API release metadata response | [PT] Aligned the no-attribute fallback version with 1.0.57; route, response shape, and health logic unchanged | 2026-07-17 |

### Verification
- Pre-change Sonarr/naming/history baseline: 46/46 passing (Release).
- Post-fix targeted Sonarr/naming/history suite: 65/65 passing (Release).
- Full post-fix Release suite: 206/206 passing.
- Warning-as-error solution build, warning-level analyzer verification, and `git diff --check` passed.
- Post-version verification repeated 206/206 tests, zero-warning build/analyzers, frontend JavaScript syntax, exact 1.0.57 release references, and whitespace checks successfully.
- Dockerfile check completed with no warnings; cache-only linux/amd64 and linux/arm64 builds completed from the 1.0.57 working source; Compose and shell syntax checks passed.
- Loaded linux/amd64 smoke became healthy at `1.0.57+local-naming-audit`, served exactly one 1.0.57 CSS/JavaScript key plus the corrected Sonarr naming help, ran PID 1 as UID/GID 1234, linked without missing libraries, and stopped gracefully with exit code 0.

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Date |
|------|-------------------|-------------------|------|
| src/Cruncharr.API/wwwroot/js/app.js | Filename template help and Sonarr naming/TVDB-numbering setting | Existing GET/POST `/api/v1/config` | Updated the existing setting description to state canonical series path/title, naming replacements, season folder format, retry-without-fallback behavior, and the explicit Crunchyroll/Sonarr title tokens; no configuration contract or component change | 2026-07-17 |
| src/Cruncharr.API/wwwroot/index.html | Existing web release asset refresh | none | Bumped aligned CSS and JavaScript cache keys 1.0.56 → 1.0.57 so browsers cannot retain the pre-fix naming/settings script | 2026-07-17 |

### API Contract
- No Cruncharr API route, request, response-shape, or status-code changes planned.

### Status (Round 41)
- In progress on `testing`.

---

## Round 40 — Language Metadata and Track Audit (2026-07-17)

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.Core.Tests/DownloadVersionResolutionTests.cs | CRD/Downloader/Crunchyroll/CrQueue.cs : 126-143; CRD/Downloader/Crunchyroll/CrunchyrollManager.cs : 1316-1338 | [PT] Added regression specifications for authoritative refreshed subtitle locales, sentinel/SkipSubs/first-available validation, selected audio/history/filename locale, explicit HLS multi-dub, AD-role metadata refresh/mapping/selection, and AD mux-file recognition across HLS/DASH/encrypted names | 2026-07-17 |
| src/Cruncharr.Core/Services/DownloadService.cs | CRD/Downloader/Crunchyroll/CrQueue.cs : 126-143; CRD/Downloader/Crunchyroll/CrunchyrollManager.cs : 1316-1338, 1444-1452 | [PT] Existing metadata/artwork refresh now carries authoritative subtitle locales and refetches version roles for AD; validation honors sentinel/SkipSubs/first-available rules with a typed terminal failure; selected playback updates audio/history/filename locales; explicit multi-dub is consistent in DASH/HLS; subtitle matching is case-insensitive with late original full-dialogue union in both paths; real same-locale AD uses its GUID and `description` role with collision-free names and mux metadata | 2026-07-17 |
| src/Cruncharr.Core/Services/CrunchyrollApiService.cs | CRD/Downloader/Crunchyroll/CrunchyrollManager.cs : 1321-1338; CRD episode/series language selection | [PT] Deserializes version `roles` and uses one complete EpisodeVersion mapper across episode, series, and queue metadata so media GUIDs and AD roles are never dropped; locale lookup and selected-dub filtering are case-insensitive across both multi-download paths; API routes and response contracts unchanged | 2026-07-17 |
| src/Cruncharr.Core/Models/DownloadModels.cs | CRD/Downloader/Crunchyroll/CrQueue.cs : 132-143 | [PT] Added an internal MissingLanguage failure classification so a deterministic desktop-style language rejection can terminate cleanly instead of being mistaken for a transient network/rate-limit failure | 2026-07-17 |
| src/Cruncharr.Core/Services/QueueService.cs | CRD/Downloader/Crunchyroll/CrQueue.cs : 132-143 | [PT] Preserves typed DownloadResult failures when entering queue error handling and classifies missing-language rejection with the existing non-retryable account/content failures; real transient network/rate-limit failures retain configured retry behavior | 2026-07-17 |
| src/Cruncharr.Core.Tests/QueuePumpEligibilityTests.cs | CRD/Downloader/Crunchyroll/CrQueue.cs : 132-143 | [PT] Added a queue retry-classification guard proving missing-language rejection is terminal while actual rate-limit and network failures remain retryable | 2026-07-17 |
| src/Cruncharr.Core.Tests/PortedGapTests.cs | CRD/Downloader/Crunchyroll/CrunchyrollManager.cs : 1345-1355; CRD/Utils/Languages.cs locale mapping | [PT] Added FFmpeg/mkvmerge guards requiring normal-before-AD defaults and `[AD]` naming, plus language lookup coverage proving case variants resolve to the canonical locale instead of `und` | 2026-07-17 |
| src/Cruncharr.Core.Tests/HistoryDownloadRecordTests.cs | CRD/Utils/Structs/History/HistorySeries.cs language availability tracking | [PT] Added a guard proving case-only locale differences do not falsely mark an already-downloaded dub/subtitle as newly available in History | 2026-07-17 |
| src/Cruncharr.Core/Models/HistoryModels.cs | CRD/Utils/Structs/History/HistorySeries.cs language availability tracking | [PT] Compares available/downloaded dub and subtitle locales case-insensitively when calculating HasNewEpisodes, matching the existing partial-download helpers and preventing duplicate language state | 2026-07-17 |
| src/Cruncharr.Core/Services/HistoryService.cs | CRD/Utils/Structs/History/HistorySeries.cs episode language refresh | [PT] Deduplicates refreshed available dub/subtitle locales case-insensitively and compares original/current audio locales case-insensitively before deciding whether a full series refresh is required | 2026-07-17 |
| src/Cruncharr.Core/Utils/Languages.cs | CRD/Utils/Languages.cs locale mapping | [PT] Resolves Crunchyroll and short locale identifiers and language sort priority case-insensitively, preventing valid API/config case variants from being labeled `und`; canonical language objects and codes are unchanged | 2026-07-17 |
| src/Cruncharr.Core/Utils/Muxing/Commands/FFmpegCommandBuilder.cs | CRD/Downloader/Crunchyroll/CrunchyrollManager.cs : 1349-1355; CRD/Utils/Muxing/Merger.cs desktop AD metadata | [PT] Emits the desktop `[AD]` audio title and prevents a description-role track from receiving FFmpeg default disposition even when it shares the chosen dub's language code | 2026-07-17 |
| src/Cruncharr.Core/Utils/Muxing/Commands/MkvMergeCommandBuilder.cs | CRD/Downloader/Crunchyroll/CrunchyrollManager.cs : 1349-1355; CRD/Utils/Muxing/Merger.cs desktop AD metadata | [PT] Keeps the existing `[AD]` title and now prevents a description-role track from receiving mkvmerge default status when normal and AD tracks share the selected language | 2026-07-17 |
| src/Cruncharr.API/Cruncharr.API.csproj | N/A (testing release metadata) | [PT] Bumped assembly, file, and package version 1.0.55 → 1.0.56 for the audited language-fix testing image; framework and dependencies unchanged | 2026-07-17 |
| src/Cruncharr.API/Controllers/HealthController.cs | Existing API release metadata response | [PT] Aligned the no-attribute fallback version with 1.0.56; route, response shape, and health logic unchanged | 2026-07-17 |

### Verification
- Pre-change targeted language suite: 58/58 passing (Release).
- Post-fix targeted language/history/queue/mux suite: 76/76 passing (Release).
- Full Release suite: 187/187 passing; warning-as-error solution build and warning-level analyzer verification are clean.
- Frontend JavaScript syntax, exact 26-locale backend/web catalog parity, Compose config, Git-Bash syntax for both shell scripts, exact 1.0.56 release references, and `git diff --check` passed.
- Dockerfile check completed with no warnings; cache-only linux/amd64 and linux/arm64 builds completed from the 1.0.56 working source.
- Loaded linux/amd64 smoke returned `1.0.56+local-language-audit`, served exactly one 1.0.56 CSS/JavaScript key, contained all four restored web locales, contained no hard-coded false rate-limit label, linked the apphost without missing libraries, retained FFmpeg N-125649-g8d394252d8, became Docker healthy, ran PID 1 as UID/GID 1234, and stopped gracefully with exit code 0.
- Freshly pulled GHCR testing image became Docker healthy and returned `1.0.56+d9baf9993fbe23357fe03aa6efa44c8c4b7b3020`; it served exactly one 1.0.56 CSS and JavaScript key, contained all restored web locales and no hard-coded false rate-limit label, ran PID 1 as UID/GID 1234, linked without missing libraries, retained FFmpeg N-125649-g8d394252d8, and stopped gracefully with exit code 0.

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Date |
|------|-------------------|-------------------|------|
| src/Cruncharr.API/wwwroot/js/app.js | Desktop Crunchyroll language catalog and queue retry status | Existing GET/POST `/api/v1/config`, GET `/api/v1/queue`, queue SSE | Added the four desktop-supported locales missing from web settings (`en-IN`, `ca-ES`, `zh-HK`, `zh-TW`) and replaced the hard-coded “Rate limited” retry label with the queue's actual failure reason plus retry time | 2026-07-17 |
| src/Cruncharr.API/wwwroot/index.html | Existing web release asset refresh | none | Bumped aligned CSS and JavaScript cache keys 1.0.55 → 1.0.56 so browsers cannot retain the pre-fix language/status script | 2026-07-17 |

### API Contract
- No API route, request, response-shape, or status-code changes.

### Status (Round 40)
- Complete on `testing`.

### Release Status (Round 40)
- Testing release version: 1.0.56.
- Source commit: `d9baf99` (`fix(download): preserve language metadata`).
- `testing` pushed to the source commit.
- Multi-architecture image pushed to `ghcr.io/mediavybz/cruncharr:testing`.
- Registry index digest: `sha256:5c7b2e779dc90f0ad02330ad0f0ee3383af170e09a8346c4a92583f10b786845`.
- linux/amd64 manifest: `sha256:688ec183ef26402d8362a57200827de1fbfe28440b98ea57af7dd9338f7720ec`; linux/arm64 manifest: `sha256:b933c6468526f658e7f2dd327805460daad9e7a2b790b4384aedc6965e7557d8`; both include provenance attestations.
- Stable `master`, `:latest`, and version tags were not changed.

---

## Round 39 — Full-Stack Verification and Docker Cache Optimization (2026-07-17)

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.API/Cruncharr.API.csproj | N/A (testing release metadata) | [PT] Bumped assembly, file, and package version 1.0.54 → 1.0.55 for the cache-optimized testing image; no framework, dependency, or application behavior change | 2026-07-17 |
| src/Cruncharr.API/Controllers/HealthController.cs | Existing API release metadata response | [PT] Aligned the no-attribute fallback version with 1.0.55; route, response shape, status, and health logic unchanged | 2026-07-17 |

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Date |
|------|-------------------|-------------------|------|
| src/Cruncharr.API/wwwroot/index.html | Existing web UI release asset refresh | none | Bumped CSS and JavaScript cache keys 1.0.54 → 1.0.55 so browsers cannot retain mixed release assets | 2026-07-17 |

### Infrastructure
| File | Purpose | Date |
|------|---------|------|
| Dockerfile | [PT] Moved the stage-scoped `SOURCE_REVISION` declaration below the stable project-manifest restore layer, so a new source commit invalidates publish metadata without forcing both architecture-specific NuGet restores to run again; image inputs and runtime behavior unchanged | 2026-07-17 |

### Verification
- Pulled-image inspection and smoke completed successfully.
- Repeat-build cache proof passed: changing only `SOURCE_REVISION` kept the architecture-specific restore instruction cached and reran only publish. The already-completed first revision resolved in 0.62 seconds; the second revision completed its publish in 40.11 seconds without either NuGet restore.
- Baseline before edits: 168/168 Release tests passed; warning-as-error build and 151–211 analyzer sets reported zero warnings; NuGet reported no vulnerable or deprecated direct/transitive packages.
- Firefox 592 px and 1440 px live checks covered all nine routes plus History poster/table/detail views and the mobile More sheet; document/content widths matched their clients with no clipped descendants. Mocked distinct series-cover and episode-shot inputs proved History cards used the series cover.
- Final restored Release test suite passed 168/168; warning-as-error build and analyzer verification remained clean. Exact release-reference, frontend JavaScript, Dockerfile check, Compose config, shell syntax, whitespace, repository integrity, and secret/pattern checks passed.
- Complete cache-only linux/amd64 and linux/arm64 images built from 1.0.55 source. The loaded amd64 candidate returned healthy at `1.0.55+local-audit`, served exactly one 1.0.55 CSS and JavaScript key, reached Docker healthy state, ran as UID/GID 1234, linked all native binaries, preserved FFmpeg N-125649-g8d394252d8, and shut down gracefully in 0.92 seconds with exit code 0.
- Trivy 0.72.0 found 0 fixable high/critical vulnerabilities in the 1.0.55 candidate. GitHub repository triage found no open issues or pull requests.
- Pulled GHCR testing image repeated the clean Trivy result and returned `1.0.55+8321049db257c660b6c96d3d23d14afd4849bc9d`; Firefox at 592 px confirmed exact document/content widths, two 274 px History columns, the series cover on every card, and no episode-thumbnail leak. Runtime health, UID/GID 1234, native linkage, FFmpeg revision, 1.0.55 assets, graceful 0.44-second shutdown, and exit code 0 passed.

### API Contract
- No API route, request, response-shape, or status-code changes.

### Status (Round 39)
- Complete on `testing`.

### Release Status (Round 39)
- Testing release version: 1.0.55.
- Source commit: `8321049` (`perf(docker): preserve restore cache`).
- `testing` pushed to the source commit.
- Multi-architecture image pushed to `ghcr.io/mediavybz/cruncharr:testing`.
- Registry index digest: `sha256:24dc07551887b7e518e8c0ba2c645246f208d110b17a76af0a71edaf51d2f7a5`.
- linux/amd64 manifest: `sha256:0f5a763230e02411332c80cd3b8fa5f5891a2157b844c751342e5ae5b049ec77`; linux/arm64 manifest: `sha256:71fb8912f10cb1535dbd6382e0257f5fbb952d4eeb9469b21db104cc5338d9de`; both include provenance attestations.
- Stable `master`, `:latest`, and version tags were not changed.

---

## Round 38 — Docker Dependency Reproducibility and Scan Follow-up (2026-07-17)

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.API/Cruncharr.API.csproj | N/A (testing release metadata) | [PT] Bumped assembly, file, and package version 1.0.53 → 1.0.54 for the checksum-enforced testing image; no application behavior or dependency change | 2026-07-17 |
| src/Cruncharr.API/Controllers/HealthController.cs | Existing API release metadata response | [PT] Aligned the no-attribute fallback version with 1.0.54; route, response shape, and status unchanged | 2026-07-17 |

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Date |
|------|-------------------|-------------------|------|
| src/Cruncharr.API/wwwroot/index.html | Existing web UI release asset refresh | none | Bumped CSS and JavaScript cache keys 1.0.53 → 1.0.54 so browsers cannot retain mixed release assets | 2026-07-17 |

### Infrastructure
| File | Purpose | Date |
|------|---------|------|
| Dockerfile | [PT] Replaced floating .NET 8 and Debian Bookworm tags with verified exact version/date tags; replaced the rolling FFmpeg `latest` asset with the dated N-125649-g8d394252d8 release and per-architecture SHA-256 checks; replaced Bento4 HEAD cloning with the exact b8c50a0 commit archive and its SHA-256 check; removed `git` from the native builder; accepts the release source revision as an MSBuild property so container-built informational versions retain commit identity without copying `.git` | 2026-07-17 |
| publish-docker.sh | [PT] Resolves the current Git HEAD (or honors explicit `SOURCE_REVISION`) and passes it to BuildKit so the container-built API/CLI informational version identifies the source commit; keeps cache-only validation and explicit publication behavior unchanged | 2026-07-17 |

### Verification
- Verified exact tags resolve to .NET SDK 8.0.423 manifest `sha256:89ce6291bde9acdf59594e79fb8277c6d84c46e4b1f5bf126a4f18766e4bd597` and Debian Bookworm 20260713 slim index `sha256:7b140f374b289a7c2befc338f42ebe6441b7ea838a042bbd5acbfca6ec875818`.
- Checksum-enforced linux/amd64 and linux/arm64 builds completed through `bash publish-docker.sh`; the pinned AMD64 inputs reproduced the previously validated final image manifest before the version bump.
- Full Release test suite: 168/168 passing.
- 1.0.54 candidate smoke: health returned healthy at 1.0.54; HTML served one 1.0.54 CSS key and one 1.0.54 JavaScript key; Docker health became healthy; effective process user was 1234:1234; native linkage and graceful shutdown passed.
- Revision-aware release-helper build returned `1.0.54+<full Git SHA>` from the health endpoint, preserving published-image source traceability without adding `.git` to the Docker context.
- Trivy 0.72.0 scan found 0 fixable high/critical vulnerabilities. The unfiltered scan reported 29 inherited Debian advisories whose statuses provide no fixed Bookworm version (`affected`, `fix_deferred`, or `will_not_fix`).
- Dockerfile check reported no warnings; Compose config, frontend JavaScript syntax, shell syntax, and `git diff --check` passed.

### API Contract
- No API route, request, response-shape, or status-code changes.

### Status (Round 38)
- Complete on `testing`.

### Release Status (Round 38)
- Testing release version: 1.0.54.
- Source commit: `74a69e1` (`build(docker): make builds reproducible`).
- `testing` pushed to the source commit.
- Multi-architecture image pushed to `ghcr.io/mediavybz/cruncharr:testing`.
- Registry index digest: `sha256:71a7c30ae90fbef1e55102825aa250973f8ec12e134f150b68c1febcd0cf7985` (linux/amd64 + linux/arm64, with attestations).
- Pulled-image smoke passed at `1.0.54+74a69e1d358ebad583c6f5337358af77287a03a1`; health and index returned 200 with both 1.0.54 cache keys; Development Swagger returned 200; FFmpeg remained N-125649-g8d394252d8; effective API user remained 1234:1234.
- Stable `master`, `:latest`, and version tags were not changed.

---

## Round 37 — Docker Build Engineering Audit (2026-07-17)

### Infrastructure
| File | Purpose | Date |
|------|---------|------|
| Dockerfile | [PT] Moved the existing self-contained API/CLI publish from ignored host-generated `docker-build` artifacts into a .NET 8 SDK build stage on `BUILDPLATFORM`; restores project manifests with the same single-file/trimming properties used by the no-restore publish so linker assets remain present and the released API size is preserved; cross-publishes the existing linux-x64/linux-arm64 runtime identifiers; preserved the Debian slim runtime, native tools, paths, entrypoint, volumes, port, and health check | 2026-07-17 |
| .dockerignore | [PT] Excluded obsolete host publish output plus local skill examples, test sources, and deployment templates that no Dockerfile stage consumes, reducing the build context without hiding production inputs | 2026-07-17 |
| publish-docker.sh | [PT] Replaced host-side dual-RID publish/delete logic with a Buildx wrapper that validates both architectures cache-only by default and accepts explicit standard Buildx tag/push options for publication; documents `bash publish-docker.sh` because the repository tracks this Windows-authored helper without an executable mode bit | 2026-07-17 |

### Verification
- `docker buildx build --check .`: complete with no warnings.
- Clean-context linux/amd64 image built and loaded from repository source; `bash publish-docker.sh` then built linux/amd64 and linux/arm64 successfully with cache-only output and no registry publication.
- Corrected multi-platform repeat build completed in 2.2 seconds with 30 cached build steps.
- Corrected-image smoke: `/api/v1/health` returned healthy at 1.0.53; `/` returned 200 with both 1.0.53 cache keys; Docker health became healthy; the CLI executed; termination completed within the 10-second grace window.
- Effective API process ran as configured UID/GID 1234:1234 after root-only bind-mount setup; `ldd` found no missing libraries for the API apphost or mp4decrypt.
- FFmpeg, ffprobe, mkvmerge, mp4decrypt, and the CLI were executable; no SDK, compiler, source tree, or apt package indexes remained in the runtime image.
- Corrected local image size: 215,830,424 bytes versus 215,826,038 bytes for released 1.0.53 (4,386-byte delta); API trimming preserved at 49,646,865 bytes versus 49,623,681 bytes released.
- `docker compose config -q`, shell syntax checks, and `git diff --check`: clean.
- Docker Scout was available but CVE scanning could not run because the local Scout installation requires Docker ID authentication; no login was attempted.

### API Contract
- No API route, request, response-shape, or status-code changes.

### Status (Round 37)
- Complete locally on `testing`; not committed or published.

---

## Round 36 — Full-Stack Security and Browser Reliability Audit (2026-07-17)

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.CLI/Cruncharr.CLI.csproj | N/A (Docker/runtime dependency manifest) | [PT] Updated Microsoft.Extensions.DependencyInjection, Logging, and Logging.Console 8.0.0 → 8.0.1 so System.Text.Json receives its security fixes without dependency downgrades; no CLI behavior change | 2026-07-17 |
| src/Cruncharr.Core.Tests/Cruncharr.Core.Tests.csproj | N/A (test dependency manifest) | [PT] Migrated deprecated xUnit 2.5.3 → xunit.v3 3.2.2 and runner 2.5.3 → 3.1.5, removing vulnerable legacy NETStandard packages; pinned the runner's Windows access-control transitive dependency to its patched 6.0.1 release so the resolved test graph contains no deprecated packages | 2026-07-17 |
| src/Cruncharr.API/Controllers/ImagesController.cs | Existing REST image proxy for desktop catalog artwork | [PT] Replaced the static redirect-following client with a request-scoped factory client so redirects cannot bypass the validated Crunchyroll HTTPS host boundary and client wrappers are disposed after each fetch; existing route and response contract unchanged | 2026-07-17 |
| src/Cruncharr.API/Controllers/HealthController.cs | Existing API release metadata response | [PT] Aligned the stale fallback version with release 1.0.53; route and response shape unchanged | 2026-07-17 |
| src/Cruncharr.API/Program.cs | Existing API service registration and API documentation | [PT] Configured the CruncharrImages client with redirects disabled; registered Swagger's Newtonsoft support so schema generation uses the API's serializer and works in the trimmed image; default IHttpClientFactory behavior remains available to other services | 2026-07-17 |
| src/Cruncharr.API/Cruncharr.API.csproj | N/A (API serializer/Swagger integration and release metadata) | [PT] Added matching Swashbuckle Newtonsoft integration for trimmed-image schema generation and bumped release metadata 1.0.52 → 1.0.53 | 2026-07-17 |
| src/Cruncharr.Core/Services/NotificationService.cs | CRD notification logging adapted for the remotely readable Docker API log | [PT] Stopped writing full webhook URLs to API-visible logs because webhook paths and query strings can contain credentials; notification behavior and payloads unchanged | 2026-07-17 |
| src/Cruncharr.Core/Services/HistoryService.cs | CRD/Utils/Structs/History/HistorySeries.cs desktop series-level poster behavior | [PT] Removed the web-only episode-thumbnail fallback from HistorySeries.ThumbnailImageUrl; series cards now remain empty until poster_tall metadata is available, so a first-episode screenshot cannot be persisted as cover art | 2026-07-17 |
| src/Cruncharr.Core.Tests/UnitTest1.cs | Existing test placeholder | [PT] Replaced the empty placeholder with a cancellation-aware regression guard proving an upstream image redirect is returned as a 502 and is not followed to an untrusted host | 2026-07-17 |
| src/Cruncharr.Core.Tests/CalendarLanguageFilterTests.cs | Existing calendar language regression suite | [PT] Marked the theory's deliberate null season-name case nullable, clearing the xUnit v3 nullability diagnostic without changing coverage | 2026-07-17 |
| src/Cruncharr.Core.Tests/HistoryDownloadRecordTests.cs | Desktop History series poster behavior | [PT] Extended the cover-art guard to prove an episode screenshot remains episode-only before refresh and poster_tall becomes the series card image after refresh | 2026-07-17 |
| src/Cruncharr.Core.Tests/QueuePumpEligibilityTests.cs | Existing queue scheduler regression suite | [PT] Passed the xUnit test cancellation token to delays and bounded waits, and made restored-retry cleanup wait for the final persisted snapshot operation before deleting its Windows temp files; assertions and production behavior unchanged | 2026-07-17 |

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Date |
|------|-------------------|-------------------|------|
| src/Cruncharr.API/wwwroot/js/app.js | History cover/details, persisted navigation, API-key access, protected artwork, and live queue updates | Existing `/api/v1/*` routes only | Added guarded browser-storage helpers so privacy/storage-denied modes and invalid persisted pages cannot break fetch/navigation/artwork/migration/SSE setup; escaped external episode-number text; bumped the one-time poster repair to v5 after removing the persisted episode-screenshot fallback | 2026-07-17 |
| src/Cruncharr.API/wwwroot/index.html | Web release cache refresh | none | Aligned CSS and JavaScript cache keys at 1.0.53 so browsers cannot retain mixed release assets | 2026-07-17 |

### Verification
- NuGet restore completed with no vulnerable or deprecated direct/transitive packages.
- Debug build: 0 warnings, 0 errors; full suite: 168/168 passing (three consecutive runs after queue-race correction).
- Release build: 0 warnings, 0 errors; full suite: 168/168 passing.
- `dotnet format analyzers --verify-no-changes --severity warn`: clean.
- Frontend syntax, guarded-storage scan, HTML ID uniqueness, CSS brace balance, Compose, shell syntax, Unraid XML, Git integrity, diff whitespace, and tracked-secret scan: clean.
- Self-contained single-file trimmed linux/amd64 publish and Docker build completed.
- Trimmed-image smoke: health returned healthy at 1.0.53; HTML served both 1.0.53 cache keys; Swagger generated 85 operations with 0 duplicate method/routes.

### API Contract
- No route, request, response-shape, or status-code changes.

### Release Status (Round 36)
- Release version: 1.0.53.
- Source commit: `be4ff44` (`fix: harden history and API runtime`).
- `testing` and stable `master` pushed to the audited source commit.
- Multi-architecture image pushed to `ghcr.io/mediavybz/cruncharr:testing`, `:latest`, and `:1.0.53`.
- Registry index digest: `sha256:b4570cc27de89c180d452025e10db99450c2a46f4ccd54f1dc4d86c78b0c8f60` (linux/amd64 + linux/arm64, with attestations).
- Pulled-image smoke passed for `:testing` and `:latest` at `1.0.53+be4ff44af18c32ff00cfb585740a7224b1a932e2`; testing Swagger returned 200.
- Tag `v1.0.53` pushed and GitHub release published: https://github.com/mediavybz/Cruncharr/releases/tag/v1.0.53

---

## Round 35 — Download, Calendar, Scheduler, and Sonarr Reliability (2026-07-17)

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.Core/Services/DownloadService.cs | CRD/Utils/Helpers.cs : 444-503; CRD/Utils/Muxing/Merger.cs : 31-56; CRD/Downloader/Crunchyroll/CrunchyrollManager.cs : 1306-1314 | [PT] Encode temp output now preserves the requested container extension; replacement requires ffmpeg exit code 0 and failed/cancelled partial output is deleted while the valid muxed input remains; mux success is propagated so a partial output left by a failed mux cannot be reported complete; organized series folders now prefer Sonarr's actual path basename/title before the Crunchyroll title | 2026-07-17 |
| src/Cruncharr.API/Services/AutoDownloadSchedulerService.cs | CRD/Utils/Structs/History/HistorySeries.cs : 249-365; CRD/Downloader/ProgramManager.cs : 170-197 | [PT] Replaced the simplified every-undownloaded/hard-coded-ja-JP scheduler loop with desktop eligibility rules (specials, Sonarr missing state, monitoring, CountMissing, partial downloads), season→series→global language overrides, "all"/"none" subtitle handling, available-dub filtering, and complete queue metadata (selected languages, locale, description, thumbnail, identifiers); wired the current config into the add-missing pass | 2026-07-17 |
| src/Cruncharr.API/Controllers/CalendarController.cs | CRD/Utils/Structs/CalendarStructs.cs : 133-149 | [PT] Flatten merged same-day calendar children into independent API episode entries so every episode the desktop recursively queues can be selected from the web calendar; retained the existing route and response object shape; language/hide-dub filters apply per exposed episode and the parent card shows its own first episode number instead of the aggregate range | 2026-07-17 |
| src/Cruncharr.API/Controllers/QueueController.cs | CRD/Downloader/Crunchyroll/CrunchyrollManager.cs : 701-709 | [PT] Open completed-file browser streams with FileShare.ReadWrite \| FileShare.Delete so an active/range HTTP response does not prevent Sonarr or the user from renaming/moving the completed file; route and response unchanged | 2026-07-17 |
| src/Cruncharr.API/Cruncharr.API.csproj | N/A (release metadata) | [PT] Bumped testing release version 1.0.51 → 1.0.52 | 2026-07-17 |
| src/Cruncharr.Core.Tests/Cruncharr.Core.Tests.csproj | CRD/Utils/Structs/CalendarStructs.cs : 145-149 | [PT] Added test-only references to Cruncharr.API, the ASP.NET Core shared framework, and the existing Microsoft.Extensions.Http 8.0.0 dependency so the current suite can execute the calendar REST mapping guard; no production dependency change | 2026-07-17 |
| src/Cruncharr.Core.Tests/CalendarLanguageFilterTests.cs | CRD/Utils/Structs/CalendarStructs.cs : 145-149 | [PT] Added regression coverage proving merged same-day episodes are returned by GET /api/v1/calendar/custom as separate selectable IDs with their own episode numbers | 2026-07-17 |
| src/Cruncharr.Core.Tests/EncodingPresetAndTranscodeTests.cs | CRD/Utils/Helpers.cs : 449-489 | [PT] Added regression guards that encode temp output preserves the input container extension and can replace the muxed source only when ffmpeg exits with code 0 and the output exists | 2026-07-17 |
| src/Cruncharr.Core.Tests/PortedGapTests.cs | CRD/Downloader/Crunchyroll/CrunchyrollManager.cs : 1306-1314 | [PT] Added regression coverage that organized folders prefer Sonarr's actual path basename, then Sonarr title, then the Crunchyroll title | 2026-07-17 |
| src/Cruncharr.Core.Tests/QueuePumpEligibilityTests.cs | CRD/Utils/Structs/History/HistorySeries.cs : 257-365 | [PT] Added scheduler regression coverage for season-over-series language overrides, "all" subtitle expansion, queue metadata propagation, and configured exclusion of unmonitored Sonarr episodes | 2026-07-17 |

### Infrastructure
| File | Purpose | Date |
|------|---------|------|
| docker-entrypoint.sh | [PT] Apply configurable UMASK (default 002) before creating/writing bind-mounted content so files are group-writable for Sonarr when containers share a PGID; existing media is not recursively modified | 2026-07-17 |
| docker-compose.yml | [PT] Expose UMASK beside PUID/PGID with default 002 and document group-writable Sonarr import behavior | 2026-07-17 |
| templates/cruncharr.xml | [PT] Expose advanced UMASK=002 setting in the stable Unraid template for group-writable Sonarr imports | 2026-07-17 |
| templates/cruncharr-test.xml | [PT] Expose advanced UMASK=002 setting in the testing-channel Unraid template for group-writable Sonarr imports | 2026-07-17 |

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Date |
|------|-------------------|-------------------|------|
| src/Cruncharr.API/wwwroot/js/app.js | History whole-season/whole-series download actions; History auto-refresh/add-missing settings | GET /api/v1/series/{seriesId}/episodes; POST /api/v1/queue; GET/POST /api/v1/config | Added episode thumbnail/cover/description metadata to History bulk queue requests; exposed and saved AutoRefreshAddToQueue with scheduler/Queue Auto Download guidance; no backend imports or shared code | 2026-07-17 |
| src/Cruncharr.API/wwwroot/index.html | Web release cache refresh | none | Bumped app.js cache key 1.0.51 → 1.0.52 | 2026-07-17 |

### Verification
- Baseline before Round 35 changes: 156/156 tests passing (Debug)
- Calendar regression tests: 21/21 passing (`CalendarLanguageFilterTests`, Debug)
- Download/encode/Sonarr targeted guards: 46/46 passing (`EncodingPresetAndTranscodeTests`, `PortedGapTests`, and calendar tests, Debug)
- Scheduler guards: 2/2 passing (override languages/metadata and unmonitored Sonarr exclusion, Debug)
- Note: a class-wide queue test run hit the existing Windows temp-file cleanup race in `RestoredRetry_WakesAndStartsWhenRetryTimeArrives`; new scheduler guards were rerun in isolation and passed
- Final Debug full suite: 168/168 passing
- Final Release full suite: 168/168 passing
- Frontend syntax: `node --check src/Cruncharr.API/wwwroot/js/app.js` passed
- Infrastructure validation: `docker compose config -q` passed; both Unraid XML templates parsed successfully
- Source hygiene: `git diff --check` passed
- Dual-architecture self-contained publish completed for linux/amd64 and linux/arm64
- Post-commit published-image smoke: `/api/v1/health` returned healthy at `1.0.52+2846b7f674ee8f37c8fae78c65b07692fb07af59`; served HTML referenced `app.js?v=1.0.52`; served JS contained the scheduler UI control

### API Contract
- No API route, request, response-shape, or status-code changes. Calendar merged episodes are exposed as additional objects using the existing response shape.

### Release Status (Round 35)
- Testing release version: 1.0.52
- Source commit: `2846b7f` (`fix: harden downloads and scheduler`)
- Multi-architecture image pushed to `ghcr.io/mediavybz/cruncharr:testing`.
- Initial pre-commit registry index digest: `sha256:67a7785e2d448bc8b7b506a7eec486742e6b8ead07b28fe1ce0dee2d1c67de6a` [superseded].
- Final post-commit registry index digest: `sha256:35b3164630ea08ba1b14af07a36fbc4af1d7c6b730d00e8b59fcc6d9887376f0` (linux/amd64 + linux/arm64).
- Stable `master`, `:latest`, and version tags were not changed.

---

## Round 18 — Frontend Design Overhaul (2026-06-23)

User-reported issues: glassy look not working; themes too similar (System≈Dark, AMOLED/Violet≈darker Dark); sidebar scrollbar invisible in some themes (contrast); general UI unhappiness.

### Frontend (Mode B)
| File | Desktop Equivalent | Changes | Date |
|------|-------------------|---------|------|
| src/Cruncharr.API/wwwroot/css/app.css | N/A (design polish) | [UI-THEMES] Rewrote design-token + theme block: added System theme (data-theme="system" + prefers-color-scheme media query, distinct cooler dark base so System≠Dark even on dark OS); reworked AMOLED (pure black + faint accent vignette so it reads as intentional cinematic black, not flat darker dark); reworked Cinematic glass (stronger 3-stop bloom, translucent surfaces, 18px backdrop-blur on cards/header/content — glass now actually visible); reworked Seerr/Nebula (lifted slate base from near-black to mid-slate, vivid indigo→purple aurora, lavender-tinted text, signature gradient active nav); refined Dark (warmer-neutral surfaces, stronger borders); refined Light/Sonarr contrast | 2026-06-23 |
| src/Cruncharr.API/wwwroot/css/app.css | N/A (bugfix) | [UI-BUG] Fixed modal CSS bug: `.modal` (panel) was styled as the dark blurred overlay while `.modal-content` (dead — no such element in HTML) held the glass-panel styles. Reversed: `.modal-overlay` = blurred dark backdrop (blur 8px + rgba 0.55), `.modal` = glass surface (var(--glass-bg) + blur 28px + popIn). Panel was nearly invisible before. | 2026-06-23 |
| src/Cruncharr.API/wwwroot/css/app.css | N/A (contrast fix) | [UI-SCROLL] Fixed invisible sidebar scrollbar: added per-theme `--scrollbar-thumb` / `--scrollbar-thumb-hover` / `--scrollbar-thumb-sidebar` / `--scrollbar-thumb-sidebar-hover` tokens (was `--border-color` @ 0.06 alpha — invisible on dark sidebar). Scrollbar thumb now 0.18-0.22 alpha + accent on hover, 8-10px wide with padding-box border for clean rendering. Deduped two conflicting scrollbar blocks. | 2026-06-23 |
| src/Cruncharr.API/wwwroot/css/app.css | N/A (design polish) | [UI-POLISH] Refined "Modern UI Polish" block: removed hardcoded `rgba(255,255,255,0.12)` card-hover border (broke light theme) → `--accent-softer`; removed hardcoded `--cr-gray-600` download-hover border → `--accent-softer`; removed `translateX(3px)` nav hover (caused layout jitter); toned down card hover lift (-6px→-3px); removed `scale(1.02)` primary-button hover (jumpy); all motion now uses --dur-* / --ease-out tokens; primary button uses `--accent-grad` token | 2026-06-23 |
| src/Cruncharr.API/wwwroot/css/app.css | N/A (design polish) | [UI-POLISH] Sidebar: added subtle top accent glow (::before radial), header bottom border, logo drop-shadow with accent-glow, gradient text title (accent-grad background-clip). Cinematic theme: scoped translucent header + transparent content so bloom blur is visible. | 2026-06-23 |
| src/Cruncharr.API/wwwroot/js/app.js | N/A (theme wiring) | [UI-THEMES] applyTheme() now sets data-theme="system" for System (CSS handles OS via prefers-color-scheme media query) instead of resolving System→Dark/Light in JS. System is now genuinely distinct from Dark. | 2026-06-23 |
| src/Cruncharr.API/wwwroot/index.html | N/A (cache-bust) | Busted CSS/JS cache version beta.131→beta.132 | 2026-06-23 |
| src/Cruncharr.API/Cruncharr.API.csproj | N/A (version) | Bumped Version beta.131→beta.132 | 2026-06-23 |

### Verification
- Build: 0 errors, 0 warnings (API Release)
- JS: node --check OK
- CSS: 509/509 brace balance
- Tests: 86/86 passing (Cruncharr.Core.Tests)
- No API contract changes (frontend-only)

### Infrastructure
| File | Purpose | Date |
|------|---------|------|
| Docker image | Multi-arch (linux/amd64 + linux/arm64) pushed to ghcr.io/mediavybz/cruncharr:latest + ghcr.io/mediavybz/cruncharr:0.2.0-beta.132. Manifest list digest sha256:f855effd78e64a05e97c77ef753dc167f9d8224de7d45e6142a635e2298ae8a7 | 2026-06-23 |

---

## Round 17 — Recursive Audit Fixes (2026-06-17)

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.API/Controllers/ConfigController.cs | N/A (API layer) | [AUDIT-H1] Added SSRF validation to POST /api/v1/config/sonarr/test — constructs URL from Host/Port/UseSsl and validates via WebhookUrlValidator before calling TestConnectionDetailedAsync (matches webhook/test pattern) | 2026-06-17 |
| src/Cruncharr.API/Controllers/ConfigController.cs | N/A (API layer) | [AUDIT-M1] Added ValidatePath to QueueFilePath and TokenFilePath — was missing while all other path fields had it (path traversal inconsistency) | 2026-06-17 |
| src/Cruncharr.CLI/Program.cs | N/A (CLI layer) | [AUDIT-H2] GetConfigPath now reads CRUNCHYROLL_CONFIG_PATH first (matches API/Dockerfile), falls back to CRUNCHYROLL_CONFIG_DIR, defaults to cruncharr.yaml (was cruncharr.json) | 2026-06-17 |
| src/Cruncharr.CLI/Commands/DaemonCommand.cs | N/A (CLI layer) | [AUDIT-H2] LoadConfig now reads CRUNCHYROLL_CONFIG_PATH first, falls back to CRUNCHYROLL_CONFIG_DIR with yaml/yml/json lookup order (was yml/json only) | 2026-06-17 |
| src/Cruncharr.API/Services/UpdateCheckerService.cs | N/A (port-added) | [AUDIT-M4] Replaced Version-based comparison with semver comparison using AssemblyInformationalVersion; ParseVersion→ParseSemver+IsNewerVersion handles prerelease suffixes (0.2.0-beta.83 > 0.2.0-beta.82 now works) | 2026-06-17 |
| src/Cruncharr.API/Controllers/HealthController.cs | N/A (API layer) | [AUDIT-M4] /version endpoint now returns AssemblyInformationalVersion (0.2.0-beta.82) instead of AssemblyVersion (0.2.0.0) | 2026-06-17 |

### Verification
- Build: 0 errors, 0 warnings (both API and CLI)
- No API contract changes (response shapes unchanged for valid inputs; new 400 BadRequest for SSRF-blocked Sonarr test)
- Docker image: multi-arch (amd64+arm64) pushed to ghcr.io/mediavybz/cruncharr:latest, digest sha256:d0c5b12114b393718029dda6308909fbcec53dbfdf1c233091a619056d97dadc

### Infrastructure
| File | Purpose | Date |
|------|---------|------|
| Dockerfile | Multi-stage build: builder stage has curl/xz-utils/cmake/make/g++/git; final stage has only ca-certificates + mkvtoolnix + gosu. Zero-dep healthcheck via /proc/net/tcp. Removed dead dirs /app/presets /app/video. Digest sha256:cc3f60c0b57d52c6881bbdc5e2ebb0e19f810bfbe5a826348b45c363927d9a55 | 2026-06-17 |
| docker-compose.yml | Healthcheck changed from curl to /proc/net/tcp grep; removed obsolete version key; fixed stale comments about dead dirs | 2026-06-17 |
| .dockerignore | Added .env, designsystem/, src/publish/, *.md; removed dead CRD/Assets exception rules | 2026-06-17 |

### Frontend (Mode B)
| File | Desktop Equivalent | Changes | Date |
|------|-------------------|---------|------|
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend optimization) | [UI-LOAD] Removed Google Fonts CDN dependency (Inter) — eliminates external DNS+TLS+CSS+6 font file downloads; uses system font stack only | 2026-06-17 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend optimization) | [UI-LOAD] DOMContentLoaded no longer blocks on await fetchConfig() — config loads in background, app shell renders immediately | 2026-06-17 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend optimization) | [UI-LOAD] Added branded loading splash (spinner + logo) shown immediately on page load, removed once app shell renders | 2026-06-17 |
| src/Cruncharr.API/wwwroot/favicon.svg | N/A (asset optimization) | [UI-LOAD] Replaced 736KB PNG-in-SVG wrapper with 0.4KB true vector SVG (99.95% reduction) | 2026-06-17 |
| src/Cruncharr.API/wwwroot/index.html | N/A (design polish) | [UI-CSS] Custom thin scrollbars, card depth+hover shadows, search bar focus glow, sidebar active nav accent bar, poster/card hover lift, progress bar gradient+glow, shimmer skeleton animation, empty-state styling, focus-visible outlines | 2026-06-17 |
| src/Cruncharr.API/wwwroot/index.html | N/A (design polish) | [UI-CSS] Sidebar logo: gradient background with glow shadow, SVG play icon instead of 13KB PNG | 2026-06-17 |
| src/Cruncharr.API/wwwroot/site.webmanifest | N/A (PWA fix) | [UI-FIX] Replaced "MyWebSite"/"MySite" placeholders with "Cruncharr"; theme_color #0a0a0a matching dark default | 2026-06-17 |

---

## Round 16 — Upstream Sync b7b10da + 245cf78 (2026-06-12)

Vendored CRD/ folder synced from upstream c123093 → 245cf78. All functional changes ported.

### Backend (Mode A)
| Upstream change | Source file | Downstream file | Notes |
|---|---|---|---|
| New option history_check_partial_downloads | CrDownloadOptions.cs | CruncharrConfig.cs (history.check_partial_downloads), ConfigController.cs | default true |
| New option proxy_all_traffic + CrunchyrollOnlyProxy | CrDownloadOptions.cs, HttpClientReq.cs | CruncharrConfig.cs (proxy.all_traffic), HttpClientWrapper.cs | scoped proxy routes only *.crunchyroll.com, Widevine licence URL excluded |
| Rename dub_download_delay_seconds → download_delay_seconds + download_delay_use_dub_based | CrDownloadOptions.cs, CrunchyrollManager.cs | CruncharrConfig.cs, DownloadService.cs (per-dub), QueueService.cs (per-episode), ConfigController.cs | YAML key renamed to match upstream |
| New option calendar_show_history_mark | CrDownloadOptions.cs | CruncharrConfig.cs (calendar.show_history_mark) | default true |
| New add-download options (search_add_to_history, single_episode_instant_add, default_search_enabled) | CrDownloadOptions.cs, AddDownloadPageViewModel.cs | CruncharrConfig.cs (new add_download section), ConfigController.cs | |
| Calendar history marks (ApplyHistoryStatus/FindHistoryMatch/ApplyMergedHistoryStatus/RefreshHistoryStatuses, ExtractVersionGuids) | CalendarManager.cs, CalendarStructs.cs | CalendarService.cs, CalendarModels.cs, CalendarController.cs | CalendarHistoryDownloadState enum; exposed as isInHistory/showHistoryMark/historyDownloadState in API |
| Calendar perf: paged GetNewEpisodes (100/page, stale-page early stop) | CrEpisode.cs, CalendarManager.cs | CrunchyrollApiService.GetNewEpisodesAsync(firstWeekDay overload), CalendarService.cs | |
| LooksLikeGenericSeasonLabel: only "Season N" treated as generic | CalendarManager.cs | CalendarService.cs | |
| AniList guards: once-per-day load, parse-failure bail-out | CalendarManager.cs | CalendarService.cs | |
| RefreshHistoryWithNewReleases request 2000 → 1000 | ProgramManager.cs | AutoDownloadSchedulerService.cs | |
| Partial history tracking not overwritten (merge downloaded locales) | HistoryEpisode.cs | HistoryModels.SetDownloadedMedia | service-level merge already existed |
| HistoryCheckPartialDownloads gate on partial actionability | HistorySeries.cs | frontend isEpisodePartiallyDownloaded + CalendarService.ApplyHistoryStatus | |
| Episode sort supports ranges "E11-12" | History.cs (NumericStringPropertyComparer) | HistoryService.cs | |
| Episode re-key keeps season prefix + real episode label; IsRegularEpisodeNumber regex w/ ranges | CrSeries.cs, EpisodeStructs.cs | CrunchyrollApiService.ListSeriesIdAsync, GetEpisodeLabelFromKey, IsRegularEpisodeNumber | also fixes "normal" filter (was StartsWith("E"), now !StartsWith("SP")) |
| StreamError.Reason in playback error output | StreamLimits.cs, CrunchyrollManager.cs | StreamError.cs, DownloadService.cs | |
| SSO authorization-code (PKCE) login for non-TV endpoints | CRAuth.cs | CrunchyrollAuthService.cs (LoginWithCodeFlowAsync/GetCodeAuthAsync/LoginWithCodeAsync, GenerateCodeVerifier, GetClientIdFromBasicHeader, ExtractCode) | TV endpoints keep password grant; code flow falls back to password grant on error |
| Queue refresh/persistence perf (RefreshItem, QueuePersistenceRequested split) | QueueManager.cs, QueuePersistenceManager.cs, HLSDownloader.cs | n/a — downstream already uses explicit ScheduleSave() at mutation points and SSE whole-queue broadcast | equivalent behavior, no UI item model collection downstream |

### Frontend (Mode B)
| Desktop change | Web UI mirror |
|---|---|
| Calendar history marks (colored dot: green=downloaded, orange=partial, gray=not downloaded) | fetchCalendar episode template + .calendar-history-mark CSS |
| GeneralSettings: download delay rename + per-dub toggle | Settings → Download tab |
| GeneralSettings: history check partial downloads toggle | Settings → History tab |
| CrunchyrollSettings: proxy all traffic toggle | Settings → Proxy tab |
| Calendar: show history mark toggle | Settings → Calendar tab |
| AddDownload tab settings (3 toggles) | Settings → Calendar tab (Add Download Settings section) |
| Partial check disabled ⇒ no partial indicators | isEpisodePartiallyDownloaded gate |

### Verification
- Build: 0 errors, 0 warnings
- Tests: 64/64 passing
- Frontend JS: node --check OK

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
| GET | /api/v1/health/version | - | - | { CurrentVersion, LatestVersion, UpdateAvailable } | **NEW** |

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
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-FIX-001**: Fixed calendar timezone bug: replaced toISOString() with local getFullYear()/getMonth()/getDate() | 2026-06-04 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-FIX-002**: Fixed calendar invalid date crashes: added isNaN(date.getTime()) validation before toLocaleTimeString() | 2026-06-04 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-FIX-003**: Fixed add-download invalid date crashes: validated airDate before toLocaleTimeString() | 2026-06-04 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-FIX-004**: Fixed history auto-refresh: renderHistory() restarts historyIntervalId if null | 2026-06-04 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-FIX-005**: Fixed double history interval leak: startPolling() guards with if (!historyIntervalId) | 2026-06-04 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-FIX-006**: Fixed calendar grid nesting: #calendar-grid directly receives className instead of nested div | 2026-06-04 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-FIX-007**: Fixed calendar error state: removed grid-column CSS, cleared calendar-grid class | 2026-06-04 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-FIX-008**: Added null guards: renderBrowseContent, renderSeasonalContent, renderUpcomingSeasonsContent | 2026-06-04 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-FIX-009**: Fixed getDoingText retry date validation: added isNaN(retryDate.getTime()) check | 2026-06-04 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-FIX-010**: Fixed unescaped retryTime: added escapeHtml(retryTime) in getDoingText() | 2026-06-04 |
| src/Cruncharr.API/wwwroot/index.html | N/A (frontend fixes) | **F-FIX-011**: Removed duplicate .dropdown-menu CSS definition | 2026-06-04 |

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
| src/Cruncharr.Core/Services/DownloadService.cs | upstream | [PT] Wired MuxTypesettingFonts config instead of hardcoded true | 2026-06-03 |
| src/Cruncharr.Core/Services/CrunchyrollApiService.cs | upstream | [PT] Fixed multi-episode regex (upstream #447); added Episode property mapping | 2026-06-03 |
| src/Cruncharr.API/Services/AutoDownloadSchedulerService.cs | upstream ProgramManager | [PT] New IHostedService for auto-download scheduler with 3 modes | 2026-06-03 |
| src/Cruncharr.API/Controllers/SchedulerController.cs | N/A (API layer) | [PT] Added GET /api/v1/scheduler/status, POST /api/v1/scheduler/trigger | 2026-06-03 |
| src/Cruncharr.API/Program.cs | N/A (DI wiring) | [PT] Registered AutoDownloadSchedulerService as hosted service | 2026-06-03 |
| src/Cruncharr.Core/Services/NotificationService.cs | upstream NotificationDispatcher | [PT] Added NotifyQueueCompleteAsync(CruncharrConfig) overload: executes DownloadFinishedExecutePath process, dispatches webhook | 2026-06-03 |
| src/Cruncharr.Core/Services/QueueService.cs | upstream QueueManager | [PT] CheckShutdownWhenQueueEmpty: calls _notificationService.NotifyQueueCompleteAsync(_config) before shutdown | 2026-06-03 |
| src/Cruncharr.API/Services/UpdateCheckerService.cs | upstream Updater.cs | [PT] New IHostedService polling GitHub releases every 6h for update availability, exposes LatestVersion/UpdateAvailable | 2026-06-03 |
| src/Cruncharr.API/Controllers/HealthController.cs | N/A (API layer) | [PT] Added GET /api/v1/health/version endpoint returning CurrentVersion, LatestVersion, UpdateAvailable | 2026-06-03 |
| src/Cruncharr.API/Program.cs | N/A (DI wiring) | [PT] Registered UpdateCheckerService as hosted service | 2026-06-03 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream CrunchyrollManager.cs | [PT] Wired Kstream config in SelectVideoTrackQma: 1-based index selects specific video track from DASH manifest | 2026-06-03 |
| src/Cruncharr.API/Services/QueueBroadcastService.cs | N/A (API layer) | **[CRIT-1]** Replaced single Channel with ConcurrentDictionary<Guid, ChannelWriter> to broadcast to all SSE clients instead of competing readers | 2026-06-03 |
| src/Cruncharr.API/Controllers/QueueController.cs | N/A (API layer) | **[CRIT-1/HIGH-2]** Updated GetQueueUpdates to Subscribe/Unsubscribe pattern with clientId; added IsGloballyPaused to initial SSE payload | 2026-06-03 |
| src/Cruncharr.API/Controllers/QueueController.cs | N/A (API layer) | **[HIGH-1]** Added try/catch to GetQueue() returning StatusCode(500) on error | 2026-06-03 |
| src/Cruncharr.API/Controllers/AuthController.cs | N/A (API layer) | **[HIGH-3]** Added try/catch to SwitchProfile() returning StatusCode(500) on error | 2026-06-03 |
| src/Cruncharr.API/Controllers/AuthController.cs | N/A (API layer) | **[HIGH-4]** Added try/catch to Logout() returning StatusCode(500) on error | 2026-06-03 |
| src/Cruncharr.API/Controllers/ConfigController.cs | N/A (API layer) | **[HIGH-5]** Injected IHttpClientFactory, replaced `new HttpClient()` with `_httpClientFactory.CreateClient()` in TestWebhook | 2026-06-03 |
| src/Cruncharr.API/Controllers/AuthController.cs | N/A (API layer) | **[MED-1/2]** Fixed NRE on MultiProfile in GetStatus() and GetProfiles() using null-conditional `?.Profiles?` | 2026-06-03 |
| src/Cruncharr.API/Controllers/HistoryController.cs | N/A (API layer) | **[MED-3]** Fixed NRE in MapToResponse() on null collections using `?.Select().ToList() ?? new List<>()` | 2026-06-03 |
| src/Cruncharr.API/Controllers/ConfigController.cs | N/A (API layer) | **[MED-4]** Added try/catch to GetConfig() returning StatusCode(500) on error | 2026-06-03 |
| src/Cruncharr.API/Controllers/QueueController.cs | N/A (API layer) | **[LOW-1]** Removed unused `using System.Threading.Channels;` | 2026-06-03 |
| src/Cruncharr.API/Controllers/SeriesController.cs | N/A (API layer) | **[LOW-2]** Removed unused `using Cruncharr.Core.Configuration;` | 2026-06-03 |
| src/Cruncharr.API/Controllers/SchedulerController.cs | N/A (API layer) | **[LOW-3]** Added try/catch to GetStatus() returning StatusCode(500) on error | 2026-06-03 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream CrunchyrollManager.cs | **[C-001]** Fixed RunProcessWithOutputAsync deadlock: read stdout/stderr concurrently BEFORE WaitForExitAsync | 2026-06-03 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream History.cs | **[C-002]** Fixed concurrent reader/writer race: added GetHistorySnapshot() helper, all read-only methods now use it | 2026-06-03 |
| src/Cruncharr.Core/Utils/HttpClientWrapper.cs | N/A (existing) | **[H-001/H-002/L-004]** Added `using` for HttpResponseMessage, dispose HttpRequestMessage in finally, replaced Console with ILogger | 2026-06-03 |
| src/Cruncharr.Core/Services/QueueService.cs | upstream QueueManager.cs | **[M-001]** Made `_isInitialized` and `_isGloballyPaused` volatile | 2026-06-03 |
| src/Cruncharr.Core/Services/QueueService.cs | upstream QueueManager.cs | **[M-002]** Catch OperationCanceledException separately in pump to avoid logging as error | 2026-06-03 |
| src/Cruncharr.Core/Services/QueueService.cs | upstream QueueManager.cs | **[M-003]** Pass `_cancellationToken` to Task.Delay in ScheduleRetry and BlockAutoDownloadUntil | 2026-06-03 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream History.cs | **[M-004]** Log exception in CrUpdateSeriesAsync instead of swallowing | 2026-06-03 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream History.cs | **[M-005]** Implemented IDisposable, dispose both SemaphoreSlim instances | 2026-06-03 |
| src/Cruncharr.API/Controllers/AuthController.cs | N/A (API layer) | **[HIGH-6]** Wrapped GetStatus in try/catch; added null-conditional access on Profile/MultiProfile properties | 2026-06-03 |
| src/Cruncharr.API/Services/UpdateCheckerService.cs | upstream Updater.cs | **[HIGH-7]** Fixed semver parsing: strip 'v' prefix and prerelease suffix (-beta.1) before Version.TryParse | 2026-06-03 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream History.cs | **[HIGH-8]** CrUpdateSeriesAsync: moved ParseSeriesByIdAsync/GetSeasonDataByIdAsync API calls outside lock to prevent contention | 2026-06-03 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream History.cs | **[HIGH-9]** MatchHistorySeriesWithSonarrAsync: moved GetSeriesAsync outside lock to prevent contention | 2026-06-03 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream History.cs | **[HIGH-10]** MatchHistoryEpisodesWithSonarrAsync: moved GetEpisodesAsync outside lock to prevent contention | 2026-06-03 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream CrunchyrollManager.cs | **[M-006]** Added null check for dub/AD MediaGuid before Contains(':') | 2026-06-03 |
| src/Cruncharr.Core/Services/DownloadService.cs | upstream CrunchyrollManager.cs | **[M-007]** Cache token locally in GetPlaybackDataAsync to avoid null race after refresh | 2026-06-03 |
| src/Cruncharr.Core/Services/CrunchyrollAuthService.cs | upstream CRAuth.cs | **[L-001]** Removed unused GitHubRelease and GitHubAsset classes | 2026-06-03 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream History.cs | **[L-002]** Removed unused InferSeriesType and RefreshSeriesDataAsync methods | 2026-06-03 |
| src/Cruncharr.Core/Services/QueueService.cs | upstream QueueManager.cs | **[L-003]** Fixed StartItem to return false when TryStartDownload fails | 2026-06-03 |
| src/Cruncharr.Core/Services/QueueService.cs | upstream QueueManager.cs | **[L-005]** Dispose old ProcessingSlotManager before creating new one in ProcessQueueAsync | 2026-06-03 |
| src/Cruncharr.API/Program.cs | N/A (API layer) | **[MED-6]** Use IHostApplicationLifetime.ApplicationStopping instead of CancellationToken.None for queue processor | 2026-06-03 |
| src/Cruncharr.API/Services/QueueBroadcastService.cs | N/A (API layer) | **[MED-7]** Implement IDisposable, unsubscribe QueueStateChanged event handler | 2026-06-03 |
| src/Cruncharr.API/Controllers/ConfigController.cs | N/A (API layer) | **[MED-8]** Add null-conditional operators (?.) throughout GetConfig response builder | 2026-06-03 |
| src/Cruncharr.API/Controllers/HistoryController.cs | N/A (API layer) | **[MED-9]** Add null-conditional (?.) on history.FirstOrDefault in GetSeriesHistory | 2026-06-03 |
| src/Cruncharr.API/Controllers/ConfigController.cs | N/A (API layer) | **[MED-10]** Cache GetAddressBytes() result to avoid up to 9 calls per IP check | 2026-06-03 |
| src/Cruncharr.Core/Services/CrunchyrollAuthService.cs | upstream CRAuth.cs | **[MED-11]** Use File.ReadAllTextAsync instead of sync File.ReadAllText in AuthenticateAsync | 2026-06-03 |
| src/Cruncharr.Core/Utils/HttpClientWrapper.cs | N/A (existing) | **[MED-12]** Implement IDisposable, store SocketsHttpHandler reference, dispose both | 2026-06-03 |
| src/Cruncharr.Core/Services/NotificationService.cs | upstream NotificationDispatcher | **[MED-13]** Dispose Process.Start result with using+WaitForExitAsync | 2026-06-03 |
| src/Cruncharr.Core/Services/QueueService.cs | upstream QueueManager.cs | **[MED-14]** Make _shutdownRequested volatile for thread-safe shutdown signaling | 2026-06-03 |
| src/Cruncharr.Core/Services/HistoryService.cs | upstream History.cs | **[MED-15]** Change GetHistorySnapshot to async GetHistorySnapshotAsync, update all callers | 2026-06-03 |

### Infrastructure
| File | Purpose | Date |
|------|---------|------|
| PORTING_LOG.md | Updated API contract with /v1 prefix, added completion entries | 2026-06-02 |
| Docker image | Multi-platform build (linux/amd64 + linux/arm64) pushed to ghcr.io/mediavybz/cruncharr:latest | 2026-06-03 |
| Docker image | Rebuilt with comprehensive security fixes (XSS, credentials, sync-over-async, HttpClient disposal) - digest: sha256:266a43ff438910ed5c3b19d690f3349aac53136536f48576d8eb0018e97c353f | 2026-06-03 |
| Docker image | v0.2.0-beta.2: Frontend audit fixes (calendar timezone, null guards, date validation, interval leaks, CSS cleanup) - digest: sha256:f53f57b09a66ea37d6d8c16167acfe14831a07d0c227e20a310e43bac59d591e | 2026-06-04 |
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
- Frontend audit gaps resolved: 26 / 26 (previous + global pause UI, cooldown setting, getDoingText/showToast XSS escaped + calendar timezone, date validation x3, interval leaks x2, grid nesting, error CSS, null guards x3, retry validation, escapeHtml, CSS cleanup)
- Backend critical issues fixed: 21 / 21 (hardcoded tokens, sync-over-async x3, null ref, Environment.Exit, undisposed HttpClients x3, SSE broadcast race, missing try/catch x5, NRE x4, unused usings x2, process deadlock, history race, auth status null safety, semver parsing, lock contention x3)
- Backend core issues fixed: 29 / 29 (C-001 deadlock, C-002 history race, H-001/H-002 HttpClient disposal, M-001 volatile, M-002 OCE handling, M-003 cancellation tokens, M-004 swallowed exception, M-005 semaphore disposal, M-006 null dereference, M-007 token race, MED-6 shutdown token, MED-7 event unsubscribe, MED-8 config null safety, MED-9 history NRE, MED-10 IP bytes cache, MED-11 async file I/O, MED-12 HttpClientWrapper dispose, MED-13 process dispose, MED-14 shutdown volatile, MED-15 snapshot async, L-001 unused classes, L-002 unused methods, L-003 wrong return value, L-004 console output, L-005 slot manager disposal)
- Upstream feature gaps: 9 / 9 COMPLETE (global queue pause, download cooldown, speed limiting, auto-download scheduler, font muxing, multi-episode parsing, execute on complete, update checker, kstream selection)
- Settings sync fixes: 3 / 3 (dub/subs fallback defaults, stream endpoint defaults, config validation)
- **UI Improvements: 4 / 4 (dead toggles removed, refresh persistence, loading spinner, multi-select dropdowns)**
- **Build warnings: 306 → 0 (100% reduction)**
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
| 2026-06-29 | Released 1.0.0 (dropped 0.2.0-beta). Full FE/BE audit: 0 build warnings, 99 tests, no async-void/deadlock/empty-catch/XSS/shell-injection/secret-leak. Fixed #7 sync (SyncingService now Stretches frames to a fixed grid so different-scale videos align) + bounded ExtractPixels; demoted MUX DEBUG logs Information→Debug | User authorized backend bug fixes (upstream closed source) + requested ship-ready audit | user confirmed |
| 2026-06-29 | Fixed special-season detection (CrunchyrollApiService): episode is special only when label is non-numeric AND no valid positive episode_number; shared IsSpecialEpisode helper | Upstream CRD v1.6.14. Verified live: One Piece had 43 regular episodes (Fish-Man Island recap saga "FMI1..21" + Wano SP22-25) wrongly flagged special because detection used only the text label. No route/shape change | user confirmed |
| 2026-06-29 | Repointed auth auto-updater to Codeberg data.json + rotated TV client to ANDROIDTV/3.66.0_22348 (client lasrqzxbemvoqioy56m0) | Upstream CRD v1.6.14: moved off dead Crunchy-Downloader GitHub URLs to Codeberg; rotated TV client. New data.json schema (version_name/version_code/Authorization); TV UA now carries _<version_code>. Mobile client unchanged (3.110.0). Token verified from authoritative data.json + base64 decode-checked. NOT login-tested (account-lockout rule) — updater repoint is self-healing | user confirmed |
| 2026-06-29 | Added fields availableDubLang / availableSoftSubs to history episode response (additive) | Upstream CRD v1.6.14 parity: history partial indicator + tooltip must only count languages actually available for the episode | user confirmed |
| 2026-06-29 | Behaviour: manual POST /api/v1/history/downloaded/... now resets downloaded dub/sub tracking to available set | Upstream CRD v1.6.14 fix: manual "Mark as Downloaded" no longer re-flags episode as partial. Route/shape unchanged | user confirmed |
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
| CRD/Utils/Structs/CalendarStructs.cs (merged `CalendarEpisodes`) | src/Cruncharr.API/Controllers/CalendarController.cs | The desktop recursively queues merged same-day children. The REST adapter must flatten the parent and all nested children into separate objects while preserving the existing calendar response shape, IDs, per-episode numbers, and per-episode language filtering. |
| Desktop completed-file access | src/Cruncharr.API/Controllers/QueueController.cs (`GET /api/v1/queue/{id}/file`) | Web downloads use an HTTP `FileStream`; keep `FileShare.ReadWrite | FileShare.Delete` so an open/range browser response does not block Sonarr or the user from renaming/deleting the completed file. Route, response headers/shape, and status codes remain frozen. |
| CRD/Utils/Structs/History/HistorySeries.cs:249-365 + CRD/Downloader/ProgramManager.cs:170-197 | src/Cruncharr.API/Services/AutoDownloadSchedulerService.cs | Scheduler missing-episode eligibility mirrors desktop specials/Sonarr/monitoring/CountMissing/partial rules. Language precedence is season override -> series override -> global config; `all` subtitles expand to episode availability and `none` is omitted. Queue items carry selected languages and episode/series artwork metadata; do not post or synthesize client `versions`. |
| CRD/Utils/Helpers.cs:444-503 + CRD/Utils/Muxing/Merger.cs:31-56 + CRD/Downloader/Crunchyroll/CrunchyrollManager.cs:1306-1314 | src/Cruncharr.Core/Services/DownloadService.cs | Encoding temp files retain the requested container extension; only ffmpeg exit 0 plus an existing output may replace the valid muxed source, and partial encode output is deleted on failure/cancel. Mux success must be enforced. Organized folders prefer Sonarr path basename, then Sonarr title, then Crunchyroll title. |
| Docker process permission setup (no desktop filesystem equivalent) | docker-entrypoint.sh + docker-compose.yml + templates/cruncharr.xml + templates/cruncharr-test.xml | `UMASK` defaults to `002` and is applied before bind-mounted directories/files are created, yielding normal 0775 directories/0664 files for a shared Sonarr PGID. Do not recursively chmod/chown existing media during startup. |
| CRD auth client tokens (data.json) | src/Cruncharr.Core/Services/CrunchyrollAuthService.cs (UpdateAuthCredentialsAsync, EmbeddedAuthData, DefaultAndroidTvAuthSettings) | **Live auth source: `https://codeberg.org/YomuLoad/CRD/raw/branch/pages/data.json`** (fallback `https://yomuload.codeberg.page/CRD/data.json`). Schema: `{type, version_name, version_code, Authorization}`. TV UA = `ANDROIDTV/{version_name}_{version_code} Android/16`; mobile UA = `Crunchyroll/{version_name} Android/16 okhttp/4.12.0` (no code). When CR deactivates the TV client, copy the latest TV token/version from that data.json into `DefaultAndroidTvAuthSettings` + `EmbeddedAuthData` (the runtime updater also pulls it automatically). Old Crunchy-Downloader GitHub URLs are dead. |
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
| CRD/ViewModels/DownloadsPageViewModel.cs delay cancellation + CRD/Downloader/QueueManager.cs retry wake | src/Cruncharr.Core/Services/QueueService.cs | Keep the per-item CTS registered before cooldown/download delays. Restored retry items require a delayed wake after ProcessQueueAsync installs the host token; both startup orderings must request the pump. |
| CRD/Utils/QueueManagement/QueuePersistenceManager.cs state restoration | src/Cruncharr.Core/Services/QueuePersistenceService.cs | Only interrupted Downloading/Processing work becomes Queued after restart. Paused and Cancelled are user intent and must remain unchanged. |
| DownloadHistory.OutputPath REST adapter | src/Cruncharr.API/Controllers/QueueController.cs | GET/DELETE queue file routes accept only canonical paths under Download.OutputDirectory. Preserve the existing route, response shape, and status behavior for valid files. |
| Sanitized desktop settings projection | src/Cruncharr.API/Controllers/ConfigController.cs | Webhook header values are represented by `[configured]` in GET and merged back case-insensitively on POST; never expose stored values in the API response. |

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
| 2026-06-03 | Added GET /api/v1/health/version | Update checker gap fix: frontend can check current/latest version and update availability | automatic |

---

## Deferred / Needs Decision
| Item | Reason | Options | Status |
|------|--------|---------|--------|
| Media/Music browser | **NOT NEEDED** - Upstream desktop app does not have a standalone music/movie browser page. Music is accessed via: (1) `SearchFetchFeaturedMusic` setting which adds featured music videos to series search results, (2) History integration for CR artists. Both are already implemented. Movies are accessed via series search. No separate browse UI exists upstream. | N/A - Feature parity achieved through existing integration. | **RESOLVED** |
| GitHub Actions CI/CD | Organization `mediavybz` is on free plan. GitHub-hosted runners disabled by org billing policy. **RESOLVED**: Removed `.github/workflows/` directory. Docker images built and pushed manually via `docker buildx`. | (1) Enable GitHub Actions billing for org, (2) Use self-hosted runner, (3) Build locally and push manually | **RESOLVED** - Using option 3 (manual build/push) |

---

## Critical Audit Fixes (2026-06-03) — Round 4 Complete

### Critical Issues Fixed (4/4)
| # | Issue | File | Fix |
|---|-------|------|-----|
| CRIT-1 | XSS via unescaped dynamic content | `index.html` | Added `escapeHtml()` to ~20 innerHTML locations; added `escapeHtmlAttribute()` for settings inputs; added `escapeJsString()` for inline onclick handlers |
| CRIT-2 | ThrottledStream sync-over-async | `ThrottledStream.cs` | Added `ThrottleAsync()` with `Task.Delay` instead of `Thread.Sleep`; `ReadAsync` now calls async throttle |
| CRIT-3 | Null content before `.Contains(':')` | `CrunchyrollAuthService.cs` | Added `!string.IsNullOrEmpty(content)` guard before all `.Contains()` checks |
| CRIT-4 | Cookie store race condition | `HttpClientWrapper.cs` | Added per-domain `ConcurrentDictionary<string, object>` locks; fixed `CookieCollection` mutations under lock |

### High Priority Issues Fixed (15/15)
| # | Issue | File | Fix |
|---|-------|------|-----|
| HIGH-5 | HTML attribute injection | `index.html` | `escapeHtmlAttribute()` applied to all settings `value="..."` and `placeholder="..."` attributes (~40 inputs) |
| HIGH-6 | JS injection via onclick | `index.html` | `escapeJsString()` applied to all inline onclick handlers injecting IDs (~30 handlers) |
| HIGH-7 | Global pause state not refreshed | `index.html` | `fetchQueueStats()` called after `toggleGlobalPause()` |
| HIGH-8 | Duplicate addEpisodeToQueue | `index.html` | Renamed second function to `addEpisodeToQueueWithDetails()` |
| HIGH-9 | Missing null checks | `index.html` | Added null checks in 16 async DOM update functions |
| HIGH-10 | SSE missing IsGloballyPaused | `QueueBroadcastService.cs`, `QueueController.cs` | Added `IsGloballyPaused` to SSE broadcasts and initial state |
| HIGH-11 | AuthController.GetProfiles() no try/catch | `AuthController.cs` | Wrapped in try/catch returning 500 on error |
| HIGH-12 | Startup crash on invalid paths | `Program.cs` | Added null/whitespace validation and try/catch around `Directory.CreateDirectory` |
| HIGH-13 | QueueController missing try/catch | `QueueController.cs` | Added try/catch to ALL 12 action methods |
| HIGH-14 | Fire-and-forget exception risk | `Program.cs` | Wrapped `ProcessQueueAsync` in `Task.Run` with exception logging |
| HIGH-15 | RunProcessAsync deadlock | `DownloadService.cs` | Read stdout/stderr concurrently BEFORE `WaitForExitAsync` |
| HIGH-16 | EnsureLoadedAsync race | `HistoryService.cs` | Added `SemaphoreSlim` to prevent multiple concurrent loads |
| HIGH-17 | Null-forgiving on RequestUri | `HttpClientWrapper.cs` | Added null check throwing `ArgumentException` |
| HIGH-18 | Cookie attachment O(n²) | `HttpClientWrapper.cs` | Replaced string scanning with `HashSet<string>` for O(1) lookup |
| HIGH-19 | RequestPump race condition | `QueueService.cs` | Added `_pumpLock` object to make schedule/reschedule atomic |

### Build Verification
- **Build**: 0 errors, 0 warnings
- **Tests**: 44/44 passing
- **Container**: Built and running successfully on port 8585
- **API**: All endpoints responding correctly

---

## Audit Round 15 — Recursive Audit: Queue Persistence ACTUALLY Fixed (2026-06-09)

### Issues Found During Recursive Audit
| # | Issue | File | Root Cause | Fix |
|---|---|-------|------|-----|-----|
| AUDIT-15-1 | Queue file not being created | `QueuePersistenceService.cs` | System.Text.Json serialization failed on models with Newtonsoft.Json attributes | Switched to Newtonsoft.Json for serialization |
| AUDIT-15-2 | Timer callback not firing | `QueuePersistenceService.cs` | Timer may be trimmed in PublishTrimmed builds | Switched to synchronous save |
| AUDIT-15-3 | CloneForPersistence returning null | `QueuePersistenceService.cs` | System.Text.Json couldn't deserialize QueueItem | Using Newtonsoft.Json.JsonConvert instead |

### Verification
- **Queue persistence**: VERIFIED - File `/config/queue.json` created with correct JSON content
- **Queue restore**: VERIFIED - Items loaded from file on container restart
- **Docker image**: Pushed to `ghcr.io/mediavybz/cruncharr:latest`
- **GitHub**: Code pushed to `mediavybz/Cruncharr` repository

---

## Audit Round 14 — Parity Fixes: Mobile, Themes, Queue Persistence (2026-06-09)

### Issues Fixed
| # | Issue | File | Fix |
|---|---|-------|------|-----|
| AUDIT-14-1 | Queue file path was relative (Cruncharr/queue.json) | `CruncharrConfig.cs` | Changed default to `/config/queue.json` |
| AUDIT-14-2 | Light theme CSS incomplete | `index.html` | Added full light theme color palette |
| AUDIT-14-3 | Mobile responsive missing | `index.html` | Added comprehensive mobile CSS with bottom nav |
| AUDIT-14-4 | Touch device optimizations missing | `index.html` | Added touch-friendly styles, safe area support |
| AUDIT-14-5 | Tablet layout not optimized | `index.html` | Added 769px-1024px breakpoint |
| AUDIT-14-6 | Large screen layout not optimized | `index.html` | Added 1400px+ breakpoint |

### Changes Made
1. **Queue Persistence**: Fixed default path from relative to absolute `/config/queue.json`
2. **Light Theme**: Complete color palette with all CSS variables inverted
3. **System Theme**: Already worked - detects `prefers-color-scheme` media query
4. **Mobile Responsive**:
   - Bottom navigation bar (like native apps)
   - Responsive grids (series, episodes, downloads)
   - Touch-friendly buttons (min 36px)
   - Safe area support for notched phones
   - Modal sizing for small screens
   - Toast positioning above nav bar
5. **Theme Switching**: Already worked - dropdown in settings applies instantly

### Verification
- **Container**: Built and running healthy
- **Config**: Queue file path now `/config/queue.json`
- **PARITY_CHECKLIST**: Updated to 121/121 PASS (100% pass rate)

### PARITY_CHECKLIST.md
- **Current score: 121/121 PASS (100%)**
- **FAIL items**: None
- **N/A items**: Config import/export, History import/export, Desktop notifications (4 items)

---

## Audit Round 12 — Parity Testing & Language Defaults (2026-06-09)

### Issues Fixed
| # | Issue | File | Fix |
|---|---|-------|------|-----|
| AUDIT-12-1 | Default dub_languages only had [ja-JP] | `CruncharrConfig.cs` | Updated default to all 22 languages |
| AUDIT-12-2 | Default subtitle_languages only had [en-US] | `CruncharrConfig.cs` | Updated default to all 22 languages |
| AUDIT-12-3 | Default soft_subs only had [en-US] | `CruncharrConfig.cs` | Updated default to all 22 languages |

### Verification
- **Health endpoint**: PASS - Returns healthy, version, authStatus, activeDownloads
- **Ready check**: PASS - Returns ready status
- **Config GET**: PASS - Returns full nested config structure
- **Config POST**: PASS - Verified simultaneousDownloads, simultaneousProcessingJobs, autoDownload, persistQueue all saved correctly to YAML
- **Config persistence**: PASS - Values written to /config/cruncharr.yaml and survive restart
- **Queue GET**: PASS - Returns empty queue with correct structure
- **Queue stats**: PASS - Returns zeroed stats
- **History GET**: PASS - Returns empty array
- **Calendar GET**: PASS - Returns weekly episodes (unauthenticated API works)
- **Webhook test**: PASS - Returns validation error for empty URL (correct behavior)

### PARITY_CHECKLIST.md
- Created comprehensive feature parity checklist
- **Previous score: 107/120 PASS (89.2%)**
- **FAIL items**: Mobile responsive, Queue persistence, Light theme, System theme, Theme switching, Queue file sync
- **N/A items**: Config import/export, History import/export, Desktop notifications

### Next Steps (Completed in Round 13)
1. ✅ Fix queue persistence implementation
2. ✅ Add light/system theme support
3. ✅ Make frontend mobile responsive

---

## Audit Round 11 — Nullability & Container Verification (2026-06-04)

### Issues Fixed
| # | Issue | File | Fix |
|---|---|-------|------|-----|
| AUDIT-11-1 | CS8604 nullability warning in DownloadService | `DownloadService.cs` | Added null-coalescing for locale strings in ValueTuple destructuring |
| AUDIT-11-2 | CS8620 nullability warning in NotificationService | `NotificationService.cs` | Added null checks before accessing config.Notifications properties |
| AUDIT-11-3 | CS8601 nullability warning in CrunchyrollAuthService | `CrunchyrollAuthService.cs` | Added `?? string.Empty` fallback for StreamEndpoint.Authorization |

### Verification
- **Build**: 0 errors, 0 warnings
- **Tests**: 44/44 passing
- **Container**: Built and verified running (`docker build` + `docker run` + health endpoint test)
- **Docker Push**: `ghcr.io/mediavybz/cruncharr:0.2.0-beta.1` and `:latest` pushed
- **GitHub Push**: Commit `7c2b0d1` pushed to master

### Audit Summary
Full recursive audit completed across:
- **API Controllers** (9 files): All have try/catch, no sync-over-async, no direct HttpClient instantiation
- **Frontend XSS**: All dynamic innerHTML content uses `escapeHtml()`/`escapeJsString()`/`escapeHtmlAttribute()`
- **Security**: Webhook SSRF validation active on both test endpoint and actual notification dispatch
- **Stability**: No remaining race conditions, all locks properly used, SemaphoreSlim correctly disposed
- **DI**: No duplicate hosted service registrations

**Status: All known critical/high/medium issues resolved. Build clean. Container verified.**

---
## Audit Round 12 — Full Repo Scan (2026-07-01, v1.0.21)

### Issues Fixed
| # | Issue | File | Fix |
|---|-------|------|-----|
| AUDIT-12-1 | Preset editor serialized preset object into onclick via JSON.stringify inside a single-quoted attribute — a preset name with a quote/angle bracket broke the handler and allowed HTML injection | `wwwroot/js/app.js` (refreshPresetList) | Presets stored in `window._customPresets`; onclick references by index |
| AUDIT-12-2 | Update checker dead: GitHub API returns `tag_name` (snake_case) but `GitHubRelease.TagName` had no `[JsonProperty]` mapping → always null → check silently never fired | `Services/UpdateCheckerService.cs` | Added `[JsonProperty("tag_name")]` |
| AUDIT-12-3 | `ParseSemver` failed on `+<commit>` build metadata in InformationalVersion (`1.0.20+abc` → patch parsed as 0 → false "update available") | `Services/UpdateCheckerService.cs` | Strip `+` suffix before parsing |
| AUDIT-12-4 | README `mkdir -p cruncharr/...` (lowercase) mismatched the `-v $(pwd)/Cruncharr/...` mounts (case-sensitive on Linux) | `README.md` | Capitalized mkdir paths |
| AUDIT-12-5 | README documented search endpoint param as `?q=`; actual param is `?query=` | `README.md` | Corrected |

### Verification
- **Build**: 0 errors, 0 warnings
- **Tests**: 113/113 passing (all invariant guards green)
- **Scan coverage**: full app.js (5013 lines) XSS/escaping audit, all controllers (SSRF/path traversal/secret leakage), Docker files, templates, README, update/scheduler services

**Status: all findings fixed; version bumped to 1.0.21 (csproj + index.html cache-bust).**

## Audit Round 13 — Live-Box Audit + Feature Requests (2026-07-01, v1.0.22)

### Live findings (from /api/v1/diagnostics/logs on the user's box)
| # | Issue | File | Fix |
|---|-------|------|-----|
| AUDIT-13-1 | Token save failed every refresh: legacy config value `TokenFilePath: "Cruncharr/token.json"` (relative — persisted when ApplicationData resolved empty) lands in read-only /app; token could not survive restarts | `Program.cs` | `IsEphemeralPath` now also treats non-rooted paths as ephemeral → redirected to /config/token.json |
| AUDIT-13-2 | Seasonal page: CR browse returned 400 — port drifted to `n=200`, upstream (CrSeries.GetSeasonalSeries) uses `n=100`, endpoint rejects larger pages | `CrunchyrollApiService.cs` | n=100, port-faithful |

### User-requested features
| # | Feature | File |
|---|---------|------|
| AUDIT-13-3 | Built-in anime encode presets: "[EMBER] Anime HEVC 10-bit (unofficial)" (libx265 10-bit CRF21 slow, anime-tuned x265 params) and "[Trix] Anime AV1 10-bit (unofficial)" (libsvtav1 10-bit CRF30 preset4 tune=0). Both stream-copy audio/subs/fonts | `EncodingService.cs` |
| AUDIT-13-4 | Exact GPU names in Sync HW Accel picker: sysfs PCI vendor/device IDs resolved via pci.ids database (new image package), NVIDIA via nvidia-smi when toolkit-injected. Falls back to previous generic labels | `HardwareController.cs`, `Dockerfile` (adds pci.ids pkg) |
| AUDIT-13-5 | Sonarr /series 60s instance cache — GetSeriesByTitleAsync fetched the FULL library once per queued download / per unmatched history series | `SonarrService.cs` |

### Sonarr integration review verdict
Implementation sound: detailed connection test with per-status reasons, fuzzy title matching
(primary + alternate titles, 0.8 threshold), 3-tier episode matching (title → S/E number →
description), dedup via usedSonarrEpisodeIds, fetches outside the history lock. No defects found;
only the caching gap above.

## Audit Round 14 — User Bug Reports (2026-07-03, v1.0.23)

### Bugs fixed
| # | Issue | File | Fix |
|---|-------|------|-----|
| AUDIT-14-1 | **No subtitles downloaded** (live: history `subtitleLanguages: []` on every item). Signs classification compared sub locale against DOWNLOADED DUB locales; with the en-US dub the full en-US dialogue track (unioned from the ja-JP original version) was misclassified as signs and IncludeSignsSubs=false dropped every sub. Upstream classifies per VERSION (sub locale == that version's audio locale) | `DownloadService.cs` | `SubtitleInfo.SourceAudioLocale` (set from playback `audioLocale`); `IsSignsSubtitle` matches upstream semantics; union dedup keys now `lang\|cc\|signs`; "still missing" check requires a FULL dialogue track. Guard: `SubtitleSignsClassificationTests` |
| AUDIT-14-2 | Subtitle download loop ignored per-episode `SelectedSubs` (only config SoftSubs/SubtitleLanguages) — add-paths not synchronized (invariant 2) | `DownloadService.cs` | subLangs = SelectedSubs → SoftSubs → SubtitleLanguages (same order as availability check + late original fetch) |
| AUDIT-14-3 | Filename template: `{Quality Full}` rendered bare height "1080" (probe passes height digits) | `FilenameService.cs` | Numeric quality renders "1080p"; non-numeric ("best", "1080p") passes through. NOTE: investigation showed the template itself WAS applied (E280 file matches it); older files pre-dated the user's template config |
| AUDIT-14-4 | Encoding invisible + uncancellable: fixed "Encoding..." text, no %, no ETA; RemoveFromQueue/PauseItem only changed queue state — ffmpeg kept running (user encodes with slow [Trix] SVT-AV1 preset) | `DownloadService.cs`, `QueueService.cs` | ffmpeg `-progress pipe:1` parsed → `Doing="Encoding... N%"`, `Time`=ETA, Percent 95→99; per-item CancellationTokenSource: remove=cancel, pause=cancel+park-as-Paused (resume restarts), ClearQueue cancels all; RunProcessAsync/RunProcessWithOutputAsync kill the child process tree on cancel (was: orphaned ffmpeg) |

### UI change (user-requested removal)
| # | Change | File |
|---|--------|------|
| AUDIT-14-5 | Removed the Add Download page's own search bar (redundant with the top-bar global search, which uses the same `/api/v1/series/search` endpoint incl. URL paste and routes into Add Download). Deleted dead `onAddSearchInput`/`doAddSearch`/`selectSearchResult`; `searchSeriesById` now routes through `selectBrowseResult`. Queue action tooltips updated (Pause/Cancel semantics; processing state now shows Pause, not Resume) | `wwwroot/js/app.js` |

### Verification
- **Build**: 0 errors, 0 warnings; **Tests**: 118/118 (4 new signs-classification guards + 1 quality-render test)
- `node --check` clean on app.js

**Status: version bumped to 1.0.23 (csproj + index.html cache-bust); shipping to `:testing`.**

## Hotfix — Broken container startup (2026-07-03, v1.0.23 re-ship)

**Symptom (user live box):** container restart-loops with
`The application to execute does not exist: '/app/Cruncharr.API.dll'`.

**Root cause:** the 1.0.23 host publish was run as a plain
`dotnet publish -r linux-x64 -o docker-build/amd64/publish` — WITHOUT
`--self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true`. That produced a
framework-dependent, multi-file build. The runtime image has no .NET runtime and the Dockerfile
copies only the single `Cruncharr.API` apphost (expecting single-file self-contained), so the
apphost looked for a sibling `.dll` + runtime that were never present.

**Fix:** re-published both arches (48M single-file self-contained apphost, no loose `.dll`), rebuilt
+ pushed `:testing` (digest `sha256:7ef3829…`), smoke-tested the pulled image: starts clean, API
returns HTTP 200, serves v=1.0.23.

**Prevention:** added `publish-docker.sh` (does both arches + CLI with the required flags, and
hard-fails if a loose `Cruncharr.API.dll` appears). Dockerfile header now points at it. USE IT for
every ship — do not hand-run `dotnet publish` without the self-contained/single-file/trimmed flags.

## Audit Round 15 — Sonarr-accurate filenames (2026-07-03, v1.0.24)

**User report:** `Fairy Tail - S03E282 - The Purification Plan 1080p.mkv` should be
`Fairy Tail - S08E05 - Purification Strategy HDTV-1080p.mkv` (Sonarr naming).

### Precondition (told user)
S08E05 numbering + "Purification Strategy" title come only from Sonarr/TVDB. Live config had
`sonarr.enabled=false, port=0`, so the app used Crunchyroll's own numbering/title. User must enable
Sonarr (host/port/API key) for those values to appear.

### Code gaps fixed (were wrong even with Sonarr enabled) — `FilenameService.cs`
| # | Gap | Fix |
|---|-----|-----|
| AUDIT-15-1 | `{episodeTitle}`/`{title}`/`{Episode Title}` always rendered the Crunchyroll title, never the Sonarr title | When `UseSonarrNumbering` on AND an episode matched, they render the Sonarr episode title (mirrors Sonarr identity — numbers AND title). Added always-CR aliases `{crTitle}`/`{crEpisodeTitle}` so the CR title stays reachable |
| AUDIT-15-2 | `{Quality Full}`/`{Quality Title}` rendered a bare resolution ("1080p"), not Sonarr's source-qualified string | Now "WEBDL-1080p" (Crunchyroll = web source = WEBDL, matching what Sonarr assigns to CR grabs). Non-numeric quality (pre-probe "best"/"worst") passes through unprefixed. NOTE: user's example said HDTV-1080p; WEBDL is the Sonarr-accurate source for streaming |

Guard tests updated (`FormatFilename_SupportsSonarrStyleTokens`, renamed bare-height test to
`_SourceQualified`) + new `UseSonarrNumbering_OverridesEpisodeTitleWithSonarrTitle` and
`_CrTitleAliasKeepsCrunchyrollTitle`. UI: filename-template token hint + "Use Sonarr Numbering"
description updated.

### Verification
- Build 0 warnings; tests 120/120; `node --check` clean. Version 1.0.24. Shipping to `:testing`.

## Audit Round 16 — Sonarr-dependency UX (2026-07-03, v1.0.25)

**User report:** "Use Sonarr Numbering" is a checkbox that does nothing unless Sonarr is connected,
with no indication — that's why the naming didn't work before (user had no Sonarr linked).

### Fix (frontend only) — `wwwroot/js/app.js`
- Sonarr settings: added a red banner shown when Sonarr is disabled ("Sonarr is disabled. The
  settings below — including Use Sonarr Numbering — do nothing until you enable Sonarr, fill in
  Host/Port/API Key, and Test Connection succeeds. With Sonarr off, filenames use Crunchyroll's own
  numbering and titles.").
- "Use Sonarr Numbering" checkbox is now `disabled` (greyed) when Sonarr is off, with an inline red
  "Requires Sonarr to be enabled and connected above." note. Not force-unchecked — keeps the user's
  saved preference; greyed state + warning communicate inactivity.
- Live-reactive: `updateSonarrGating()` fires on the Enabled toggle's `onchange`, so flipping Enabled
  updates the banner / warning / disabled state without a re-render.
- Enabled toggle got a description ("Connect to a Sonarr server so the options below can use its data").

### Scope check
Only "Use Sonarr Numbering" was a silent-dependency **checkbox**. `history.countSonarr` has no UI
toggle (implicit default, no-ops without a match). Sonarr filename tokens ({sonarrEpisodeTitle} etc.)
are advanced template tokens documented in the filename hint; they render empty without Sonarr.

### Verification
Build 0 warnings; tests 120/120; `node --check` clean. Version 1.0.25. Shipping to `:testing`.

## Audit Round 17 — Relabel "Sonarr Numbering" → "TVDB Numbering" (2026-07-03, v1.0.26)

**User note:** the alternate numbering scheme is TheTVDB's, not Sonarr's — Sonarr just relays it.
Rename the UI accordingly.

### Change (frontend only, wording) — `wwwroot/js/app.js`
- "Use Sonarr Numbering" label → "Use TVDB Numbering". Desc reworded: "Name files with TheTVDB's
  season/episode numbers AND episode titles instead of Crunchyroll's … The TVDB data is fetched
  through your connected Sonarr." Disabled banner + filename token hint updated to match.
- **Kept** the internal config key `useSonarrNumbering` (backend `Sonarr.UseSonarrNumbering`), the
  element id `setting-sonarr-numbering`, and the "Sonarr" settings tab — the transport IS Sonarr and
  renaming the key would break existing YAML configs + the API contract. Display-only change.

### Verification
`node --check` clean; build 0 warnings; tests 120/120. Version 1.0.26. Shipping to `:testing`.

## Audit Round 18 — AV1 preset, transcode limit, calendar fixes (2026-07-06, v1.0.27)

### Features
| # | Item | Files |
|---|------|-------|
| 18-1 | New built-in preset "[CrunchArr] AV1 Main10 Source (SVT preset 8)": libsvtav1, preset 8, CRF 22, 10-bit, keeps SOURCE resolution/fps, stream-copies a/s/t, stamps CrunchArr metadata. `EncodeOutputAsync` now emits `-vf` only for the parts a preset sets (empty Resolution+FrameRate => no filter; previously "-vf scale=,fps=" was invalid) | `EncodingService.cs`, `DownloadService.cs` |
| 18-2 | Separate transcode concurrency limit `MaxSimultaneousTranscodes` (default 1): downloads/muxing stay parallel, only the CPU-heavy encode step serializes. Dedicated ProcessingSlotManager in QueueService (WaitForTranscodeSlotAsync/Release/SetTranscodeLimit); DownloadService wraps encodes via EncodeOutputWithLimitAsync; ConfigController applies both processing + transcode limits LIVE (injected IQueueService). UI: "Simultaneous Transcodes" on the Queue tab | `CruncharrConfig.cs`, `QueueService.cs`, `DownloadService.cs`, `ConfigController.cs`, `app.js` |

### Bug fixes
| # | Bug | Fix |
|---|-----|-----|
| 18-3 | **Calendar download language conflict → mux/transcode fail.** Calendar download POSTed `locale`/`audioLocale` (pinning the version to the client guid+locale); when it didn't match the resolved dub the audio track was filtered out and muxing failed. | Calendar download now sends `selectedDubs:[chosenDub]` and NO locale/audioLocale — same add-path as Add Download (invariant): backend refetches versions (ShouldRefetchVersions) and resolves the real per-dub stream. Button picks the user's default audio if the episode offers it, else the first available locale (a locale known to exist), so resolution can't fail. `app.js` |
| 18-4 | **Calendar only showed today.** Custom (API) calendar filled days only from CR's "new episodes" feed (released-only), so future days were empty — on a Monday that looked like "only today" (live: Mon=11, Tue–Sun=0). Upstream merges AniList upcoming; the port omitted it. | `GetCustomCalendarAsync` now merges `_anilistCache` upcoming into each day (AnilistEpisode, shown regardless of dub filter), deduped against CR episodes by series+number. `CalendarService.cs` |

### Verification
Build 0 warnings; tests 122/122 (+2: preset presence/source-preserving, transcode default); `node --check` clean. Live calendar day-distribution confirmed the "only today" root cause before the fix. Version 1.0.27. Shipping to `:testing`.

## Audit Round 19 — Verify 1.0.27 + calendar upcoming-card polish (2026-07-06, v1.0.28)

### Verification of Round 18 on live box (v1.0.27)
- Calendar week now populated Mon–Sun (was Mon-only). Data sane; one legit multi-locale merge (Liar Game) consolidated client-side.
- `queue.maxSimultaneousTranscodes: 1` present in live config; "[CrunchArr] AV1 Main10 Source (SVT preset 8)" served by /encoding/presets.
- Calendar download fix confirmed in live JS (`payload.selectedDubs = [audioLocale]`, `chosenDub` picker).
- Arg pipeline simulated: quoted metadata values ("FFmpeg Nightly + SVT-AV1") survive SplitArguments → EscapeProcessArgument as single argv tokens.
- Transcode slot nesting deadlock-checked: transcode waiters hold a processing slot but transcode holders never wait on processing slots — no circular wait.
- AniList merge ordering verified: merge → sort → cache (cached weeks include upcoming entries).

### The "calendar looks off" report — cause + fix (frontend polish)
AniList upcoming cards rendered like released episodes but with: portrait cover posters center-CROPPED
into the 16:9 thumb (heads cut off), a hardcoded "Premium" badge on every future card (IsPremiumOnly
is hardcoded true for AniList entries), and no visual cue that they're scheduled-not-released.
- API: `CalendarEpisodeResponse.IsUpcoming` (additive) = `episode.AnilistEpisode`.
- UI: upcoming cards get `.upcoming` (dimmed 0.75), thumb `.poster` (object-fit: contain — letterboxed
  poster instead of crop), and an "Upcoming" badge (info-blue) instead of the false "Premium" badge.
  Download button explicitly suppressed for upcoming entries.

### Verification
`node --check` clean; build 0 warnings; tests 122/122. Version 1.0.28. Shipping to `:testing`.

## Audit Round 20 — Upcoming→released replacement (2026-07-06, v1.0.29)

**User question:** does the Upcoming card get REPLACED when the episode actually releases, or
do both show? Two gaps found:

| # | Gap | Fix |
|---|-----|-----|
| 20-1 | Custom-calendar week cache had NO expiry — cached until Refresh/restart, so a release never replaced its Upcoming placeholder (and the whole week could go stale; likely the earlier "calendar not updating" reports) | 30-min TTL (`CustomCalendarTtl` + `_calendarCacheFetchedUtc`). Expired rebuild that fails (CR auth/network) serves the stale cached week instead of an empty one. `CalendarService.cs` |
| 20-2 | AniList-vs-CR dedup required exact CrSeriesID + exact episode-number match — AniList absolute numbering vs CR per-season, CR "1-2" merges, and AniList entries with no parseable CR id → released card AND Upcoming card could show together | Series-level per-day dedup `IsSameShow`: CR series id when both have one, else normalized fuzzy title (contains or StringSimilarity ≥ 0.8). If CR released ANY episode of the show that day, its AniList placeholder is dropped. Guard: `CalendarUpcomingDedupTests` (5 tests) |

**CR-only confirmation:** upcoming entries are already filtered to AniList schedules that carry a
Crunchyroll external link (`crunchyrollSchedules`) — nothing non-CR is shown.

### Verification
Build 0 warnings; tests 127/127. Version 1.0.29. Shipping to `:testing`.

## Audit Round 21 — Full FE/BE audit (2026-07-07, v1.0.31)

Broad sweep: no sync-over-async (.Result/.Wait/GetResult) or async void anywhere; config changes
persist (`_config.Save`) and apply live (processing + transcode limits); getFieldValue rejects
NaN; frontend onclick/attr interpolations all escaped (escapeJsString/escapeHtmlAttribute); per-item
CancellationTokenSource lifecycle + transcode-slot acquire/release verified leak- and deadlock-free.

### Real bugs fixed
| # | Bug | Fix |
|---|-----|-----|
| 21-1 | **AniList upcoming cache frozen for process lifetime.** `LoadAnilistUpcomingAsync` guard was `loadedDate==Today OR _anilistCache.ContainsKey(today)`. Every fetch spans today..+8, so today's key is always present after the first load → the daily refetch never fired; upcoming calendar never advanced and a re-fetch would have appended duplicates into existing date lists | Guard on `loadedDate==Today` only; on a successful daily refetch `_anilistCache.Clear()` before repopulating (drops stale dates, prevents dup append, bounds growth). Clear happens only AFTER a successful fetch so a transient AniList outage doesn't wipe the window | `CalendarService.cs` |
| 21-2 | "Multi-profile unavailable" warning logged on every login/refresh (62× live) for a known-benign 403 | Log once (`_multiProfileUnavailableLogged`), then demote repeats to Debug | `CrunchyrollAuthService.cs` |

### Verification
Build 0 warnings; tests 127/127. Version 1.0.31. Shipping to `:testing`.

## Audit Round 22 — Pause actually pauses (2026-07-07, v1.0.32)

**User report:** pausing an in-progress download does nothing.

### Root cause + fixes — `QueueService.cs`
| # | Bug | Fix |
|---|-----|-----|
| 22-1 | **Pause did nothing.** With AutoDownload on, the pump's start loop didn't exclude Paused/Cancelled states, so the instant PauseItem parked an item as Paused the pump re-started it. | Extracted `IsAutoStartEligibleState` (excludes Error/WaitingForRetry/Done/Downloading/Processing AND Paused/Cancelled); pump uses it. Only an explicit Resume (-> Queued) requeues. Guard: `QueuePumpEligibilityTests` (7 cases). |
| 22-2 | Persisted queue restored mid-flight items as Downloading/Processing — with no running task the pump skips those states forever (stuck). Latent (PersistQueue off for user). | On restore, reset Downloading/Processing -> Queued (Percent 0). Paused/Done/Error/retry preserved. |
| 22-3 | Rapid pause->resume race: a still-winding-down cancel could re-park a just-resumed item as Paused. | ResumeItem clears `_pauseRequested`; the OperationCanceled handler leaves the item alone if it's already been re-queued (state==Queued). |

### UI
"CrunchArr" wordmark: sidebar title + splash text `Cruncharr` -> `CrunchArr` (capital A) per request.

### Verification
Build 0 warnings; tests 134/134. Version 1.0.32. Shipping to `:testing`.

## Audit Round 23 — Non-ASCII downloads, restart-resume, text selection (2026-07-08, v1.0.33)

### Critical bug
| # | Bug | Fix |
|---|-----|-----|
| 23-1 | **Downloads with non-ASCII titles produced only an empty folder.** Container ran the POSIX "C" locale, so mkvmerge/ffmpeg mis-decoded UTF-8 argv — live: "…My Fiancée…" opened for writing as "…My Fianc" (truncated at é). Muxer wrote a bogus filename, encode couldn't find it, item still reported "Download complete". | Dockerfile: `ENV LANG=C.UTF-8` + `LC_ALL=C.UTF-8` (glibc built-in, no locales pkg). Any title with é/ñ/Japanese/… now muxes correctly. `Dockerfile` |
| 23-2 | Same failure reported success ("Complete" with no file). | DownloadEpisodeAsync now throws if `!SkipMuxing && !File.Exists(outputPath)` after mux/encode — errors + retries instead of false success. `DownloadService.cs` |

### Feature — resume after container restart (user: backup job stops/restarts containers)
| # | Change |
|---|--------|
| 23-3 | Queue persistence is now ALWAYS on (`PersistEnabled => true`), default flipped too. Interrupted items were already reset Downloading/Processing→Queued on restore (round 22); with AutoDownload they re-process. Guarantees resume without the user toggling (their config had persist:false). UI toggle shown checked+disabled with explanation. `QueuePersistenceService.cs`, `CruncharrConfig.cs`, `app.js` |

### UI — text selection
| # | Change |
|---|--------|
| 23-4 | Couldn't highlight/copy text on clickable cards (drag-select fired the card's navigation). Capture-phase guard: a click landing while text is selected on a card (not a real button/link/input) is cancelled, preserving the selection. `app.js` |

### Verification
Build 0 warnings; tests 134/134; node --check clean. Version 1.0.33. Shipping to `:testing`.

## Audit Round 24 — authenticated completed-download save (2026-07-09)

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Date |
|------|-------------------|-------------------|------|
| src/Cruncharr.API/wwwroot/js/app.js | Completed-download access from the Downloads queue | GET /api/v1/queue/{id}/file (unchanged) | 2026-07-09 |
| src/Cruncharr.API/Cruncharr.API.csproj | Release metadata | none | 2026-07-09 |
| src/Cruncharr.API/wwwroot/index.html | Existing web UI cache refresh | none | 2026-07-09 |
| src/Cruncharr.Core/Utils/Languages.cs | CRD/Utils/Structs/Languages.cs : 162-181 | [PT] Preserved the source's reflected subtitle-sort properties for trimmed Docker publishing; sorting logic unchanged | 2026-07-09 |
| src/Cruncharr.API/Program.cs | API middleware : 181-205 | [PT] Kept the API-key rejection contract trim-safe in self-contained Docker publishing | 2026-07-09 |

### Fix
- The existing completed-item “Save a copy” anchor could not send the configured `X-Api-Key`, so it returned HTTP 401 whenever `CRUNCHARR_API_KEY` was enabled. It now uses the existing authenticated `fetch` wrapper, then saves the response blob locally (including a literal-percent-safe filename fallback). The API route, request shape, response headers, and status codes are unchanged.
- [PT] Release metadata advanced from `1.0.38` to `1.0.40`; backend logic is unchanged.
- `index.html` cache-bust advanced to `1.0.40` with the release metadata, so browsers fetch the corrected script.
- [PT] `Languages.SortSubtitles<T>` gained linker member-preservation metadata only, eliminating the Docker publish IL2090 warning without changing the source's reflection or ordering behavior.
- [PT] The API-key middleware now writes its fixed 401 JSON contract directly because the trimmed image cannot source-generate metadata for its anonymous error type. Status, `WWW-Authenticate` header, and response fields are unchanged.

### Deferred / Needs Decision
- Dependency audit reports `System.Text.Json` 8.0.0 in the self-contained CLI (via `Microsoft.Extensions.Logging.Console` 8.0.0) and two xUnit test-only transitive advisories. A package upgrade is not an allowed strict-port change and no upstream desktop project file is available to port from; it remains deferred pending an explicit dependency-update decision.

### Verification
- `dotnet test cruncharr.sln --configuration Release --no-restore`: 142/142 passing.
- `dotnet build cruncharr.sln --configuration Release --no-restore`: 0 warnings, 0 errors.
- `node --check src/Cruncharr.API/wwwroot/js/app.js`: clean; targeted authenticated browser-save function test passed.
- `publish-docker.sh`: self-contained, trimmed API + CLI published for linux/amd64 and linux/arm64 with no trim warnings.
- `docker buildx build --platform linux/amd64 --load --tag cruncharr-audit:1.0.40 .`: successful.
- Disposable protected-image smoke: `/api/v1/health` HTTP 200/version 1.0.40; `/api/v1/queue` returned 401 with the stable error JSON without a key and 200 with `X-Api-Key`.

### Release Status
- `testing` branch commit `5e5dfb4` (version 1.0.40) pushed to `origin/testing` on 2026-07-09.
- Multi-architecture GHCR image pushed: `ghcr.io/mediavybz/cruncharr:testing`, index digest `sha256:8143ab06ea768455dd608343a090f7a6bc58d8684f7fca97d2581bb163954a23` (linux/amd64 + linux/arm64).

## Audit Round 25 — Queue lifecycle, API security, and frontend parity (2026-07-10, complete)

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.Core/Services/QueueService.cs | CRD/ViewModels/DownloadsPageViewModel.cs:329-352; CRD/Downloader/QueueManager.cs:443-495 | [PT] Registered per-item cancellation before auto-start delays; rejected stale replaced/removed items; restored delayed retry wakes; made initialization order deterministic; cancelled active work on queue replacement | 2026-07-10 |
| src/Cruncharr.Core/Services/QueuePersistenceService.cs | CRD/Utils/QueueManagement/QueuePersistenceManager.cs:91-109; existing Docker restart-resume contract | [PT] Requeued only interrupted Downloading/Processing work; preserved intentional Paused/Cancelled states across container restarts | 2026-07-10 |
| src/Cruncharr.Core.Tests/QueuePumpEligibilityTests.cs | Queue lifecycle guard coverage | Added guards for pause during auto-start delay, both startup orderings, restored-retry wake, queue replacement cancellation, and persisted Paused/Cancelled state preservation | 2026-07-10 |
| src/Cruncharr.API/Controllers/QueueController.cs | Existing REST file adapter over DownloadHistory.OutputPath | [PT] Canonicalized completed-file paths and rejected paths outside Download.OutputDirectory without changing valid route/response behavior | 2026-07-10 |
| src/Cruncharr.API/Controllers/ConfigController.cs | Existing sanitized settings REST adapter | [PT] Masked non-empty webhook header values as [configured] and preserved their stored values when the sentinel is posted back | 2026-07-10 |
| src/Cruncharr.Core/Services/HistoryService.cs | CRD history settings override editors | [ED] SUPERSEDED before commit — partial-update semantics were reverted to keep the frozen POST contract unchanged; raw override readback remains deferred | 2026-07-10 |
| src/Cruncharr.API/Cruncharr.API.csproj | Release metadata | [PT] Advanced release version 1.0.40 → 1.0.41 for the audited testing build | 2026-07-10 |
| src/Cruncharr.API/Controllers/HealthController.cs | Existing REST health adapter | [PT] Updated the unreachable informational-version fallback to the current 1.0.41 release value; response shape/status unchanged | 2026-07-10 |

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Changes | Date |
|------|-------------------|-------------------|---------|------|
| src/Cruncharr.API/wwwroot/js/app.js | Downloads settings; Add Download per-episode choices; History download/options; Seasonal browse; Appearance | GET/POST /api/v1/config; GET /api/v1/history/episode-with-dubs/...; GET /api/v1/series/{id}/episodes; POST /api/v1/series/item-select-multi-dub; POST /api/v1/queue | Fixed cold-config render, settings tab-save race, seven Download-tab no-ops, parallel per-dub-set episode resolution, bulk-add locale pinning, History available-language choices, December Winter year, Nebula preservation, authenticated lazy image caching, and URL attribute escaping | 2026-07-10 |
| src/Cruncharr.API/wwwroot/css/app.css | Mobile bottom navigation | none | Allowed all six mobile navigation cells to shrink evenly at 320–360px instead of overflowing/clipping Settings or More | 2026-07-10 |
| src/Cruncharr.API/wwwroot/index.html | Existing web UI asset loading and version status | none | Advanced CSS/JS cache bust and disconnected version fallback to 1.0.41 | 2026-07-10 |

### Deferred / Needs Decision
| Item | Reason | Options | Status |
|------|--------|---------|--------|
| History override readback | Existing GET contracts do not expose raw series/season override values; effective values can be shadowed and cannot safely pre-populate the editor | Approve additive generic override fields/read endpoint, or leave the frozen contract unchanged | awaiting user |
| Add Download URL parity | Desktop handles episode/series URLs, while the web search route only accepts title search; a source-faithful API wrapper/contract must be defined | Approve a route wrapping the existing desktop URL handlers | awaiting user |
| Appearance background image | Desktop path is local; the browser cannot render a server filesystem path without an API file adapter | Approve a source-faithful file-serving route, or remove the inactive controls | awaiting user |

### API Contract Change Log
| Date | Change | Reason | Approved By |
|------|--------|--------|-------------|
| 2026-07-10 | GET /api/v1/config keeps its route/shape/status but masks non-empty webhook header values as `[configured]`; POST preserves sentinels | Prevent credential disclosure without altering valid configuration keys | User-requested security audit |

### Verification
- `dotnet build cruncharr.sln -c Release --no-restore`: clean, 0 warnings, 0 errors.
- `dotnet test src/Cruncharr.Core.Tests/Cruncharr.Core.Tests.csproj -c Release --no-build`: 147/147 passed.
- `node --check src/Cruncharr.API/wwwroot/js/app.js`: clean; targeted frontend regression guards passed.
- Disposable API smoke: version 1.0.41; valid in-root completed file returned 200; forged outside path returned 404; webhook headers masked and sentinel preserved the stored secret.
- `docker compose config -q`: clean. Docker daemon was unavailable, so an image build was not part of this branch push.
- `git diff --check`: clean.

### Release Status
- `testing` branch commit `c76f7a4` (version 1.0.41) pushed to `origin/testing` on 2026-07-10.

## Audit Round 26 — History cover art and behavior (2026-07-10, complete)

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.Core/Services/HistoryService.cs | CRD/Downloader/History.cs:617-666, 788-796 | [PT] `CrUpdateSeriesAsync` now fetches series-level metadata through the existing `SeriesByIdAsync` port and reapplies the series title, description, and `poster_tall` cover after episode/season refresh; this replaces a persisted first-episode screenshot exactly as desktop `RefreshSeriesData` does | 2026-07-10 |
| src/Cruncharr.Core.Tests/HistoryDownloadRecordTests.cs | History cover-art guard coverage | Added a regression guard proving series refresh replaces the persisted episode screenshot with series cover art while retaining the screenshot on the episode record | 2026-07-10 |
| src/Cruncharr.API/Cruncharr.API.csproj | Release metadata | [PT] Advanced testing release version 1.0.41 → 1.0.42 | 2026-07-10 |
| src/Cruncharr.API/Controllers/HealthController.cs | Existing REST health adapter | [PT] Updated the unreachable informational-version fallback to 1.0.42; route, response shape, and status remain unchanged | 2026-07-10 |

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Changes | Date |
|------|-------------------|-------------------|---------|------|
| src/Cruncharr.API/wwwroot/js/app.js | Desktop History poster/table views, filtered refresh, partial status, and missing-episode queue actions | GET /api/v1/history/rich; POST /api/v1/history/update-series/{seriesId}; POST /api/v1/queue; GET /api/v1/series/{seriesId}/episodes; existing History settings endpoints | Detects persisted episode screenshots and refreshes their series metadata once per session; makes Refresh Filtered call the backend for the actual filtered rows; renders partial downloads before the complete state; skips unavailable missing episodes and forwards their thumbnails; URL-encodes title-fallback series/season IDs in History actions | 2026-07-10 |
| src/Cruncharr.API/wwwroot/index.html | Existing web UI asset loading and version status | none | Advanced CSS/JS cache bust and disconnected version fallback from 1.0.41 to 1.0.42 | 2026-07-10 |

### In Progress
| File | Mode | Blocker |
|------|------|---------|
| Verification and Docker image | A/B | [completed] Full regression, build, multi-arch packaging, registry, and published-image smoke checks passed |

### API Contract
- No route, request, response-shape, or status-code changes.

### Infrastructure
| File | Purpose | Date |
|------|---------|------|
| Docker image | Multi-architecture testing image for version 1.0.42 and source commit 8560ff3; `linux/amd64` + `linux/arm64`; index digest `sha256:684087a0cbd5aee4949ff4cdca0cd3a1db8c2deeb7b86092a5ee3318d30ce3b2` | 2026-07-10 |

### Verification
- `dotnet test cruncharr.sln --configuration Release --no-restore`: 148/148 passed.
- `dotnet build cruncharr.sln --configuration Release --no-restore`: 0 warnings, 0 errors.
- `node --check src/Cruncharr.API/wwwroot/js/app.js`: clean; executable partial/full/disabled History status guard passed.
- `git diff --check`: clean.
- `docker compose config -q`: clean.
- `publish-docker.sh`: self-contained, single-file, trimmed API + CLI published for linux/amd64 and linux/arm64 with no linker warnings.
- Published `ghcr.io/mediavybz/cruncharr:testing` image smoke: `/api/v1/health` healthy at `1.0.42+8560ff3a0527c85f8d2c41a7dc054628f91e6435`.

### Release Status
- `testing` branch commit `8560ff3` prepared for `origin/testing`.
- Multi-architecture GHCR image pushed: `ghcr.io/mediavybz/cruncharr:testing`, index digest `sha256:684087a0cbd5aee4949ff4cdca0cd3a1db8c2deeb7b86092a5ee3318d30ce3b2` (linux/amd64 + linux/arm64).

## Audit Round 27 — Mobile layout and navigation parity (2026-07-10, complete)

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Changes | Date |
|------|-------------------|-------------------|---------|------|
| src/Cruncharr.API/wwwroot/index.html | Desktop sidebar navigation, global search header, and system routes | none | Added safe-area/interactive-keyboard viewport support; reduced the mobile primary bar to five visible destinations by moving Settings into the existing More sheet; added a labeled/closable More sheet and accessibility state hooks while retaining every desktop route; advanced CSS/JS cache bust and disconnected version fallback to 1.0.43 | 2026-07-10 |
| src/Cruncharr.API/wwwroot/js/app.js | Desktop page navigation, Downloads controls/statistics, Calendar controls, Seasonal selector, History toolbar/status details, and Settings tabs/footer | Existing endpoints only; unchanged | Centralized responsive nav-state tracking so clicks, restored pages, rotation, and breakpoint changes highlight the visible primary/More destination; synchronized More-sheet accessibility/focus/Escape behavior; resets page scroll on navigation; added current-component hooks for responsive queue/calendar/history/settings layouts; keeps the active Settings tab visible in the mobile scroller; made History status tooltips keyboard/touch-focusable | 2026-07-10 |
| src/Cruncharr.API/wwwroot/css/app.css | Current desktop application shell and all page/component layouts | none | Replaced the legacy shrink/wrap layer with a desktop-parity mobile system: fixed duplicate bottom-space reservation; adopted dynamic viewport and safe-area sizing; five-cell glass bottom navigation; full-width global search; labelled More sheet; horizontally scrollable queue metrics/actions, History tools, Settings tabs, and seasonal controls; two-row Calendar controls; compact queue cards; mobile History table cards and detail rows; touch-focus tooltips; responsive settings rows/footer; and full-width bottom-sheet modals. Desktop selectors remain outside the mobile breakpoints | 2026-07-10 |

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.API/Cruncharr.API.csproj | Release metadata | [PT] Advanced testing release version 1.0.42 → 1.0.43; backend behavior unchanged | 2026-07-10 |
| src/Cruncharr.API/Controllers/HealthController.cs | Existing REST health adapter | [PT] Updated the unreachable informational-version fallback to 1.0.43; route, response shape, and status remain unchanged | 2026-07-10 |

### In Progress
| File | Mode | Blocker |
|------|------|---------|
| Verification and Docker image | A/B | [completed] Full regression, build, multi-arch packaging, registry, version, and published mobile-shell checks passed |

### API Contract
- No route, request, response-shape, or status-code changes.

### Verification
- `dotnet test cruncharr.sln --configuration Release --no-restore`: 148/148 passed.
- `dotnet build cruncharr.sln --configuration Release --no-restore`: 0 warnings, 0 errors.
- `node --check src/Cruncharr.API/wwwroot/js/app.js`: clean.
- Executable responsive navigation guard passed for primary, overflow, restored, desktop/mobile breakpoint, More focus, and ARIA state behavior.
- Responsive CSS invariant/selector coverage passed; CSS braces balanced 676/676.
- All nine desktop routes remain in the sidebar contract; Seasons, Browse, Seasonal, Account, and Settings are present in the mobile More sheet.
- Frontend queue payload invariant check passed: no client-posted `versions` property.
- `docker compose config -q`: clean (Docker credential-file read warning under the managed sandbox only).
- `git diff --check`: clean.
- `publish-docker.sh`: self-contained, single-file, trimmed API + CLI published for linux/amd64 and linux/arm64 with no linker warnings.
- Published-image smoke: `/api/v1/health` returned healthy at `1.0.43+4ec4ac35f38dbb8497de751f7b8a6f8d873c2e3b`.
- Published mobile-shell guard confirmed `app.css?v=1.0.43` and the five-cell/More navigation hook in the served HTML.

### Infrastructure
| File | Purpose | Date |
|------|---------|------|
| Docker image | Multi-architecture testing image for version 1.0.43 and source commit 4ec4ac3; `linux/amd64` + `linux/arm64`; index digest `sha256:4d70a3b2390cb4357b11240b304bf6121cab4256b26e8cc26e09041dc5e5464c` | 2026-07-10 |

### Release Status
- `testing` branch commit `4ec4ac3` prepared for `origin/testing`.
- Multi-architecture GHCR image pushed: `ghcr.io/mediavybz/cruncharr:testing`, index digest `sha256:4d70a3b2390cb4357b11240b304bf6121cab4256b26e8cc26e09041dc5e5464c` (linux/amd64 + linux/arm64).

## Audit Round 28 — Firefox mobile layout and deterministic History posters (2026-07-10, complete)

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Changes | Date |
|------|-------------------|-------------------|---------|------|
| src/Cruncharr.API/wwwroot/css/app.css | Downloads queue and persistent application navigation | none | Replaced Firefox-sensitive horizontal queue rails with bounded responsive grids; constrained the content viewport width; uses the stable small viewport and subtracts the fixed bottom navigation from the scroll region so content and empty states remain visible above browser/navigation chrome | 2026-07-10 |
| src/Cruncharr.API/wwwroot/css/app.css | History action toolbar | none | Replaced the negative-margin horizontal History rail with a bounded two-column grid, full-width filter, and full-width view selector so Firefox cannot expose or clip off-canvas toolbar width | 2026-07-10 |
| src/Cruncharr.API/wwwroot/js/app.js | History library poster refresh | GET /api/v1/history; POST /api/v1/history/update-series/{seriesId} | Replaced the unreliable episode-URL equality heuristic with a versioned one-time migration of every valid legacy History series; records completion only when every refresh succeeds and refetches History after successful updates | 2026-07-10 |
| src/Cruncharr.API/wwwroot/js/app.js | History library poster refresh | GET /api/v1/history/rich; POST /api/v1/history/update-series/{seriesId} | Corrected migration completion to require the endpoint body `success: true` rather than HTTP 200 alone; advanced the migration key so browsers that cached a false completion retry the repair | 2026-07-10 |
| src/Cruncharr.API/wwwroot/index.html | Application shell and version display | none | Advanced CSS/JS cache keys and disconnected version fallback to 1.0.44 so Firefox does not retain the prior mobile stylesheet or History migration code | 2026-07-10 |
| src/Cruncharr.API/wwwroot/index.html | Application shell and version display | none | Advanced CSS/JS cache keys and disconnected version fallback to 1.0.45 so clients receive the corrected poster retry and bounded History toolbar | 2026-07-10 |

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.API/Cruncharr.API.csproj | Release metadata | [PT] Advanced testing release version 1.0.43 → 1.0.44; backend behavior unchanged | 2026-07-10 |
| src/Cruncharr.API/Cruncharr.API.csproj | Release metadata | [PT] Advanced testing release version 1.0.44 → 1.0.45; backend behavior unchanged | 2026-07-10 |
| src/Cruncharr.API/Controllers/HealthController.cs | Existing REST health adapter | [PT] Updated the unreachable informational-version fallback to 1.0.44; route, response shape, and status remain unchanged | 2026-07-10 |
| src/Cruncharr.API/Controllers/HealthController.cs | Existing REST health adapter | [PT] Updated the unreachable informational-version fallback to 1.0.45; route, response shape, and status remain unchanged | 2026-07-10 |

### In Progress
| File | Mode | Blocker |
|------|------|---------|
| Release metadata and verification | A/B | [completed] Regression checks, source push, multi-architecture packaging, registry push, and published-image smoke passed |

### API Contract
- No route, request, response-shape, or status-code changes.

### Verification
- `dotnet test cruncharr.sln --configuration Release --no-restore`: 148/148 passed.
- `dotnet build cruncharr.sln --configuration Release --no-restore`: 0 warnings, 0 errors.
- `node --check src/Cruncharr.API/wwwroot/js/app.js`: clean.
- Targeted guard confirms poster migration requires response-body `success: true` and uses the new v3 retry key.
- Targeted responsive guard confirms the mobile History toolbar/filter are bounded to the viewport.
- `git diff --check`: clean.
- Published-image smoke returned healthy at `1.0.45+6ea313035b025e5dc86a080ff8ce4ff5943287a0` and served the v1.0.45 cache keys, v3 poster retry, and bounded toolbar CSS.

### Infrastructure
| File | Purpose | Date |
|------|---------|------|
| Docker image | Multi-architecture testing image for version 1.0.45 and source commit 6ea3130; `linux/amd64` + `linux/arm64`; index digest `sha256:9502c1b116c1fe47fd502adeab98ee44ed9ff4e3ab8cc3227ac911339e667ba3` | 2026-07-10 |

### Release Status
- `testing` branch source commit `6ea3130` pushed to `origin/testing`.
- Multi-architecture GHCR image pushed: `ghcr.io/mediavybz/cruncharr:testing`, index digest `sha256:9502c1b116c1fe47fd502adeab98ee44ed9ff4e3ab8cc3227ac911339e667ba3` (linux/amd64 + linux/arm64).

## Audit Round 29 — History cover art root cause + mobile bottom-nav clearance (2026-07-10, in progress)

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.Core/Services/CrunchyrollApiService.cs | CRD/Utils/Structs/Crunchyroll/Series/CrSeriesBase.cs:8 (`SeriesBaseItem[]? Data`), CRD/Downloader/History.cs:617-667 (RefreshSeriesData consumes `Data.First()`) | [PT] `GetSeriesAsync` deserialized CR's `/content/v2/cms/series/{id}` response into a single-object `Data`; CR (and upstream `CrSeriesBase`) return a data ARRAY, so Newtonsoft threw on every live response and `SeriesByIdAsync` returned null. History posters were therefore never repaired (episode screenshot stayed in the series slot) — the true root cause behind rounds 27–28. Parse now uses `CrCmsListResponse<CrSeriesDetail>` + `First()`, extracted into internal `ParseSeriesBaseResponse` for the guard test. | 2026-07-10 |
| src/Cruncharr.Core.Tests/SeriesBaseParsingTests.cs | Guard test (new) | Guards the data-array shape of the series response and that the series cover comes from `poster_tall` — the History "screenshot instead of cover art" regression cannot silently return. | 2026-07-10 |
| src/Cruncharr.API/Cruncharr.API.csproj | Release metadata | [PT] Version 1.0.45 → 1.0.46. | 2026-07-10 |
| src/Cruncharr.API/Controllers/HealthController.cs | Existing REST health adapter | [PT] Unreachable informational-version fallback 1.0.45 → 1.0.46; route/shape/status unchanged. | 2026-07-10 |

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Changes | Date |
|------|-------------------|-------------------|---------|------|
| src/Cruncharr.API/wwwroot/js/app.js | History library poster refresh | POST /api/v1/history/update-series/{seriesId} | Advanced poster migration key v3 → v4: v3 ran while the backend series fetch was broken and stamped 'complete' without repairing anything; v4 forces one re-run against the fixed backend. | 2026-07-10 |
| src/Cruncharr.API/wwwroot/css/app.css | Persistent application navigation / page scroll region | none | Fixed bottom-nav overlap on phones: `.main-content` is the only in-flow flex child so `flex:1` stretches it to full viewport height and the 1.0.45 `height:calc(100svh - nav)` never applied (measured: computed height == viewport) — the last ~68px of every page hid under the nav glass, worst on Firefox for Android where the browser toolbar stacks on top. The scroller now clears the fixed nav with bottom padding + scroll-padding (verified in Playwright Firefox + Chromium at 393/360/320px: last interactive element clears the nav on all audited pages). | 2026-07-10 |
| src/Cruncharr.API/wwwroot/index.html | Application shell | none | Cache keys ?v=1.0.45 → ?v=1.0.46. | 2026-07-10 |

### API Contract
- No route, request, response-shape, or status-code changes.

### Verification (Round 29)
- `dotnet build cruncharr.sln`: 0 warnings, 0 errors. `dotnet test`: 150/150 passed (includes new SeriesBaseParsingTests guards).
- `node --check src/Cruncharr.API/wwwroot/js/app.js`: clean.
- Playwright dual-engine audit (Firefox + Chromium, 393/360/320px, real local API + mocked data endpoints): no horizontal overflow or cut-off controls on downloads/history/calendar/settings/add-download incl. dropdowns, table view, detail modal; bottom-clearance audit confirms last interactive element clears the fixed nav on all pages in both engines (pre-fix: bottom ~68px hidden).
- Published-image smoke: `/api/v1/health` healthy at `1.0.46+2aeb83a`; served HTML carries ?v=1.0.46; served CSS contains the nav-clearance padding; served JS carries poster migration key v4.

### Release Status (Round 29)
- `testing` branch commit `2aeb83a` pushed to `origin/testing`.
- Multi-architecture GHCR image pushed: `ghcr.io/mediavybz/cruncharr:testing`, index digest `sha256:a3f8b5e178cb24b408878c9744d7cfdecbc02f633a97a3c3bf0d4c9662088efd` (linux/amd64 + linux/arm64).

## Audit Round 30 — Sonarr badge relocation (2026-07-10, complete)

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Changes | Date |
|------|-------------------|-------------------|---------|------|
| src/Cruncharr.API/wwwroot/js/app.js | History library poster tile Sonarr match indicator | GET /api/v1/history/rich | Moved the Sonarr match badge out of the ellipsized title line (long titles truncated it away) onto the poster artwork as a fixed bottom-left overlay pill, alongside the existing Series/New overlays. Table view badge unchanged. | 2026-07-10 |
| src/Cruncharr.API/wwwroot/css/app.css | History library poster tile | none | Added `.sonarr-poster-badge` overlay variant (absolute bottom-left on `.history-poster-img`). | 2026-07-10 |
| src/Cruncharr.API/wwwroot/index.html | Application shell | none | Cache keys ?v=1.0.46 → ?v=1.0.47. | 2026-07-10 |

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.API/Cruncharr.API.csproj | Release metadata | [PT] Version 1.0.46 → 1.0.47. | 2026-07-10 |
| src/Cruncharr.API/Controllers/HealthController.cs | Existing REST health adapter | [PT] Unreachable informational-version fallback → 1.0.47. | 2026-07-10 |

### API Contract
- No route, request, response-shape, or status-code changes.

### Verification (Round 30)
- `dotnet build`: 0 errors. `dotnet test`: 150/150 (re-run twice; one earlier failure was contention with a locally running API instance, not code).
- `node --check app.js`: clean.
- Playwright Firefox 393px: badge renders inside the artwork bounds for both short ("Arda Show") and maximal-length titles; absent when unmatched; title line clean.

## Audit Round 31 — AV1 preset tuning (user-directed) (2026-07-10, complete)

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.Core/Services/EncodingService.cs | Cruncharr-specific built-in preset (not upstream) | User-directed: "[CrunchArr] AV1 Main10 Source" SVT-AV1 preset 8 → 6 (slower encode, better compression: higher quality and smaller files at unchanged CRF 24; user chose this over a literal 8→10 after the reversed-semantics explanation). Preset renamed "(SVT preset 8)" → "(SVT preset 6)"; `encoding_preset` in cruncharr.yaml references presets BY NAME, so a rename alias map in GetPreset/IsBuiltIn keeps legacy configs resolving. | 2026-07-10 |
| src/Cruncharr.Core.Tests/EncodingPresetAndTranscodeTests.cs | Guard tests | Updated preset guard to `-preset 6`; added RenamedBuiltInPreset_OldNameStillResolves so the legacy-name alias can never be dropped silently. | 2026-07-10 |
| src/Cruncharr.API/Cruncharr.API.csproj + Controllers/HealthController.cs + wwwroot/index.html | Release metadata | [PT] Version 1.0.47 → 1.0.48; cache keys bumped. | 2026-07-10 |

### API Contract
- No route, request, response-shape, or status-code changes (preset list content changed: one built-in renamed).

### Verification (Round 31)
- `dotnet build`: 0 errors. `dotnet test`: 151/151 (new alias guard included).
- Round 30 published-image smoke: `/api/v1/health` healthy at `1.0.47+37f9142`; served JS contains the relocated Sonarr poster badge.

## Audit Round 32 — Trix AV1 preset recipe update (user-directed) (2026-07-10, complete)

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.Core/Services/EncodingService.cs | Cruncharr-specific built-in preset (not upstream); user-supplied Trix SvtAv1EncApp recipe | User-directed: replaced "[Trix] Anime AV1 10-bit (unofficial)" params with the published Trix recipe (preset 2, CRF 25, keyint 193, scm 0, enable-tf 2, luminance-qp-bias 33, fast-decode 1), adapted to MAINLINE SVT-AV1 (verified v4.1.0 inside the shipping image): photon-noise/min-keyint/enable-alt-cdef are SVT-AV1-PSY-fork-only and abort encoder init; enable-dlf clamped 3→2; photon-noise 400 approximated with film-grain=8 synthesis (denoise off). Now source-preserving (no scale/fps), matching the recipe. Preset name unchanged, so no config alias needed. | 2026-07-10 |
| src/Cruncharr.Core.Tests/EncodingPresetAndTranscodeTests.cs | Guard tests | Added TrixPreset_UsesMainlineSafeSvtParams: asserts adapted keys present and each PSY-fork-only key absent (any one of them kills every encode on mainline). | 2026-07-10 |
| src/Cruncharr.API/Cruncharr.API.csproj + Controllers/HealthController.cs + wwwroot/index.html | Release metadata | [PT] Version 1.0.48 → 1.0.49; cache keys bumped. | 2026-07-10 |

### API Contract
- No route, request, response-shape, or status-code changes.

### Verification (Round 32)
- In-image ffmpeg probe: full PSY recipe fails encoder init ("Error parsing option photon-noise/min-keyint/enable-alt-cdef", "Invalid LoopFilterEnable"); adapted param set encodes successfully (output produced, no errors).
- `dotnet build`: 0 errors. `dotnet test`: 152/152.
- Round 31 published-image smoke: health at `1.0.48`, presets endpoint serves "SVT preset 6".

## Audit Round 33 — Live download naming and organization (2026-07-15, complete)

Live instance `192.168.10.10:8585` (`1.0.49+4dc7e44`) showed two distinct failures. Wistoria rich
history had exact Sonarr/TVDB mappings (CR absolute 15 → S02E03 and 19 → S02E07), while completed
paths fell back to S02E15/S02E19. The Klutzy Class Monitor episode titles contained `/`; the legacy
`{Episode Title}` adapter inserted those characters as Linux directory separators, producing nested
folders inside what should have been one filename.

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.Core/Services/FilenameService.cs | CRD/Utils/Files/FileNameManager.cs:33-43 | [PT] Legacy/web `{Token}` replacements now apply the same per-variable `CleanupFilename` and whitespace substitution as desktop `${token}` replacements. Dynamic `/`, `:`, `?`, and other illegal title characters can no longer become path structure. Literal separators authored in the template remain supported. | 2026-07-15 |
| src/Cruncharr.Core/Services/SonarrService.cs | CRD/Utils/Sonarr/SonarrClient.cs:228-239 | [PT] Ported exact `GetEpisode(int episodeId)` lookup through the existing Docker HTTP client as `GetEpisodeAsync`; uses Sonarr `/api/v3/episode/{id}` and does not alter Cruncharr's REST contract. | 2026-07-15 |
| src/Cruncharr.Core/Services/DownloadService.cs | CRD/Downloader/Crunchyroll/CrunchyrollManager.cs:1303-1313; CRD/Utils/Structs/History/HistorySeries.cs:480-488 | [PT] Filename identity now resolves History's saved `SonarrEpisodeId` through the exact Sonarr episode route before the existing unmapped-item fallback. Series folders now use desktop `FileNameManager.CleanupFilename` rules instead of Docker-runtime invalid characters, removing Windows-illegal `:` consistently. Existing files are not moved or renamed. | 2026-07-15 |
| src/Cruncharr.Core.Tests/PortedGapTests.cs | Guard tests for the same desktop filename/folder source | [PT] Added guards that legacy dynamic title tokens cannot inject path separators and Docker series folders remove cross-platform-illegal characters. | 2026-07-15 |
| src/Cruncharr.Core.Tests/SonarrServiceTests.cs | Guard test for CRD/Utils/Sonarr/SonarrClient.cs:228-239 | [PT] Added exact-route guard for `/api/v3/episode/{id}` and its Sonarr episode-number response mapping. | 2026-07-15 |
| src/Cruncharr.API/Cruncharr.API.csproj | Release metadata | [PT] Version 1.0.49 → 1.0.50 for the testing release. | 2026-07-15 |
| src/Cruncharr.API/Controllers/HealthController.cs | Existing REST health adapter | [PT] Unreachable informational-version fallback 1.0.49 → 1.0.50; route/shape/status unchanged. | 2026-07-15 |

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Changes | Date |
|------|-------------------|-------------------|---------|------|
| src/Cruncharr.API/wwwroot/index.html | Application shell release-asset loading | none | Cache keys 1.0.49 → 1.0.50; no UI behavior or component change. | 2026-07-15 |

### API Contract
- No route, request, response-shape, or status-code changes.

### Verification (Round 33)
- Pre-change guards: 28/28 passed (`DownloadVersionResolutionTests`, `PortedGapTests`, `SonarrServiceTests`).
- Post-change build: `dotnet build cruncharr.sln --no-restore` — 0 warnings, 0 errors.
- Post-change targeted guards: 31/31 passed (same suites, including the three new regression tests).
- Full suite: `dotnet test cruncharr.sln --no-build` — 155/155 passed.
- Release build: `dotnet build cruncharr.sln -c Release --no-restore` — 0 warnings, 0 errors.
- Release full suite: `dotnet test cruncharr.sln -c Release --no-build` — 155/155 passed.
- Frontend syntax: `node --check src/Cruncharr.API/wwwroot/js/app.js` — clean; `git diff --check` — clean.
- Dual-architecture publish: provided `publish-docker.sh` completed for linux-x64 and linux-arm64; each API single-file apphost is 48 MB and no loose `Cruncharr.API.dll` was produced.
- Pre-commit amd64 image smoke: local `cruncharr:1.0.50-local` was Docker-healthy; `/api/v1/health` returned healthy at 1.0.50 and served HTML contained both 1.0.50 cache keys. Final publish will be regenerated after commit so `AssemblyInformationalVersion` carries the fix commit rather than the prior HEAD.
- Post-commit published-image smoke: freshly pulled `ghcr.io/mediavybz/cruncharr:testing` returned healthy at `1.0.50+8080ebf1ebfd469e1d1424d96723e345d4c6ec99`; served HTML contained both 1.0.50 cache keys.
- Registry manifest inspection: `linux/amd64` and `linux/arm64` both present under index digest `sha256:ace1aecf1c3abb5ff3bef44e722b90480eb018cffb569d7ebb8d5b942f35964c`.

### Infrastructure
| File | Purpose | Date |
|------|---------|------|
| Docker image | Multi-architecture testing image for version 1.0.50 and source commit 8080ebf; `linux/amd64` + `linux/arm64`; index digest `sha256:ace1aecf1c3abb5ff3bef44e722b90480eb018cffb569d7ebb8d5b942f35964c` | 2026-07-15 |

### Release Status (Round 33)
- Source commit `8080ebf` pushed to `origin/testing`.
- Multi-architecture image pushed to `ghcr.io/mediavybz/cruncharr:testing`, index digest `sha256:ace1aecf1c3abb5ff3bef44e722b90480eb018cffb569d7ebb8d5b942f35964c`.
- Stable `master`, `:latest`, and version tags were not changed.

## Audit Round 34 — Output filename length parity (2026-07-15, complete)

The live 1.0.50 retry for The Klutzy Class Monitor episode 5 reached muxing with a sanitized,
single-component basename but both mkvmerge and ffmpeg rejected it as `File name too long`. The
reported `.mkv` name is 261 ASCII bytes, exceeding Linux `NAME_MAX` (255); the desktop backend
already reserves headroom by limiting the template result to 220 characters.

### Backend (Mode A)
| File | Source File | Changes | Date |
|------|-------------|---------|------|
| src/Cruncharr.Core/Utils/Helpers.cs | CRD/Utils/Helpers.cs:827-837 | [PT] Ported `LimitFileNameLength` exactly: preserve the directory and extension while truncating only the final filename component to the requested desktop limit. | 2026-07-15 |
| src/Cruncharr.Core/Services/DownloadService.cs | CRD/Downloader/Crunchyroll/CrunchyrollManager.cs:1963-1990 | [PT] Ported the desktop 220-character output-name cap at both Docker filename-generation points, including its raw episode-title length check. Title-aware shortening recognizes the port's equivalent `${episodeTitle}` and legacy/web `{Episode Title}` aliases, preserves suffix fields such as `WEBDL-1080p`, and falls back to the exact desktop helper for non-title templates. | 2026-07-15 |
| src/Cruncharr.Core.Tests/PortedGapTests.cs | Regression guard for CRD/Downloader/Crunchyroll/CrunchyrollManager.cs:1963-1990 | [PT] Reproduces the exact episode-5 Sonarr title/template: the pre-fix `.mkv` component exceeds 255 characters; the ported result is exactly 220 before extension while retaining S01E05 and `WEBDL-1080p`. | 2026-07-15 |
| src/Cruncharr.API/Cruncharr.API.csproj | Release metadata | [PT] Advanced the testing release version 1.0.50 → 1.0.51. | 2026-07-15 |
| src/Cruncharr.API/Controllers/HealthController.cs | Existing REST health adapter | [PT] Updated the unreachable informational-version fallback 1.0.50 → 1.0.51; route, response shape, and status code are unchanged. | 2026-07-15 |

### Frontend (Mode B)
| File | Desktop Equivalent | API Endpoints Used | Changes | Date |
|------|-------------------|-------------------|---------|------|
| src/Cruncharr.API/wwwroot/index.html | Application shell release-asset loading | none | Advanced the existing CSS/JS cache keys 1.0.50 → 1.0.51; no component or behavior change. | 2026-07-15 |

### In Progress
| File | Mode | Blocker |
|------|------|---------|
| Targeted and full verification | A | [completed] Protected/full Windows and Linux tests, dual-architecture publish, registry inspection, and published-image smoke all passed. |

### API Contract
- No route, request, response-shape, or status-code changes.

### Verification (Round 34)
- Pre-change protected guards: 21/21 passed (`DownloadVersionResolutionTests`, `PortedGapTests`, `EncodingPresetAndTranscodeTests`).
- Post-change protected guards: 22/22 passed, including the exact long Sonarr-title regression.
- Debug build: `dotnet build cruncharr.sln --no-restore` — 0 warnings, 0 errors.
- Debug full suite: `dotnet test cruncharr.sln --no-build` — 156/156 passed.
- Release build: `dotnet build cruncharr.sln --configuration Release --no-restore` — 0 warnings, 0 errors.
- Release full suite: `dotnet test cruncharr.sln --configuration Release --no-build` — 156/156 passed.
- Linux-container full suite (`mcr.microsoft.com/dotnet/sdk:8.0`): 156/156 passed, including the Linux path-semantics regression.
- Frontend syntax: `node --check src/Cruncharr.API/wwwroot/js/app.js` — clean; `docker compose config -q` — clean.
- `git diff --check`: clean.
- Dual-architecture publish: the repository `publish-docker.sh` completed for linux-x64 and linux-arm64; each API apphost is approximately 48 MB and neither output contains a loose `Cruncharr.API.dll`.
- Pre-commit amd64 image smoke: local `cruncharr:1.0.51-local` became Docker-healthy; `/api/v1/health` returned healthy at 1.0.51 and the served HTML contained both 1.0.51 cache keys. The publish will be regenerated after commit so the informational version carries the fix commit.
- Final pre-ship rerun after aligning the raw-title condition exactly with desktop: Release 156/156 passed; Linux-container `PortedGapTests` 14/14 passed.
- Post-commit published-image smoke: a fresh pull of `ghcr.io/mediavybz/cruncharr:testing` became Docker-healthy; `/api/v1/health` returned `1.0.51+07bb8f34a6547722ad073850a549fe713df66a0d`; served HTML contained both 1.0.51 cache keys.
- Registry manifest inspection: linux/amd64 and linux/arm64 are present under index digest `sha256:885afd68b513d52fd0f12db4d51eae67899b52270447f9b62c899c36f87fa169`.

### Infrastructure
| File | Purpose | Date |
|------|---------|------|
| Docker image | Multi-architecture testing image for version 1.0.51 and source commit 07bb8f3; `linux/amd64` + `linux/arm64`; index digest `sha256:885afd68b513d52fd0f12db4d51eae67899b52270447f9b62c899c36f87fa169` | 2026-07-15 |

### Release Status (Round 34)
- Source commit `07bb8f3` pushed to `origin/testing`.
- Multi-architecture image pushed to `ghcr.io/mediavybz/cruncharr:testing`, index digest `sha256:885afd68b513d52fd0f12db4d51eae67899b52270447f9b62c899c36f87fa169`.
- Live LAN check still returned 1.0.50 with one active download, so no forced restart/update was attempted; it must re-pull `:testing` before retrying the episode.
- Stable `master`, `:latest`, and version tags were not changed.
