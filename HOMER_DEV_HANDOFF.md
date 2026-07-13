# RED Developer Handoff for Homer

This file is the concise cross-session source of development context for work performed outside Homer sessions. Update it before finishing every meaningful RED code change. Add a newest-first entry under **Latest Changes** with the date, version (when applicable), commit, exact files changed, behavior, verification, and deployment status. Never place secrets, credentials, private inspection data, or license contents here.

## Latest Changes

### 2026-07-12 — RED 2.0.11 — `8fcd0b024edfb1cbb9bfb7fd31cafb92ea31cdec`

- **Files changed:** `App.xaml.cs`, `InspectionEditor.csproj`, `InspectionPickerWindow.xaml.cs`, `MainWindow.xaml.cs`, and new `Services/AppUpdateService.cs`.
- **What changed:** Added a shared production self-updater. Normal application startup calls `AppUpdateService.CheckAndInstallIfAvailableAsync()` and checks at most once per 24 hours using `%LOCALAPPDATA%\RED\.last_app_update_check`. Triple-clicking the RED logo on the opening/About screen calls the same service with `force: true`, bypasses the throttle, downloads the latest GitHub ZIP, starts the elevated copy/restart batch, and exits RED. The editor-only updater interval was also changed from 12 to 24 hours.
- **What was broken:** In 2.0.10, the opening/About triple-click fetched and displayed the latest version but did not install it. The automatic app check was attached to the editor window rather than reliably running when RED itself opened, and its interval was 12 hours.
- **Verification:** Release build succeeded with 0 errors (16 pre-existing compiler warnings); 11 static tests passed; self-contained win-x64 publish succeeded; `Red.exe` reported product/file version 2.0.11; ZIP SHA-256 was `612BF4BE56A3645EAFE744BA849CD21ED79BD3AA7A86EFCE8B9211A42F973DDB`; GitHub latest-release API returned v2.0.11 and the expected ZIP asset. The destructive end-to-end overwrite/restart path was not executed against the development checkout.
- **Deployed to GitHub:** Yes. https://github.com/fullinspect-source/Red/releases/tag/v2.0.11

### 2026-07-12 — RED 2.0.10 — `aea852e7ed1ed25b7fad92ac9a99cf82b50c1be7`

- **Files changed:** `InspectionEditor.csproj`, `docs/RED-2.0-RELEASE-NOTES.md`.
- **What changed:** Upgraded `SixLabors.ImageSharp` from 3.1.5 to 3.1.12 to resolve known crafted-GIF crash/infinite-loop advisories while retaining RED's existing image workflow.
- **Verification:** Build, publish, image smoke testing, and NuGet vulnerability audit were performed; the current dependency audit still reports no known vulnerable packages.
- **Deployed to GitHub:** Yes. https://github.com/fullinspect-source/Red/releases/tag/v2.0.10

### 2026-07-12 — RED 2.0.9 — `bca68cba3a9941671a757ade8044f47cf19dbf69`

- **Files changed:** `InspectionEditor.csproj`, `MainWindow.xaml.cs`, new `Services/NumberpadDefaultService.cs`, `docs/RED-2.0-RELEASE-NOTES.md`, `docs/RedHelp.html`, new `tests/numberpad_defaults_static_test.py`, and `tests/red_ai_prompt_static_test.py`.
- **What changed:** Added archive-informed Numberpad defaults for recurring numeric prompts in AFI, CPP, CPR, HEF, HER, HET, IEF, IER, and SRP. Defaults include minimum, maximum, increment, and whether Camera accompanies Numberpad. CPP/SRP corner and interior beam measurements use quarter-inch increments; prepour beam-width defaults are 0–15. Added a compact collapsed-row slider when Numberpad is active and no comment preview exists. Compact and expanded sliders stay synchronized. A parked slider does not write a value until the user interacts with it. Default numeric workflows omit Comments; applicable evidence workflows use Numberpad plus Camera. `ApplyNumberpadDefaultMigration()` forces migration version 1 once, after which user customization is remembered normally.
- **Verification:** Release build and the static Numberpad/AI tests passed.
- **Deployed to GitHub:** Yes. https://github.com/fullinspect-source/Red/releases/tag/v2.0.9

