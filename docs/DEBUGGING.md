# Debugging Guide for Cruncharr

## Quick Verification Commands

### 1. Check Environment
```powershell
# Check .NET version
dotnet --version

# Check Git version
git --version

# Check Docker (requires WSL2)
docker --version
```

### 2. Build Verification
```powershell
cd C:\Users\Aorus\Desktop\Cruncharr

# Clean build
dotnet clean
dotnet build

# Should show: 0 Warning(s), 0 Error(s)
```

### 3. CLI Command Tests
```powershell
cd C:\Users\Aorus\Desktop\Cruncharr

# Test help
dotnet run --project src\Cruncharr.CLI -- --help

# Test config
dotnet run --project src\Cruncharr.CLI -- config get OutputDirectory

# Test login (dry run)
dotnet run --project src\Cruncharr.CLI -- login --email test@test.com --password test123

# Test search (stub)
dotnet run --project src\Cruncharr.CLI -- search "Attack on Titan" --format json

# Test series (stub)
dotnet run --project src\Cruncharr.CLI -- series "GR751KNZY" --format json

# Test download (will show "not yet ported" message)
dotnet run --project src\Cruncharr.CLI -- download "https://www.crunchyroll.com/watch/GY8VM8GJY"
```

### 4. Configuration File Test
```powershell
# Create test config
$env:CRUNCHYROLL_CONFIG_DIR = "C:\Users\Aorus\Desktop\Cruncharr\test-config"
New-Item -ItemType Directory -Path $env:CRUNCHYROLL_CONFIG_DIR -Force

# Test with environment variables
$env:CRUNCHYROLL_EMAIL = "test@example.com"
$env:CRUNCHYROLL_PASSWORD = "secret"
$env:CRUNCHYROLL_OUTPUT_DIR = "C:\Users\Aorus\Desktop\Cruncharr\test-downloads"

dotnet run --project src\Cruncharr.CLI -- config get OutputDirectory

# Should show: C:\Users\Aorus\Desktop\Cruncharr\test-downloads
```

### 5. YAML Config Test
```powershell
# Create YAML config
$yaml = @"
cruncharr:
  crunchyroll:
    email: "yaml-test@example.com"
  download:
    output_dir: "C:\\Users\\Aorus\\Desktop\\Cruncharr\\yaml-output"
    quality: "1080p"
    dub_languages:
      - "ja-JP"
      - "en-US"
"@
$yaml | Out-File -FilePath "$env:CRUNCHYROLL_CONFIG_DIR\cruncharr.yml" -Encoding utf8

dotnet run --project src\Cruncharr.CLI -- config get OutputDirectory
```

### 6. JSON Config Test
```powershell
# Create JSON config
$json = @"
{
  "crunchyroll": {
    "email": "json-test@example.com"
  },
  "download": {
    "outputDir": "C:\\Users\\Aorus\\Desktop\\Cruncharr\\json-output",
    "quality": "best"
  }
}
"@
$json | Out-File -FilePath "$env:CRUNCHYROLL_CONFIG_DIR\cruncharr.json" -Encoding utf8

dotnet run --project src\Cruncharr.CLI -- config get OutputDirectory
```

## Testing Ported Logic

### When Core Logic is Ported:
```powershell
# Test actual download (requires valid credentials)
dotnet run --project src\Cruncharr.CLI -- login --email "your@email.com" --password "yourpassword"
dotnet run --project src\Cruncharr.CLI -- download "https://www.crunchyroll.com/watch/episode-id" --format json

# Test with quiet mode
dotnet run --project src\Cruncharr.CLI -- download "https://..." --quiet
$LASTEXITCODE  # Should be 0 for success, 1 for failure

# Test series download
dotnet run --project src\Cruncharr.CLI -- series "series-id" --download --format json
```

## Docker Testing (After WSL2 Installation)

### Install WSL2:
```powershell
# Enable required Windows features
dism.exe /online /enable-feature /featurename:Microsoft-Windows-Subsystem-Linux /all /norestart
dism.exe /online /enable-feature /featurename:VirtualMachinePlatform /all /norestart

# Restart computer
Restart-Computer

# After restart, install WSL2
wsl --install

# Set WSL2 as default
wsl --set-default-version 2

# Install Ubuntu
wsl --install -d Ubuntu
```

### Test Docker Build:
```powershell
# After Docker Desktop is running
cd C:\Users\Aorus\Desktop\Cruncharr
docker build -t cruncharr:test .

# Test the image
docker run --rm cruncharr:test --help

# Test with config
docker run --rm -e CRUNCHYROLL_EMAIL=test@test.com cruncharr:test login --email test@test.com --password test

# Test download (stub)
docker run --rm cruncharr:test download "https://..." --format json
```

### Docker Compose Test:
```powershell
docker-compose up --build
```

## Debugging Checklist

- [ ] `dotnet build` succeeds with 0 errors
- [ ] `dotnet run -- --help` shows all commands
- [ ] Config commands work (get/set)
- [ ] Environment variables are read correctly
- [ ] YAML config parsing works
- [ ] JSON config parsing works
- [ ] Login command stores credentials
- [ ] Logout command clears credentials
- [ ] Search command returns JSON
- [ ] Series command returns JSON
- [ ] Download command shows proper error (not yet ported)
- [ ] Docker image builds (requires WSL2)
- [ ] Docker container runs --help
- [ ] Image size < 300MB

## Performance Testing

### Binary Size:
```powershell
# Check published binary size
dotnet publish src\Cruncharr.CLI -c Release -r linux-x64 --self-contained true
Get-ChildItem src\Cruncharr.CLI\bin\Release\net8.0\linux-x64\publish | Measure-Object -Property Length -Sum
```

### Memory Usage:
```powershell
# Monitor memory during download
# (When core logic is ported)
```

## Troubleshooting

### Issue: Docker build fails with "500 Internal Server Error"
**Cause**: Docker Desktop requires WSL2 which is not installed
**Fix**: 
```powershell
wsl --install
# Restart computer
# Start Docker Desktop
```

### Issue: `dotnet build` fails with missing packages
**Cause**: NuGet packages not restored
**Fix**:
```powershell
dotnet restore
dotnet build
```

### Issue: CLI says "Download logic not yet ported"
**Cause**: Core download logic needs to be extracted from original codebase
**Fix**: Follow docs/PORTING.md instructions

### Issue: Config file not found
**Cause**: Config directory doesn't exist
**Fix**:
```powershell
$env:CRUNCHYROLL_CONFIG_DIR = "C:\Users\Aorus\Desktop\Cruncharr\config"
New-Item -ItemType Directory -Path $env:CRUNCHYROLL_CONFIG_DIR -Force
```
