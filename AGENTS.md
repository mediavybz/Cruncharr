# Cruncharr Development Guide

## Build Commands
```bash
# Build solution
dotnet build

# Run tests
dotnet test

# Run CLI
dotnet run --project src\Cruncharr.CLI -- [command]

# Publish for Linux
dotnet publish src\Cruncharr.CLI -c Release -r linux-x64 --self-contained false
```

## Project Structure
- `src/Cruncharr.Core/` - Core business logic (models, services, config)
- `src/Cruncharr.CLI/` - Command-line interface
- `tests/Cruncharr.Core.Tests/` - Unit tests
- `docs/` - Documentation
- `crunchy-downloader/` - Original codebase (reference only)

## Key Files
- `src/Cruncharr.CLI/Program.cs` - CLI entry point
- `src/Cruncharr.Core/Configuration/CruncharrConfig.cs` - Configuration management
- `src/Cruncharr.Core/Services/DownloadService.cs` - Download logic (stub)
- `Dockerfile` - Docker image definition

## Development Notes
- Uses .NET 8.0
- Target runtime: linux-x64 for Docker
- No GUI dependencies
- Security: All packages checked for CVEs before use

## Next Steps
1. Port core download logic from `crunchy-downloader/CRD/Downloader/Crunchyroll/`
2. Remove Avalonia dependencies from ported code
3. Test Docker build after WSL2 installation
4. Add more comprehensive tests