### 2026-07-12 — RED 2.0.8 — `7161e0bbc36d9334bc87927c2fb7132a458a1c9c`

- **Files changed:** `AppIdentity.cs`, `InspectionEditor.csproj`, `InspectionPickerWindow.xaml`, `MainWindow.xaml.cs`, `scripts/publish-release.sh` (present in that commit, no longer present on current `main`), and `scripts/update_red.bat`.
- **What changed:** Added checklist/section progress coloring and completion-state UI, and centralized runtime version/date display through assembly metadata and `AppIdentity`.
- **Verification:** Release build/publish was performed for the production release.
- **Deployed to GitHub:** Yes. https://github.com/fullinspect-source/Red/releases/tag/v2.0.8

## Current Production State

- **Production version:** 2.0.11.
- **Release date in the application:** 2026-07-12.
- **Latest GitHub production release:** v2.0.11, non-draft and non-prerelease. It was published at 2026-07-13 02:19:05 UTC (2026-07-12 in the America/Chicago release session).
- **Release URL:** https://github.com/fullinspect-source/Red/releases/tag/v2.0.11
- **Release asset:** `Red-v2.0.11-win-x64.zip` (90,539,960 bytes).
- **Production executable/install:** `Red.exe` in `C:\Red`.
- **Production user data:** `%LOCALAPPDATA%\RED` via `AppIdentity.LocalAppDataPath`.
- **Runtime:** .NET 8 WPF, target `net8.0-windows10.0.19041.0`, self-contained win-x64 production publish.

## Current Sources of Truth

Use code and live GitHub state before older prose documentation:

| Concern | Authoritative source |
|---|---|
| Application version | `<Version>` in `InspectionEditor.csproj` (currently 2.0.11). Do not hard-code a second version elsewhere. |
| Release date | `<ReleaseDate>` in `InspectionEditor.csproj`; emitted as assembly metadata (currently 2026-07-12). |
| Runtime identity/UI display | `AppIdentity.cs`: `Version`, `VersionDisplay`, `ReleaseDate`, `PublishedDateText`, `VersionWithPublishedDate`, window titles, and app-data folder selection. |
| Startup app update | `App.xaml.cs`, `OnStartup()` calling `AppUpdateService.CheckAndInstallIfAvailableAsync()`. |
| Shared updater | `Services/AppUpdateService.cs`: `AppUpdateResult`, `CheckAndInstallIfAvailableAsync`, version comparison, GitHub release parsing, download/extract, and elevated batch handoff. |
| Opening/About triple-click | `InspectionPickerWindow.xaml.cs`: `AboutLogo_MouseLeftButtonDown()` and `ForceAboutUpdateAsync()`; force mode bypasses the daily marker. |
| Editor update fallback | `MainWindow.xaml.cs`: `ShouldRunStartupAppUpdateCheck()`, `WelcomeLogo_MouseLeftButtonDown()`, and `CheckForUpdatesAsync()`. This is older duplicated logic and is not the primary startup implementation. |
| Numberpad product defaults | `Services/NumberpadDefaultService.cs`, especially `Get()`, `GetPrePourProfile()`, and `MigrationVersion`. |
| Numberpad integration/UI | `MainWindow.xaml.cs`: `ApplyNumberpadDefaultMigration()`, `CreateCollapsedInlineNumberpadSlider()`, `CreateInlineNumberpadTouchSlider()`, `SynchronizeInlineNumberpadSliders()`, and drawer preference helpers. |
| Build project/package inputs | `InspectionEditor.csproj`; `build-standalone.bat` is a convenience wrapper. |
| Manual field updater | `scripts/update_red.bat`; it backs up existing installation/user state, downloads GitHub's latest ZIP, installs to `C:\Red`, restores allowed local files, repairs the shortcut, and relaunches. |
| Release history | Git commits/tags plus GitHub Releases. `docs/RED-2.0-RELEASE-NOTES.md` covers 2.0.9/2.0.10 but does not yet contain 2.0.11. |

