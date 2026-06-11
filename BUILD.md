# RED - Build & Deploy Instructions

## Prerequisites
- .NET 8.0 SDK installed
- Windows 10/11 (for building WPF apps)
- Visual Studio 2022 or `dotnet` CLI

## Project Structure
```
PL/
├── data/                    ← Runtime data (auto-copied to build)
│   ├── quick_comments.json
│   └── inspector_stats.json
├── docs/                    ← Documentation
├── licenses/                ← License files for team
├── releases/                ← Build archives
├── samples/                 ← Test data
├── scripts/                 ← Python utilities
├── Models/                  ← C# data models
├── Services/                ← C# services
├── *.xaml / *.xaml.cs      ← UI and code-behind
├── InspectionEditor.csproj  ← Project file
├── RedHelp.pdf             ← Help documentation
├── settings.txt            ← API key config
└── red_dot.ico             ← App icon
```

## Build Commands

### 1. Clean Previous Builds
```bash
cd /Users/trentfuller/Library/CloudStorage/Dropbox/P/PL
dotnet clean
```

### 2. Restore Dependencies
```bash
dotnet restore
```

### 3. Build Release (Framework-Dependent)
Requires .NET 8.0 runtime on target machine:
```bash
dotnet build -c Release
```
Output: `bin/Release/net8.0-windows10.0.19041.0/`

### 4. Build Standalone (Self-Contained) ⭐ RECOMMENDED
No .NET runtime required on target machine:
```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
```
Output: `bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/`

### 5. Verify Build Contents
Ensure these files are in the publish folder:
- [ ] `InspectionEditor.exe` (main app)
- [ ] `quick_comments.json` (AI suggestions data)
- [ ] `inspector_stats.json` (inspector deviation data)
- [ ] `RedHelp.pdf` (help documentation)
- [ ] `settings.txt` (API key - copy manually if missing)

```bash
ls bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/ | grep -E "\.exe$|\.json$|\.pdf$|\.txt$"
```

## Deploy (Create ZIP)

### 1. Navigate to Publish Folder
```bash
cd bin/Release/net8.0-windows10.0.19041.0/win-x64/publish
```

### 2. Create Dated ZIP Archive
```bash
# Get today's date
DATE=$(date +%Y-%m-%d)

# Create ZIP (excluding unnecessary files)
zip -r "../../../../../../../releases/RED-${DATE}-standalone.zip" . \
    -x "*.pdb" \
    -x "*.xml" \
    -x "*.deps.json"

echo "Created: releases/RED-${DATE}-standalone.zip"
```

### 3. Manual ZIP (Alternative)
1. Open `bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/` in Explorer
2. Select all files
3. Right-click → Send to → Compressed (zipped) folder
4. Rename to `RED-YYYY-MM-DD-standalone.zip`
5. Move to `releases/` folder

## Distribution

### Copy to Shared Location
```bash
# Copy to Google Drive team folder
cp releases/RED-${DATE}-standalone.zip "/path/to/Google Drive/RED/"
```

### Or Upload via gog CLI
```bash
gog drive upload releases/RED-${DATE}-standalone.zip \
    --account fullinspect@gmail.com \
    --parent "RED" \
    --overwrite
```

## Update Data Files (Before Build)

### Regenerate Quick Comments
```bash
cd scripts
python3 generate_quick_comments.py
```
Outputs to: `data/quick_comments.json`

### Regenerate Inspector Stats
```bash
cd scripts
python3 calculate_inspector_stats.py
```
Outputs to: `data/inspector_stats.json`

## One-Liner: Full Rebuild & Deploy
```bash
cd /Users/trentfuller/Library/CloudStorage/Dropbox/P/PL && \
dotnet clean && \
dotnet publish -c Release -r win-x64 --self-contained true && \
DATE=$(date +%Y-%m-%d) && \
cd bin/Release/net8.0-windows10.0.19041.0/win-x64/publish && \
zip -r "../../../../../../../releases/RED-${DATE}-standalone.zip" . -x "*.pdb" -x "*.xml" && \
echo "✓ Built and zipped: releases/RED-${DATE}-standalone.zip"
```

## Troubleshooting

### Missing data files in build?
Check `.csproj` has these entries:
```xml
<None Include="data\quick_comments.json">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
<None Include="data\inspector_stats.json">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
<None Update="RedHelp.pdf">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

### Build fails on macOS?
WPF requires Windows. Options:
1. Build on Windows machine
2. Use Windows VM
3. Use GitHub Actions with `windows-latest` runner

### App won't start on target machine?
1. Check Windows version (requires Win10 1903+)
2. For framework-dependent builds: install .NET 8.0 runtime
3. For standalone builds: ensure x64 architecture matches

## Version History
- 2026-02-08: Project reorganization, local data paths, help system added
- 2026-02-05: Latest stable release
