# Cruncharr operations handoff

- Work on the `testing` branch unless the user explicitly requests another branch.
- Forgejo remote: `https://forgejo.foss.homes/shoy/Cruncharr.git`.
- GitHub mirror: `https://github.com/mediavybz/Cruncharr.git`.
- Test image: `ghcr.io/mediavybz/cruncharr:testing`.
- Live UI/API: `http://192.168.10.10:8585/` and `/api/v1`.
- Sonarr is configured by the live app at `192.168.10.10:8991`; never copy its API key into this repository.
- This checkout uses its configured Git credential helper for repository access. Check `git remote -v`
  when resuming; the older `forgejo_cruncharr` SSH key is not present on this workstation.
- Unraid container access uses `root@192.168.10.10` with `~/.ssh/unraid-easymedia`.
- The live container is `CrunchArr` and is managed by Unraid template
  `/boot/config/plugins/dockerMan/templates-user/my-Cruncharr.xml`.
- Update only through Unraid's supported `update_container CrunchArr` script after confirming the
  live queue has no active downloads and recording mounts/image state.
- Build and publish Docker images with `.forgejo/workflows/docker-testing.yml` on the self-hosted
  Unraid `docker-build` runner; do not use GitHub-hosted runners or the agent workstation. There is
  no local fallback. `scripts/publish-docker.sh` is the runner workflow's build implementation.
- Pushes to `testing` validate AMD64 and ARM64 and smoke-test an AMD64 container without publishing.
  Manually dispatch the same workflow with `publish=true` to update the testing image.
- After publishing, verify both AMD64 and ARM64 manifests and smoke-test the exact version/commit.
  The runner removes disposable containers and images and caps its persistent BuildKit cache at
  20 GB.
- Never call episode naming fixed from unit tests alone. Deploy the test image, refresh/rematch live
  series, and confirm both Crunchyroll and Sonarr identities. Regression probes include Wistoria's
  collapsed `SP -> CR 1 -> Sonarr S00E02` and Slime's `24.5`, `24.9`, `48.5`, and `65.5` entries.
- A 64-character value presented beside `ssh-keygen -Y sign` is an SSH-key verification challenge,
  not a Forgejo API token. Forgejo issue mutation requires a separate scoped API access token.
- Do not store tokens, passwords, API keys, signatures, or live configuration in tracked files.
- After pushing and mirroring, run `scripts/verify-repository-sync.ps1 -Fetch` to compare every
  local, Forgejo and GitHub branch/tag hash. Compare the same branch; do not promote beta to
  `master` merely to make their version numbers equal.
- Matching Git refs do not prove Forgejo's database is synchronized. Also run
  `scripts/verify-forgejo-metadata.sh` inside Forgejo as `git` (database
  `/data/forgejo.db`, bare repository `/data/git/repositories/shoy/cruncharr.git`,
  repository ID `4`). See `docs/repository-backups.md` for hook and metadata repair.

- On Windows, always specify `encoding="utf-8"` and `newline="\n"` for Python source-file writes (UTF-8 for reads too). The system
  code page cannot encode all UI text, and a failed write can truncate the source file.
