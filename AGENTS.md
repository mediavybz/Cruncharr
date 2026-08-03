# Cruncharr operations handoff

- Work on the `testing` branch unless the user explicitly requests another branch.
- Forgejo remote: `ssh://git@192.168.10.10:2222/shoy/Cruncharr.git`.
- GitHub mirror: `https://github.com/mediavybz/Cruncharr.git`.
- Test image: `ghcr.io/mediavybz/cruncharr:testing`.
- Live UI/API: `http://192.168.10.10:8585/` and `/api/v1`.
- Sonarr is configured by the live app at `192.168.10.10:8991`; never copy its API key into this repository.
- Repository SSH uses `~/.ssh/forgejo_cruncharr`; this checkout pins it with `core.sshCommand`.
- Unraid container access uses `root@192.168.10.10` with `~/.ssh/unraid-easymedia`.
- The live container is `CrunchArr` and is managed by Unraid template
  `/boot/config/plugins/dockerMan/templates-user/my-Cruncharr.xml`.
- Update only through Unraid's supported `update_container CrunchArr` script after confirming the
  live queue has no active downloads and recording mounts/image state.
- Build and push Docker images locally with `scripts/publish-docker.sh`; do not use GitHub Actions.
- After publishing, verify both AMD64 and ARM64 manifests, smoke-test the exact version/commit, and
  reclaim local containers, images, volumes, build cache, and generated `bin/obj` files.
- Never call episode naming fixed from unit tests alone. Deploy the test image, refresh/rematch live
  series, and confirm both Crunchyroll and Sonarr identities. Regression probes include Wistoria's
  collapsed `SP -> CR 1 -> Sonarr S00E02` and Slime's `24.5`, `24.9`, `48.5`, and `65.5` entries.
- A 64-character value presented beside `ssh-keygen -Y sign` is an SSH-key verification challenge,
  not a Forgejo API token. Forgejo issue mutation requires a separate scoped API access token.
- Do not store tokens, passwords, API keys, signatures, or live configuration in tracked files.
