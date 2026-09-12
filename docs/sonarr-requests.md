# Sonarr requests

With Sonarr enabled, requesting an episode chooses a source using your Crunchyroll account:

- **Premium:** Cruncharr registers the series in Sonarr, then uses its own download queue. New series start unmonitored and no Sonarr search is sent. Episodes already present in Sonarr are skipped unless Replace Existing Files is enabled. Completed, muxed files are submitted to Sonarr for import.
- **Signed out or without Premium:** Cruncharr registers the series and asks Sonarr to monitor and search the selected missing episodes. Sonarr uses its own indexers, download clients and quality profile. Nothing enters Cruncharr's download queue.

Viewing a show does not add it. The download/request button submits the selected episodes; requesting a season submits that season. Cruncharr checks current Sonarr files before searching, and suppresses repeated requests for the same episodes for 30 minutes. If multiple editions match, select the intended TVDB series in the dialog. The selection is saved with the series link.

## Settings

Open **Settings → Sonarr**. Enable the connection and use **Test Connection** first.

**Register Cruncharr downloads in Sonarr** enables registration and completed-file imports for Premium requests. **Use Sonarr without Premium** enables guest/free-account requests. Both default to on when Sonarr is enabled.

**Turn off Sonarr monitoring for Premium requests** also unmonitors an existing show so feeds cannot grab competing releases while Cruncharr supplies it. Turn this off to preserve existing monitoring. This does not cancel downloads or searches Sonarr has already started. Newly added Premium series always start unmonitored.

Choose a **quality profile** and **root folder** for new series. Automatic selects the most common profile and most used root folder in the existing Sonarr library. An empty library with multiple profiles needs an explicit profile selection. Existing series keep their profiles, paths and tags.

**Cruncharr download folder as seen by Sonarr** maps Cruncharr's output directory to the path Sonarr can access. For example, if the same host folder is mounted at `/downloads` in Cruncharr and `/incoming/cruncharr` in Sonarr, enter `/incoming/cruncharr`. Leave it blank when the full paths are identical. Sonarr needs access to the completed files; an API connection alone cannot transfer them. A per-series output override must remain under the mapped output directory, or use identical paths with the mapping blank.

Imports use the confirmed Sonarr series and episode IDs, even when the filename alone is unrecognized. Sonarr inspects the file before import; rejections stay pending and appear in request status. Imports copy into the Sonarr library. Files already inside the series folder use a rescan and need recognizable filenames; enable **Sonarr Naming / TVDB Numbering** when writing directly into the Sonarr series folder. Raw tracks produced with Skip Muxing are not submitted as completed episodes.

## Background status

Pending searches and imports survive restarts in `/config/sonarr-requests.json`. A worker checks them every ten seconds and retries failed requests after a minute. It waits for Sonarr to finish populating a newly added series before matching episodes. Only confirmed episode identities are searched; unmatched selections stay pending.

**Settings → Sonarr → Refresh status** shows each request, missing metadata or import errors, pending work, and Sonarr's episode/file counts. Browse badges and History file status refresh in the background, roughly once per minute. An import scan completing does not itself prove that Sonarr accepted a file: the displayed file counts come from Sonarr. Inspect Sonarr's import logs if a scan completes without increasing the count.

**Schedule new episodes** uses the same account-based routing for future releases. Explicit Cruncharr subscriptions continue to run when Premium registration unmonitors a Sonarr series. Guest searches use Sonarr's language and quality rules; Cruncharr's dub/subtitle selections apply to Cruncharr downloads.
