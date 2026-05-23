# Cruncharr - Porting Guide

## Overview
This document describes how to port the core download logic from the original Crunchy-Downloader into Cruncharr.

## Original Codebase Structure
The original code is in `crunchy-downloader/CRD/` with these key areas:

### Core Logic (TO PORT)
1. **Authentication** (`CRD/Downloader/Crunchyroll/CRAuth.cs`)
   - Handles Crunchyroll login/session management
   - Multiple auth endpoints (TV, Mobile, Guest)
   - Token refresh logic
   - **Action**: Extract into `AuthenticationService`

2. **Download Manager** (`CRD/Downloader/Crunchyroll/CrunchyrollManager.cs`)
   - Main download orchestration (161KB file)
   - Episode download logic
   - Series download logic
   - Muxing/encoding coordination
   - **Action**: Extract into `DownloadService`
   - **WARNING**: Heavy Avalonia dependencies (MainWindow.Instance.ShowError, Dispatcher.UIThread)

3. **Episode Fetching** (`CRD/Downloader/Crunchyroll/CrEpisode.cs`)
   - Episode metadata retrieval
   - Stream URL extraction
   - **Action**: Port to `SearchService`

4. **Series Fetching** (`CRD/Downloader/Crunchyroll/CrSeries.cs`)
   - Series metadata
   - Season/episode listing
   - **Action**: Port to `SearchService`

5. **Queue Management** (`CRD/Downloader/QueueManager.cs`)
   - Download queue
   - Concurrent download control
   - **Action**: Extract into `DownloadService` or separate `QueueService`
   - **WARNING**: Uses Avalonia Dispatcher and ObservableObject

6. **Muxing** (`CRD/Utils/Muxing/`)
   - MKV/MP4 muxing with ffmpeg/mkvmerge
   - Subtitle processing
   - Font embedding
   - **Action**: Port to `DownloadService` or `MuxingService`

7. **DRM** (`CRD/Utils/DRM/`)
   - Widevine CDM integration
   - Decryption handling
   - **Action**: Port as-is (no UI deps)

8. **Configuration** (`CRD/Utils/CfgManager.cs`)
   - Settings persistence
   - Path management
   - **Action**: Already implemented in `CruncharrConfig`

### UI Code (DO NOT PORT)
- `CRD/Views/` - Avalonia XAML views
- `CRD/ViewModels/` - ReactiveUI view models
- `CRD/App.axaml` - Application definition
- `CRD/Program.cs` - GUI entry point

## Porting Strategy

### Phase 1: Extract Pure Logic
1. Copy file from original codebase
2. Remove all Avalonia/ReactiveUI using statements
3. Remove ObservableObject inheritance
4. Replace Avalonia Dispatcher with plain async/await
5. Replace ObservableCollection with List or custom events
6. Remove MainWindow.Instance references (replace with logging or exceptions)

### Phase 2: Adapt to New Architecture
1. Convert singleton patterns to DI services
2. Replace file paths with config-driven paths
3. Add progress callbacks instead of UI updates
4. Add proper cancellation token support

### Phase 3: Testing
1. Unit test each service
2. Integration test download flow
3. Test error handling
4. Test Docker container

## Key Challenges

### 1. Avalonia Coupling
The original code uses:
- `Avalonia.Threading.Dispatcher.UIThread.Post()` - Replace with async/await
- `CommunityToolkit.Mvvm.ComponentModel.ObservableObject` - Remove, use plain properties
- `Avalonia.Collections.RefreshableObservableCollection` - Replace with `List<T>` + events
- `MainWindow.Instance.ShowError()` - Replace with exceptions or logging

### 2. Threading Model
Original uses UI thread for progress updates. New version should:
- Use `IProgress<T>` for progress reporting
- Use `CancellationToken` for cancellation
- Use async/await throughout

### 3. File Paths
Original uses hardcoded relative paths. New version uses:
- Config-driven paths
- Docker volume mounts
- Environment variables

## Implementation Order

1. **Authentication** (Easiest, mostly HTTP calls)
2. **Search** (Next easiest, mostly HTTP calls)
3. **Download Core** (Complex, involves HLS, DRM, file I/O)
4. **Muxing** (Complex, involves external processes)
5. **Queue Management** (Complex, involves state management)

## Testing Commands

```bash
# After porting auth
cruncharr login --email user@example.com --password secret

# After porting search
cruncharr search "Attack on Titan" --format json

# After porting download
cruncharr download "https://www.crunchyroll.com/watch/episode-id" --format json

# Docker test
docker run --rm -e CRUNCHYROLL_EMAIL=user@example.com -e CRUNCHYROLL_PASSWORD=secret cruncharr download "https://..."
```

## Notes
- The original `Program.cs` has a `--headless` flag but still initializes Avalonia
- We need pure headless mode with zero GUI initialization
- All progress should be reported via `IProgress<DownloadProgress>`
- All output should support `--format json` and `--quiet` modes