`README.md`, `BUILD.md`, `docs/ARCHITECTURE.md`, and `docs/RED-2.0-DEPLOYMENT.md` contain useful background but are partially stale. Examples: several still identify 2.0.0 as current, `BUILD.md` expects `InspectionEditor.exe`/a separately copied `RedHelp.pdf`/packaged `settings.txt`, and old deployment instructions expect `version.txt`. Current code produces `Red.exe`, embeds `RedHelp.pdf`, and deliberately excludes `settings.txt`.

## Recent User-Facing Behavior

- RED opens through the licensed startup flow in `App.xaml.cs`, shows `SplashWindow`, performs the once-daily app check, then checks runtime data and opens the configured startup surface.
- Triple-clicking the opening/About RED logo now performs a forced install check, not merely a version display. If a newer release is found, RED downloads it, starts the updater, exits, and is relaunched after files are copied.
- Numberpad-default checklist rows can expose a compact slider while collapsed. A comment preview wins the middle-row space if a comment exists. The expanded drawer retains its normal full slider; both slider instances synchronize.
- The compact slider's handle can be visually parked at the left without assigning the minimum/zero value. A value is committed only after user interaction.
- Matching numeric prompts receive Numberpad-only (`N`) or Numberpad-plus-Camera (`N+C`) product defaults. Comments are not defaulted for these numeric workflows. NI remains available through the normal inline controls.
- 2.0.8 added checklist/section completion colors; inspect the inline checklist construction/update methods in `MainWindow.xaml.cs` before modifying their state logic.

## Architecture and Workflow Notes

- This is a WPF application with substantial code-behind in `MainWindow.xaml.cs`; services under `Services/` isolate selected domains, but UI/update logic is not fully service-oriented.
- Inspection data models live in `Models/InspectionModels.cs`; saving uses `Services/SurgicalSaveService.cs` to preserve existing INS structure where possible.
- The new updater service is shared by startup and the opening/About screen. `MainWindow.xaml.cs` still contains an independent legacy updater implementation, creating duplication and drift risk.
- Numberpad defaults are keyed by normalized inspection code and prompt text rather than unstable item numbers/template IDs. Changing prompt normalization or inspection-code matching can silently change which defaults apply.
- User drawer/range choices are persisted under `%LOCALAPPDATA%\RED` in `inline-drawer-preferences.json`. Migration marker `NumberpadDefaultsMigrationVersion` prevents product defaults from repeatedly overwriting later user choices.
- Runtime datasets are flattened into the publish root by `InspectionEditor.csproj`: `quick_comments.json`, `inspector_stats.json`, and `inspection_types.csv`. Tesseract data is copied under `tessdata/` when present. `RedHelp.pdf` is embedded as a resource.

## Security-Sensitive Rules

