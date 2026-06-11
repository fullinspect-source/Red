# RED

RED is the Windows WPF inspection editor used by Strand/Full Inspect inspectors.

## Current release line

- Version: 2.0.0
- Project: `InspectionEditor.csproj`
- Runtime: .NET 8 WPF, `net8.0-windows10.0.19041.0`
- Production executable: `Red.exe`
- Production install folder: `C:\Red`
- Production user data folder: `%LOCALAPPDATA%\RED`

## Build

Standalone/self-contained Windows x64 build:

```bash
dotnet restore InspectionEditor.csproj -r win-x64
dotnet publish InspectionEditor.csproj -c Release -r win-x64 --self-contained true -o bin/Publish
echo 2.0.0 > bin/Publish/version.txt
```

See `docs/RED-2.0-DEPLOYMENT.md` for deployment notes, migration behavior, and manual Windows checks.

## Release assets

The field updater uses GitHub latest release assets:

- `Red-vX.Y.Z.zip`
- `update_red.bat`

Stable updater URL:

`https://github.com/fullinspect-source/Red/releases/latest/download/update_red.bat`
