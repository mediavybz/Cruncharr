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
`testing` every 15 minutes as a backup to the push trigger.
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

Git refs can match while Forgejo's database is stale. Its repository warning,
branch counts and tag counts depend on database records, which receive hooks
update after a push. In September 2026, the hooks had lost their executable bits;
pushes stored commits without updating those records or starting workflows.

Check this separately inside the Forgejo container with
`sh verify-forgejo-metadata.sh DATABASE BARE_REPOSITORY REPOSITORY_ID`, using
`scripts/verify-forgejo-metadata.sh`. It reads SQLite and Git, checks every branch
commit and tag target, and fails if a managed hook is missing or not executable.
Run it as Forgejo's `git` user so the permission check uses the service account.
For other database engines, compare the same records with that engine's client.

If the checks fail, back up the database and hooks, run
`forgejo doctor check --run hooks`, and use `forgejo admin regenerate hooks` to
restore the generated hooks. In the admin dashboard, run **Sync missed branches
from Git data to database** and **Sync tags from Git data to database**. These
operations rebuild metadata from existing Git data. Verify the warning has gone
on the signed-in repository page, then verify a new push updates the database
and starts the mirror. An up-to-date push does not exercise receive hooks.

Preserve executable bits when copying repository storage or changing share
permissions. Exclude it from bulk file-permission resets such as Unraid's New
Permissions tool; ordinary data-file permissions prevent Git hooks from running.