- Never commit, print, document, or package API keys, GitHub credentials/tokens, private license data, or private INS/report contents.
- `.gitignore` excludes `settings.txt`, `EmbeddedApiKeyProvider.Generated.cs`, `licenses/`, `*.ins`, user data, build output, and release ZIPs.
- `InspectionEditor.csproj` explicitly sets `settings.txt` to `CopyToOutputDirectory=Never` and `CopyToPublishDirectory=Never`. Do not reverse this. A local `settings.txt` may be used by `SettingsWindow.LoadApiKeyFromFile()`; legacy values beginning with `xai-` are rejected because current AI calls use Gemini.
- `EmbeddedApiKeyProvider.cs` contains only the loader/obfuscation mechanism. Any private generated partial must remain in gitignored `EmbeddedApiKeyProvider.Generated.cs` and must not be included in this handoff. Obfuscation is not equivalent to secure secret storage; avoid exposing built binaries unnecessarily.
- `Services/GrokApiClient.cs` currently calls Google's Gemini API using an `x-goog-api-key` header. The class name is historical. Do not assume it still calls Grok/xAI or that legacy xAI collection search works; `SearchCollectionAsync()` is intentionally disabled pending knowledge-base migration.
- Licensing is implemented in `Services/LicenseService.cs`. `license.lic` lives beside the executable; timestamp/tamper markers live under `%LOCALAPPDATA%\RED`. Licensing includes machine fingerprinting, expiration/grace behavior, clock/tamper detection, and a destructive cleanup path. Do not casually modify or trigger that path during development/testing.
- The packaged release must not contain a user `license.lic`, user preferences, `settings.txt`, generated key source, or private inspection data. The verified 2.0.11 publish contained the three public runtime datasets and Tesseract data; `RedHelp.pdf` is embedded. A third-party dependency file named `LICENSE` is normal and is not RED's user license.
- `Services/AppUpdateService.cs` trusts GitHub's latest release endpoint over HTTPS, selects a `.zip` asset, requires extracted `Red.exe`, then starts an elevated batch to copy files over the running installation and restart. There is currently no cryptographic checksum/signature validation of the downloaded release asset.
- `scripts/update_red.bat` creates backups under `%LOCALAPPDATA%\RED_Backups`, preserves selected local license/preferences/userdata, and deliberately refuses to restore an old `settings.txt` beginning with `xai-`.

## Known Warnings, Debt, and Incomplete Work

- Release build currently succeeds with **16 compiler warnings and 0 errors**. Warnings are existing nullability/event-signature/unused-field issues, including `MainWindow.xaml.cs`, `InspectionPickerWindow.xaml.cs`, and `Services/GrokApiClient.cs`; do not report the build as warning-free.
- Automated coverage is only two Python static test files: `tests/numberpad_defaults_static_test.py` (6 tests) and `tests/red_ai_prompt_static_test.py` (5 tests). There is no automated WPF interaction suite or true updater integration test.
- The 2.0.11 self-update overwrite/elevation/restart sequence was source-checked, built, packaged, and release-API verified but was not destructively exercised against the development installation. Perform a manual Windows field-machine update test before assuming every elevation/environment edge case is covered.
- App update logic is duplicated: primary shared `AppUpdateService` plus older `MainWindow.CheckForUpdatesAsync()`. Consolidation is desirable to prevent behavior drift.
- Current updater asset selection uses the first matching ZIP rather than an exact platform-specific filename and does not verify a published hash/signature.
- Several documentation files are stale as described above. This handoff and the current code/GitHub release take precedence until those docs are refreshed.
- `docs/RED-2.0-RELEASE-NOTES.md` lacks a 2.0.11 entry.
- The current v2.0.11 GitHub release has only `Red-v2.0.11-win-x64.zip`; older releases also attached `update_red.bat`. The stable manual updater still exists in the repository at `scripts/update_red.bat` but is not attached to v2.0.11.
- Local Git tags observed after the release include v2.0.11 and v2.0.8, while GitHub Releases also contain v2.0.9 and v2.0.10. Do not infer release absence solely from a local `git tag` list.
- `Red.pdb` is currently present in the self-contained ZIP. This is not a secret by itself but is unnecessary for most field deployments and increases exposed implementation metadata.

## Assumptions Homer Must Not Make

- Do not assume 2.0.0, 2.0.8, 2.0.9, or 2.0.10 is production; production is 2.0.11.
- Do not assume the opening-screen triple-click only reports an update. In 2.0.11 it force-checks and initiates installation/restart.
- Do not assume startup checking occurs only after an inspection/editor window opens. The primary check now runs from `App.OnStartup()` once per 24 hours.
- Do not assume every numeric row should have Numberpad. Defaults are an explicit inspection-code/prompt allowlist in `NumberpadDefaultService`.
- Do not assume default migration should overwrite preferences on every launch. Migration version 1 forces the product defaults once; later customization must persist.
- Do not assume a collapsed slider's leftmost visual handle means zero has been entered.
- Do not assume numeric rows can never have comments. If a comment exists, its preview takes precedence over the collapsed slider.
- Do not assume `GrokApiClient` uses xAI. It currently uses Gemini; historical names remain.
- Do not package `settings.txt`, generated key source, license files, INS files, or user data even if older documentation says to copy settings into the publish folder.
- Do not derive displayed versions from literal strings in XAML. Use `InspectionEditor.csproj` plus `AppIdentity`.

