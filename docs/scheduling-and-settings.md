# Scheduled downloads and settings

Open a show from Browse, Search, or History and select **Schedule new episodes**.
The first save records the currently released episodes as a baseline; it does not
queue the back catalog. The subscription covers the entire series, including later
seasons. Pause/resume preserves that baseline. Remove and subscribe again starts
from a new baseline.

Manage subscriptions under **Settings → Scheduler**. Checks run every 15 minutes by
default (configurable from 1 to 1440 minutes). New episodes enter the queue on the
next check after release. Premium accounts use the Cruncharr queue; enable
**Settings → Queue → Auto Download** to start downloads automatically. Global queue
pause still applies. With **Use Sonarr without Premium** enabled, guest and free
accounts send new episodes to Sonarr to monitor and search instead. Guest browsing
and saving a schedule remain available.

History must be enabled. Season language overrides take priority over series
overrides. Without an override, single-dub mode uses Default Audio; multi-dub mode
uses the configured dub list. The scheduler waits for the requested audio languages
rather than completing the subscription with the wrong dub. The existing “Wait for
Selected Languages” setting additionally waits for explicitly requested subtitles.
Local files and freshly checked Sonarr files are skipped. A failed provider refresh
or Sonarr check leaves the episode pending for retry. Settings and episode baselines
survive restarts in `subscriptions.json` beside the main configuration file.

**Automatically add new shows** (Settings → Scheduler) subscribes a series when its
first episode (season 1, episode 1) is released with one of the chosen **audio (dub)**
languages. The series is scheduled with that audio and the chosen **subtitles** (none
chosen means the Softsubs Languages setting), its released episodes are queued at the
same check, and the download registers it in Sonarr. For example, choose English audio
for English dubs, or Japanese audio with English subtitles. Only shows that premiere after the
option is turned on are added, each show at most once; removing its schedule is
respected. New seasons of shows you do not follow are not added.

The older **History → Auto Refresh Interval** is a separate bulk operation on all
History entries. Its default is 0 (disabled). Its auto-add option can queue missing
back-catalog episodes; use Scheduler subscriptions for new releases only. Adding a
subscription does not enable the bulk operation.

## Settings fixes in 1.0.81

- Settings autosave while typing and before navigation. Invalid or failed saves do
  not partially update runtime configuration. Optional text fields can be cleared.
- Series/season quality and output-directory overrides now reach the download pipeline;
  per-download overrides never alter global settings. Saving unrelated settings preserves
  language lists without adding defaults.
- Both processing-limit controls update the same queue limit. Auto Download wakes
  the queue immediately; queue-path changes persist subsequent snapshots there.
- Proxy, FlareSolverr, stream endpoint, token location, and log-mode changes apply
  without restarting. Webhook tests use the configured method, headers and body.
- History enable/disable also controls completed-download recording. Background
  images, opacity and blur now render. Image paths refer to files in the container.
- Search selections can be added to History, and pasted Crunchyroll episode URLs
  honor Single Episode Instant Add (Premium accounts only). URL and title search
  share one input. Obsolete API-selection, default-search-mode and unimplemented
  MITM controls are no longer offered as working settings.

## Transcoding

Use **Settings → Muxing → Manage Presets** to inspect built-ins or create a custom
copy. Blank resolution and frame rate preserve the source. The preview comes from
the same command builder used for encoding, including codec-specific quality
arguments. Quality -1 leaves the encoder default; AV1 supports values through 63.
Every preset preserves audio, subtitles and attachments unless its additional
parameters explicitly override them. A selected custom preset cannot be deleted
until another is selected. Renamed built-in aliases continue to work.

### Temp folder in RAM

**Settings → Download → Temp Directory** can point at a RAM disk (tmpfs) to keep download,
mux and encode I/O off an SSD. Everything written there is memory, so **RAM Limit (GB)** caps
Cruncharr's memory, including the encoder and its temp files: a new download waits, with the
reason shown in the queue, until it fits. 0 means automatic, a quarter of the memory available
to Cruncharr. One job always runs, so a limit set too low slows the queue but never stops it. A
temp folder on disk is not limited.

Encoding errors fail the queue item instead of reporting an unencoded download as
success. The muxed source is preserved, including in the temporary working directory.
All 19 built-in software profiles were smoke-tested with the container FFmpeg using
a short synthetic clip containing video, two audio tracks, subtitles and an attachment.
Hardware profiles still require the corresponding GPU and driver in the container.
