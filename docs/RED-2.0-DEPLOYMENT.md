# RED 2.0 Deployment Readme

## Source / project location

Active RED 2.0 source is in Dropbox:

- Windows path: `C:\Users\grace\Dropbox\P\PL`
- Mac synced path: `/Users/trentfuller/Library/CloudStorage/Dropbox/P/PL`

This folder is not itself a git checkout. The GitHub repo `fullinspect-source/Red` is currently used mainly for releases and updater assets.

## Important project files

- `InspectionEditor.csproj` — WPF .NET 8 project file.
- `AppIdentity.cs` — app display/version/data-folder identity.
- `App.xaml`, `App.xaml.cs` — startup flow.
- `InspectionPickerWindow.*` — RED 2.0 My List / home window.
- `MainWindow.*` — inspection editor, updater, tools, comments, AI, save workflow.
- `Services/LicenseService.cs` — machine-locked RED license validation.
- `Services/DataUpdateService.cs` — quick comments / inspector stats update plumbing.
- `Services/GrokApiClient.cs` — Gemini-backed AI tools; class name is legacy.
- `data/quick_comments.json` — packaged quick comments.
- `data/inspector_stats.json` — packaged inspector stats.
- `data/inspection_types.csv` — packaged inspection-type rules/config.
- `tessdata/eng.traineddata` — OCR data for PDF/design extraction.
- `RedHelp.pdf` — embedded help PDF.
- `red2_dev.ico` — current app icon used by the project.
- `scripts/publish-release.sh` — release publisher to GitHub.
- `scripts/update_red.bat` — user-downloadable installer/updater.

## Generated/local/private files

Do not commit or publish these as source:

- `bin/`, `obj/`, `publish*/`, `releases/`, `work/`, `RED-temp/` — generated builds/test outputs.
- `settings.txt` — packaged into release output, but contains API key material and should not be committed as source.
- `licenses/`, `scripts/license*.lic`, `*.lic` — individual inspector license files.
- `userdata/` and any user `.ins` files — user data / inspection data.
- Old release zips under project root.

The `.gitignore` was updated to protect these.

## App identity/version

Current production identity:

- Version: `2.0.0`
- Main exe: `Red.exe`
- Assembly name: `Red`
- Product: `RED 2.0`
- AppData folder for production: `%LOCALAPPDATA%\RED`
- Legacy fallback/migration folder: `%LOCALAPPDATA%\InspectionEditor`
- Dev-only identity: any assembly name containing `Dev` uses `%LOCALAPPDATA%\RED-2.0-Dev`

Important change made during deployment prep: RED 2.0 is no longer treated as “Dev” simply because the version starts with `2.`. That matters because production RED 2.0 must use the normal RED AppData folder and normal GitHub updater behavior.

## License/settings/user data preservation

RED license lookup currently checks:

1. App install folder: `C:\Red\license.lic`
2. Production AppData: `%LOCALAPPDATA%\RED\license.lic`
3. Dev fallback only: `%LOCALAPPDATA%\RED\license.lic` when running a dev build

RED user data/settings are under:

- `%LOCALAPPDATA%\RED\red_app_settings.json`
- `%LOCALAPPDATA%\RED\inline-drawer-preferences.json`
- `%LOCALAPPDATA%\RED\userdata\...`
- legacy `%LOCALAPPDATA%\InspectionEditor\userdata\...`
- old install-folder `C:\Red\userdata\...`

`MainWindow.MigrateOldUserData()` merges old app-folder and legacy AppData `userdata` into the current AppData user-data folder without overwriting saved comments/prefixes/suffixes destructively.

The BAT updater backs up before install:

- `C:\Red`
- `%LOCALAPPDATA%\RED`
- `%LOCALAPPDATA%\InspectionEditor`
- `%LOCALAPPDATA%\RED-2.0-Dev`

Backup location on each user machine:

`%LOCALAPPDATA%\RED_Backups\before-red2-YYYYMMDD-HHMMSS\`

Log file:

`%LOCALAPPDATA%\RED_Backups\before-red2-YYYYMMDD-HHMMSS\update-red2.log`

## Build command

RED builds should be standalone/self-contained so inspectors do not need a .NET runtime installed:

```bash
cd /Users/trentfuller/Library/CloudStorage/Dropbox/P/PL
dotnet restore InspectionEditor.csproj -r win-x64
dotnet publish InspectionEditor.csproj -c Release -r win-x64 --self-contained true -o bin/Publish
echo 2.0.0 > bin/Publish/version.txt
cd bin/Publish
zip -r -q /tmp/Red-v2.0.0.zip . -x '*.pdb' '*.xml'
```

Required files verified in the publish output:

- `Red.exe`
- `Red.dll`
- `Red.deps.json`
- `Red.runtimeconfig.json`
- `version.txt`
- `quick_comments.json`
- `inspector_stats.json`
- `inspection_types.csv`
- `settings.txt`
- `tessdata/eng.traineddata`

## Release / updater mechanism

GitHub repo:

`fullinspect-source/Red`

Stable BAT URL:

`https://github.com/fullinspect-source/Red/releases/latest/download/update_red.bat`

The app’s built-in updater checks:

`https://api.github.com/repos/fullinspect-source/Red/releases/latest`

Then it downloads the first `.zip` release asset and compares the latest tag against the running app version.

## User migration BAT behavior

`scripts/update_red.bat` now:

1. Elevates to Administrator.
2. Logs everything to `%LOCALAPPDATA%\RED_Backups\before-red2-YYYYMMDD-HHMMSS\update-red2.log`.
3. Closes `Red.exe`, `Red2Dev.exe`, and `InspectionEditor.exe`.
4. Backs up install folder and AppData folders before touching install files.
5. Downloads latest RED GitHub release zip.
6. Extracts and verifies `Red.exe` exists before install.
7. Installs to `C:\Red`.
8. Removes old `InspectionEditor.exe` / `Red2Dev.exe` from `C:\Red` after successful install.
9. Restores `license.lic`, preferences, notify message, and `userdata` where safe.
10. Does not restore old `settings.txt` if it starts with `xai-`; it keeps the packaged RED 2.0 Gemini settings instead.
11. Recreates Desktop shortcut `RED.lnk`.
12. Launches `C:\Red\Red.exe`.

## Manual Windows checks before emailing users

Run these on Grace’s Windows tablet before sending the email:

1. Download the BAT from the latest-release URL and run it as a normal user.
2. Confirm it elevates properly.
3. Confirm backup folder/log is created.
4. Confirm `C:\Red\Red.exe` exists after install.
5. Confirm `C:\Red\license.lic` is preserved or restored.
6. Confirm RED launches without license prompt on an already licensed machine.
7. Confirm saved comments / prefixes / suffixes still appear.
8. Confirm opening an existing Dropbox `.ins` works and no Dropbox inspection files were moved/deleted.
9. Triple-click logo or use update behavior to confirm the built-in GitHub update path still reaches latest release and reports up to date.
10. Confirm Desktop shortcut launches RED 2.0.

## Known risks

- I built and packaged on Mac using Windows targeting; WPF launch/install still needs real Windows verification.
- `SixLabors.ImageSharp` 3.1.5 has NuGet vulnerability warnings. Build still succeeds, but this should be upgraded after rollout pressure is off.
- `GrokApiClient` is still a legacy class name even though the code now uses Gemini models.
- The release ZIP includes `settings.txt` because field users need RED to work without API-key prompts; keep `settings.txt` out of source commits.
- If Windows SmartScreen blocks the BAT/installer, users must choose “More info” → “Run anyway.”
