# Buildarr-Inspired Improvements for Cruncharr

## Configuration Format (YAML Support)
Buildarr uses YAML for human-readable configuration. Cruncharr should support both YAML and JSON:

```yaml
# /config/cruncharr.yml
cruncharr:
  crunchyroll:
    email: "${CRUNCHYROLL_EMAIL}"
    password: "${CRUNCHYROLL_PASSWORD}"
  
  download:
    output_dir: "/downloads"
    temp_dir: "/tmp/cruncharr"
    filename_template: "{SeriesTitle} - S{season:00}E{episode:00} - {EpisodeTitle}"
    quality: "best"
    dub_languages:
      - "ja-JP"
    subtitle_languages:
      - "en-US"
    default_audio: "ja-JP"
    default_subtitle: "en-US"
    simultaneous_downloads: 2
    retry_attempts: 5
    retry_delay_seconds: 5
    skip_muxing: false
    mux_fonts: true
    include_chapters: true
  
  history:
    enabled: true
    remove_missing: true
  
  notifications:
    webhook_url: ""
    webhook_method: "POST"
    on_complete: true
    on_error: true
```

## CLI Patterns from Buildarr
- `cruncharr run` - Single execution mode (like `buildarr run`)
- `cruncharr daemon` - Continuous monitoring mode (like `buildarr daemon`)
- `cruncharr dump-config` - Export current config
- `cruncharr validate-config` - Validate configuration without running

## Docker Patterns
Buildarr's Docker usage:
```bash
docker run -it --rm callum027/buildarr:latest sonarr dump-config http://sonarr.example.com:8989
```

Cruncharr equivalent:
```bash
docker run --rm -e CRUNCHYROLL_EMAIL=... cruncharr download "https://..."
docker run --rm -v ./config:/config cruncharr dump-config
```

## Logging
Buildarr uses structured logging with timestamps and log levels. Cruncharr should:
- Use ISO 8601 timestamps
- Include log level (DEBUG, INFO, WARN, ERROR)
- Include component name (e.g., `[download]`, `[auth]`)
- Support `--log-level` flag
- Support `--quiet` for no output

## Plugin Architecture (Future)
Consider supporting multiple download sources via plugins:
```csharp
public interface IDownloadPlugin{
    string Name { get; }
    bool CanHandle(string url);
    Task<DownloadResult> DownloadAsync(string url, DownloadOptions options);
}
```

## *Arr Integration Points
1. **Sonarr**: Custom script on grab/import that calls `cruncharr download`
2. **Radarr**: Same pattern for movies
3. **Lidarr**: For music content
4. **Prowlarr**: As a download client type
