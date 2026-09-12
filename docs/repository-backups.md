# Repository backups

Forgejo is the primary Git repository; GitHub is its backup. Compare matching
branches when checking synchronization: `master` is stable and `testing` is beta.
Their tips intentionally differ. A repository's default page shows `master`.

The `mirror-github.yml` Forgejo workflow copies all branches, tags, historical Git
LFS objects and published release notes to GitHub. It preserves destination-only
release records and assets.
Release attachments, container images, issues and runtime configuration are not
copied by this workflow; they need their own backups. No release attachments
were present when release-note synchronization was added.

Unraid's `cruncharr-github-mirror` User Script dispatches the workflow from
`testing` every 15 minutes because native Forgejo triggers have been unreliable.
When changing its schedule, update both User Scripts' `schedule.json` and generated
`customSchedule.cron`, then run Unraid's `update_cron`. Verify the entry in the
installed cron table and a successful workflow run. A schedule saved in JSON alone
does not survive cron regeneration as an active job.

After repository work, push to Forgejo, run the mirror and compare all remote
branch/tag hashes with `git ls-remote --heads --tags`. Fetch both remotes locally
with tags so the local repository contains their history. Check release records
separately: Git does not store release notes. Keep credentials out of these files.

From the local checkout, run `pwsh -File scripts/verify-repository-sync.ps1 -Fetch`.
It compares all local and remote branch/tag hashes and reports uncommitted files.
It exits with an error when any copy differs. Use `-OutputPath report.json` to save
the dated result. Fetching updates remote-tracking refs; it does not merge branches.