## Build, Test, Publish, and Release Commands

Run from the repository root on Windows. The portable SDK path below is the one used in the 2026-07-12 Codex session; a normally installed .NET 8 SDK may be invoked as `dotnet` instead.

```powershell
$dotnet = 'C:\Users\grace\Documents\Codex\dotnet-sdk\dotnet.exe'
$git = 'C:\Users\grace\Documents\Codex\git\cmd\git.exe'
$gh = 'C:\Users\grace\Documents\Codex\bin\gh.exe'
$env:DOTNET_CLI_HOME = 'C:\Users\grace\Documents\Codex\2026-07-12\d\work\dotnet-home'
$env:NUGET_PACKAGES = 'C:\Users\grace\Documents\Codex\2026-07-12\d\work\nuget-packages'
```

Restore/build and dependency audit:

```powershell
& $dotnet restore InspectionEditor.csproj -r win-x64
& $dotnet build InspectionEditor.csproj -c Release --no-restore
& $dotnet list InspectionEditor.csproj package --vulnerable --include-transitive
```

Static tests:

```powershell
python tests\numberpad_defaults_static_test.py
python tests\red_ai_prompt_static_test.py
```

Self-contained publish and ZIP (replace the version in paths):

```powershell
$version = '2.0.11'
$stage = "C:\Users\grace\Documents\Codex\2026-07-12\d\work\release-stage-$version"
$zip = "C:\Users\grace\Documents\Codex\2026-07-12\d\outputs\Red-v$version-win-x64.zip"
& $dotnet publish InspectionEditor.csproj -c Release -r win-x64 --self-contained true --no-restore -o $stage
Compress-Archive -Path "$stage\*" -DestinationPath $zip -Force
(Get-Item "$stage\Red.exe").VersionInfo | Select-Object ProductVersion, FileVersion
Get-FileHash $zip -Algorithm SHA256
```

Pre-commit verification:

```powershell
& $git diff --check
& $git status --short
& $git diff --stat
```

Commit, tag, push, and create the GitHub release (write accurate release notes; do not blindly reuse this text):

```powershell
& $git add -- <exact-files-in-scope>
& $git commit -m '<specific release message>'
& $git tag -a "v$version" -m "RED $version"
& $git push origin main
& $git push origin "v$version"
& $gh release create "v$version" "$zip#RED v$version Windows x64" --repo fullinspect-source/Red --title "RED v$version" --notes '<specific notes>' --latest
```

Live release verification:

```powershell
& $gh api repos/fullinspect-source/Red/releases/latest --jq '{tag_name: .tag_name, draft: .draft, prerelease: .prerelease, url: .html_url, assets: [.assets[] | {name: .name, size: .size}]}'
& $git ls-remote --tags origin "refs/tags/v$version"
```

Before any future commit, update this file's **Latest Changes** section in the same commit as the meaningful code change. After deployment, ensure its deployment field and verification details are accurate; if deployment happens after the code commit, make a small factual follow-up documentation commit.

## Relevant Commits and Links

- 2.0.11 updater correction: `8fcd0b024edfb1cbb9bfb7fd31cafb92ea31cdec`
- 2.0.10 ImageSharp security update: `aea852e7ed1ed25b7fad92ac9a99cf82b50c1be7`
- 2.0.9 Numberpad defaults/compact sliders: `bca68cba3a9941671a757ade8044f47cf19dbf69`
- 2.0.8 checklist progress colors/AppIdentity: `7161e0bbc36d9334bc87927c2fb7132a458a1c9c`
- 2.0.7 fixes: `e4f13cf5335170d722cfcdc6398cdb1b037f8c02`
- Current production release: https://github.com/fullinspect-source/Red/releases/tag/v2.0.11
- Repository: https://github.com/fullinspect-source/Red
