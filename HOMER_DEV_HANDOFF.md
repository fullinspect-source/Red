# RED Developer Handoff for Homer

This file is the concise cross-session source of development context for work performed outside Homer sessions. Update it before finishing every meaningful RED code change. Add a newest-first entry under **Latest Changes** with the date, version (when applicable), commit, exact files changed, behavior, verification, and deployment status. Never place secrets, credentials, private inspection data, or license contents here.

## Latest Changes

### 2026-10-18 — RED 2.2.3 — F-004: every item control, with INSPECT's values

- **Base:** 2.2.2 (`34162d2`). F-004 was built in the Dropbox copy (`Dropbox\P\PL`) and merged here file by file.
  - MainWindow.xaml.cs, Services/SurgicalSaveService.cs and tests/InsRoundTripHarness/Program.cs were three-way
    merged: base = PL before F-004, theirs = PL after F-004. The 2.2.1 review-fix hunks were identical on both sides,
    and this tree's 2.1.43 checklist snippets were kept.
  - A second merge from the ORDERS session's pre-port copies gave the same files.
  - The other files took only their F-004 hunks.
- **Files changed:**
  - New:
    - `Services/ItemControls.cs`: the ControlName vocabulary and value sets, the NA→NI scope, number-pad rules N1-N7,
      en-US time/date, the phone mask, upload values, SearchLookup Add New, copy rules and the bulk-writer value guard.
    - `Services/ItemAutoFailRules.cs`: the four automatic Fail rules, moved out of MainWindow unchanged.
    - `tests/ItemControlsHarness/`: 78 checks, plus `--corpus` and `--ui-fixture` modes.
  - Modified:
    - `MainWindow.xaml.cs`: the classic and inline editors per control; time/date pickers; upload, document and
      button controls; Add New; the unsupported-control badge; the stored-value note; section "All" labels; the
      keypad rules; the keyboard numeric editor; the phone mask; Item Fixed Confirmation; Copy/Remove; transcription
      guard.
    - `Services/SurgicalSaveService.cs`: NA→NI at load, held back on a save with no other change; `ProjectCurrent`;
      Memo "ok" and PictureUpload "[Uploaded]" derived at save; SearchLookup ValueList and DocumentUpload Template
      patched; copies reset Template and ButtonText.
    - `Services/ChecklistItemVisibility.cs`: only `Hide` hides a row.
    - `Services/TemplateService.cs` and `Services/EnergyComplianceService.cs`: values pass the control guard.
  - Tests:
    - `tests/ChecklistVisibilityHarness/Program.cs` and `tests/InsRoundTripHarness/Program.cs` (corpus mode, NA→NI
      aware).
    - Seven harness csproj files now also link `Services/ItemControls.cs`.
    - Five Python source-shape tests were updated to the F-004 code:
      - framing_design_extraction_static_test, item_requirement_static_test and red_21_layout_static_test (new
        source strings);
      - inline_text_ni_regression_test and ocr_unit_scope_test (stubs for the new helpers). NI toggled off is now
        null, as INSPECT does it.
  - Also changed: the version (2.2.3) in the csproj and the four release-metadata tests, these release notes and this
    handoff.
- **Behavior:** spec `Dropbox/ios-port/program/features/F-004-item-controls`. See SPEC, ACCEPTANCE and
  NOTES-windows.md, plus owner Q-11, Q-12, Q-13 (default a), Q-15 and Q-16 (default). Released alone by owner
  decision (ROADMAP "Release policy", 2026-10-19).
- **Verification:**
  - Debug build `--no-incremental`: 0 errors.
  - Harnesses, all exit 0: InsRoundTrip 71, SaveLoss 152, FailedSaveRecovery 17, LastEdit 74, OrientationPdf 345,
    ChecklistVisibility 7, InspectUserType 131, FeeToken and ItemControls 78.
  - The regenerated lifecycle probe is unchanged.
  - Python: 35/45 (PYTHONUTF8=1); the other 10 are the known environment failures.
  - Corpus round trip on a sandbox copy (1703 files): A-002-02 and A-002-01 1703/1703, V3 18/18, V4 2/2 (was 0/2; the
    difference was INSPECT's own NA→NI), fees 1703/1703.
  - F-004 corpus run on a sandbox copy: no-edit open/close wrote nothing 1704/1704, and 0 values outside their set
    across 76 FormIds.
  - In the Dropbox copy: an isolated dev-build UI click-through with a synthetic report covering every control.
- **Deployment:** pending.

### 2026-10-18 — RED 2.2.2 — F-003 rest: Archive retention row, one profile guard, O8 folder helper

- **Base:** 2.2.1 (`95ccd6d`). F-003 was finished in the Dropbox copy (`Dropbox\P\PL`). Every file it touches was byte-identical here to its pre-F-003 state, so the files were copied whole from a frozen snapshot taken before the F-004 work started in PL. MainWindow.xaml.cs and InspectionPickerWindow.xaml.cs are not touched.
- **Files changed:**
  - New:
    - `Services/InspectFolders.cs` (O8 folder rule) and `Services/InspectLiveSettings.cs` (ArchiveRetention read live, F-015 N1/N2/N4);
    - `ProfileGuard.cs` (the one O9 guard call for Submit, Approve and Add);
    - `tests/InspectConfigReadOnlyCheck/` (F-003 V2 read-only check on INSPECT's real config files).
  - Modified:
    - `Services/InspectorProfileService.cs`: `CompleteProfile`/`RequireComplete` replace `AddToMyListProfile`/`ForAddToMyList`; `StampNewInspection`, `Initials`.
    - `Services/StrandMyListService.cs`: MyList is created through `InspectFolders`; initials come from the helper.
    - `App.xaml.cs`: `ShowHomeSettings`.
    - The profile card and Add: `InspectionPickerWindow.InspectorProfile.cs` (retention row, `OpenSettingsTab`), `InspectionPickerWindow.xaml` (two TextBlocks), `InspectionPickerWindow.AddToMyList.cs` and `OrderRequestWindow.xaml.cs` (both now call `ProfileGuard.Require`).
    - Tests: `tests/InspectUserTypeHarness` (Program.cs and csproj) and `tests/FeeTokenHarness/FeeTokenHarness.csproj` (links the two files `StrandMyListService` now needs).
  - Also changed: the version (2.2.2) in the csproj and the four release-metadata tests, these release notes and this handoff.
- **Behavior:** spec `Dropbox/ios-port/program/features/F-003-settings-import` (SPEC, ACCEPTANCE, NOTES-windows.md; owner answers 2026-10-18):
  - Settings shows the full F-015 N4 retention text, read from `{IF}\config.json`, then `.bak`, then 40. With 0 or fewer days it shows the first sentence plus the N2 refusal line. RED still deletes nothing from the Archive (F-015 is not built).
  - The Add to My List guard checks O9's three items only: Strand username with @, a two-word name, and a non-blank user type. The start-up lock still checks six.
  - RED creates MyList, Outbox or Archive only right before writing; never Temp, Documents, Data or Jobs; Review and Logs only through their own named calls.
- **Verification:**
  - Debug build `--no-incremental`: 0 errors.
  - Harnesses, all exit 0: InsRoundTrip 71, SaveLoss 152, FailedSaveRecovery 17, LastEdit 74, OrientationPdf 345,
    ChecklistVisibility 6, InspectUserType 131 (58 new F-003 checks) and FeeToken.
  - Python: 35/45 (PYTHONUTF8=1); the other 10 are the known environment failures.
  - `tests/InspectConfigReadOnlyCheck` against the real INSPECT config files on the build PC: all unchanged (SHA-256,
    size, LastWriteTime), and no entry was added next to them.
  - The corpus round trip was not re-run, because no save code changed.
  - Before the port, in the Dropbox copy, an isolated dev-build UI run showed the retention row and the 0-day line,
    and starting RED created no folders.
- **Deployment:** published 2026-10-18 as GitHub release `v2.2.2` (commit `aa33e4b`, assets `Red-v2.2.2.zip` 89.2 MB with 502 entries, and `update_red.bat`), marked Latest. `releases/latest` returns v2.2.2, the zip size matches the local build, and the download answers HTTP 200. The packaged Red.exe (2.2.2+aa33e4b) was started once on the build PC: "RED v2.2.2 - My List". Installed copies update on their next start, at most one check per 24 hours. Copies that had not picked up 2.2.1 go straight to 2.2.2, which contains it.

### 2026-10-18 — RED 2.2.1 — F-002: INSPECT-faithful .ins saving, changed-on-disk stop with Overwrite

- **Base:** 2.2.0 (`924c549`). F-002 was developed in the Dropbox copy and merged here file by file. MainWindow.xaml.cs, Models/InspectionModels.cs and InspectionPickerWindow.xaml.cs were three-way merged against 2.1.42 (`4653e33`); F-002's pre-change backups equal 2.1.42, so only F-002 hunks came over. The other files were unchanged here since 2.1.42 and were taken whole.
- **Files changed:**
  - New: `Services/InspectionFileReader.cs`, `Services/InspectProjectionWriter.cs`, `tests/InsRoundTripHarness/`.
  - Modified: `Services/SurgicalSaveService.cs`, `Services/AtomicInspectionWriter.cs`, `Models/InspectionModels.cs` (`Item.CopiedFrom`), `MainWindow.xaml.cs` (save, load and duplicate code; the four-choice changed-on-disk window with Overwrite), and `InspectionPickerWindow.xaml.cs` (`ParseInspectionFile` drops vanished files).
  - Also modified: six harness csproj files, `tests/LastEditHarness/Program.cs`, `tests/FailedSaveRecoveryHarness/Program.cs`, `tests/template_first_open_*`, `tests/generate_save_lifecycle_probe.py` and `tests/SaveLossHarness/LifecycleProbe.Generated.cs`; plus the version (2.2.1) in the csproj and the four release-metadata tests, these release notes and this handoff.
- **Behavior:** spec `Dropbox/ios-port/program/features/F-002-ins-roundtrip` (SPEC, ACCEPTANCE, NOTES-windows.md; owner Q-02, Q-21, Q-48).
  - Mode A preserving save: only edited values change.
  - E1 changed-on-disk stop: Reload, Save As Copy, a twice-confirmed Overwrite that keeps the newer file's fees, or Keep editing. A moved report is never recreated.
  - R2-R5 busy and damaged reads. A10 staging in `{IF}\MyList\.red-save-staging\{folder}` (changed from F-002's `%LOCALAPPDATA%\RED\SaveStaging`; see below).
- **Release-review fixes made here** (port to Dropbox\P\PL; see shared notes `red-2.2.1-release.md`):
  - `AtomicInspectionWriter.SidecarDirectory`:
    - Review, Archive, Outbox and Temp\{date} stage in a hidden `MyList\.red-save-staging\{folder}` in the same tree. That is on the same volume for any drive, share, junction or mount point. Each tree and folder has its own trim pool, and A10 allows sidecars only in MyList.
    - `ReportFolderAvailable`: a missing folder or drive is a retryable `folder-unavailable`, not a moved report.
  - `InspectionChangedOnDiskException.RecoveryPath`: the conflict window promises a recovery copy only when one exists.
  - `FailedSaveRecoveryService.PreserveReplaced` and `OverwriteChangedFile` (which now returns the kept path): the replaced newer file is kept, untrimmed, in Recovery before an Overwrite. If it can't be kept, nothing is overwritten.
  - `SurgicalSaveService`: `MissingReport` (moved vs unavailable) and `PreserveUnsavedEdits`.
  - `MainWindow`:
    - no save or close while a report is loading;
    - the conflict actions are bound to the model they were opened for (`IsStillCurrent`);
    - Reload is transactional: locks and edits are kept until the newer copy loads, a recovery copy is made first, and the rules are skipped only for the same code and builder;
    - close-without-saving makes a recovery copy first;
    - a pending PDF capture no longer dead-ends the conflict: `DoSave` and `MainWindow_Closing` show the choices first when paused, and `OfferToLeavePendingPdfs` lets Reload or Close leave uncaptured PDF working copies on disk;
    - while saving is paused, ONE rolling recovery copy is kept (`FailedSaveRecoveryService.PreserveRolling`, `paused-edits-*.ins`), not one per edit.
  - `tests/generate_save_lifecycle_probe.py`: stubs and 2 new checks; `SaveLossHarness` probe regenerated.
- **Verification:**
  - Debug build: 0 errors.
  - `tests/InsRoundTripHarness`: 71/71, with new checks for Overwrite's kept copy, MyList staging, the unavailable folder and the rolling paused copy.
  - SaveLoss (with 10 lifecycle checks), FailedSaveRecovery (17), LastEdit (74), OrientationPdf (345), ChecklistVisibility (6), InspectUserType and FeeToken all pass.
  - Python: 35/45 (PYTHONUTF8=1); the other 10 are the same environment failures as untouched 2.1.43.
  - Corpus round trip on a sandbox copy of 1703 real files: 1703/1703 byte-identical, 0 load errors, no-edit save wrote nothing, 18/18 INSPECT Temp snapshots, fees 1703/1703.
  - Five multi-agent review and verify rounds were run; every confirmed release blocker is fixed above, and the final check found none.
  - The packaged Red.exe (2.2.1+b847fd1) was started once on the build PC: "RED v2.2.1 - My List".
- **Not clicked through in the running app:** the four-choice conflict window, Overwrite's second warning, Reload and the PDF-pending prompts (their service paths are covered by the harnesses).
- **Deployment:** published 2026-10-18 as GitHub release `v2.2.1` (commit `b847fd1`, assets `Red-v2.2.1.zip` 89.2 MB and `update_red.bat`), marked Latest. `releases/latest` returns v2.2.1 and the zip downloads (HTTP 200). Installed copies update on their next start, at most one check per 24 hours.

### 2026-10-18 — RED 2.2.0 — ORDERS tab: live Strand schedule (INSPECT 2026 Schedule parity)

- **Base:** `origin/main` at `f2ac28b` (2.1.43). The ORDERS work was developed in the Dropbox copy (2.1.42 base) and moved over file by file. The Dropbox copy's older `Services/GrokApiClient.cs` (gemini-3.5 fallback) was deliberately NOT carried over.
- **Files changed:**
  - New: `InspectionPickerWindow.Orders.cs`, `.StrandAccount.cs`, `.AddToMyList.cs`, `.OrdersFilter.cs`, `OrderRequestWindow.xaml(.cs)`, `OrdersDialogs.cs`, `Services/StrandScheduleService.cs`, `Services/StrandCredentialService.cs`, `Services/StrandMyListService.cs`, `Services/InspectUserType.cs`, `Services/InspectorProfileService.cs`, `InspectionPickerWindow.InspectorProfile.cs`, `docs/ORDERS_SCHEDULE_SPEC.md`, `tests/FeeTokenHarness/`, `tests/InspectUserTypeHarness/`.
  - Modified: `InspectionPickerWindow.xaml(.cs)`, `App.xaml.cs` (profile gate, My List folder, kind log), `MainWindow.xaml.cs` (Open picker folder only), `Services/DiagnosticLogService.cs` (`Info`), `InspectionEditor.csproj` (2.2.0, `System.Security.Cryptography.ProtectedData`), the four release-metadata tests, these release notes, and this handoff.
- **Behavior:** see `docs/ORDERS_SCHEDULE_SPEC.md`. The Strand API, endpoints, Add to My List and the file format were reverse-engineered from INSPECT 2026 (2026.1012.2).
  - The Strand login is saved with DPAPI in `%LOCALAPPDATA%\RED\strand_account.json`, or imported from Inspect's `config.json`.
  - Assign is gated to the owner account. Delete follows the Strand permissions `Service Request Edit - All` / `Service Request Delete`.
  - The iPad fee token (decision D-03 = B) is owner-only and never persisted.
  - INSPECT user type (owner Q-22, F-003 O9/O10): one exact-ordinal classifier, `InspectUserType`. A blank type means Missing, never "Inspector"; only an absent key means "Inspector".
  - Inspector profile (F-003, owner 2026-10-18): RED's own copy in `%LOCALAPPDATA%\RED\inspector_profile.json` (atomic writes; deliberately not `red_app_settings.json`, which Settings rewrites whole and Reset RED Preferences deletes). First run imports INSPECT's S1/S2 values and, when RED has none, the Strand login. Import from INSPECT re-imports with a changes list and confirmation. INSPECT files are only read (shared access, 100/200/400 ms retries, `.bak` fallback with the S4 message).
  - Start-up lock (owner decisions 2026-10-18): required = two-word full name, Strand username with @, saved Strand password, default state, non-blank user type, existing Inspections folder (INSPECT's IsFormConfigValid). While any is missing RED opens on Settings with "Please insert missing profile information before anything else." and disables My List and ORDERS; a file argument opens the home window instead of the editor. The user type has no picker in RED. Add to My List uses the profile (folder, user type, initials) and refuses an incomplete one before any server call. My List follows the profile's Inspections folder (O7). The user kind is logged once per launch (O10).
- **Verification:**
  - Debug build: 0 errors.
  - 35 of 45 Python tests pass (run with PYTHONUTF8=1). The other 10 fail identically on untouched 2.1.43 for environmental reasons: no pymupdf, Mac-only fixture paths, temporary harness builds.
  - `tests/FeeTokenHarness`: ALL PASS.
  - `tests/InspectUserTypeHarness`: ALL PASS (73 checks: user type, blank UserType, Preinspector with no RequestFiles PUT, INSPECT config reading, full import, required items, changes list, profile store, single "[Unassigned]").
  - Multiple multi-agent review and verify rounds; all confirmed findings fixed. The packaged Red.exe was started once on the build PC: "RED v2.2.0 - My List", profile complete.
- **Assigned To:** a single "[Unassigned]" row. The server's own row is kept, with its username, as INSPECT's GetInspectorList does; RED adds one only when the server sends none (owner report, 2026-10-18).
- **Known gap:** survey requests (ServiceTypeId 7) should list crew chiefs (`Inspect/User/CrewChiefs`) as in INSPECT; RED lists inspectors.
- **Deployment:** published 2026-10-18 as GitHub release `v2.2.0` (commit `15fcbd2`, assets `Red-v2.2.0.zip` and `update_red.bat`), marked latest. Installed copies update on their next start, at most one check per 24 hours.

### 2026-09-01 — RED 2.1.23 — enforce one selected checklist row

- **Files changed:** `InspectionEditor.csproj`, `MainWindow.xaml.cs`, `tests/red_21_layout_static_test.py`, release metadata tests, release notes, and this handoff.
- **Cause:** Badge-driven selection painted the new row but did not always rebuild the previously selected row, allowing two cyan selection highlights while an unrelated drawer remained visible.
- **Fix:** `LoadItemEditor` now owns the full highlight handoff for every selection path: capture old item, collapse an old drawer, refresh the old row, then paint only the new row.
- **Verification/deployment:** Source commit/tag `95db7a6f169652aea04d10b698898ab2bf840772` passed all 97 Python regression tests and built/published standalone win-x64 with 0 errors and the same 16 existing warnings. NuGet found no vulnerable packages. GitHub release `v2.1.23` is live at https://github.com/fullinspect-source/Red/releases/tag/v2.1.23. Downloaded ZIP SHA-256 is `f41232074214b5b3950b1ef93a5042c961ffc415b9d9dde5b05b70348fc90bc3`; updater SHA-256 remains `94724c7c5dea2777e27f856e6697818f04d74fe11e265ed3e65ac95c7578f49d`. Dropbox source and `Publish-current` were synced with verified parity after a reversible 9-file backup.

### 2026-09-01 — RED 2.1.22 — complete single-tap and OCR isolation hardening

- **Files changed:** `InspectionEditor.csproj`, `MainWindow.xaml.cs`, `tests/red_21_layout_static_test.py`, release metadata tests, release notes, and this handoff.
- **Interaction fix:** Removed the remaining single-tap drawer expansion paths from Fail/status and value-choice badges, and made drawer close state synchronous before animation.
- **OCR fix:** Transcription option sets are item-scoped, best-option selection counts parsed pairs, and matching searches the combined display label plus full item name.
- **Verification/deployment:** Source commit/tag `391c6ee283d886fdec0f24f0da782a8679f0e8e4` passed all 97 Python regression tests and built/published standalone win-x64 with 0 errors and the same 16 existing warnings. NuGet found no vulnerable packages, and source/package scans found no prohibited settings, license, inspection, generated-key source, or plaintext AI key. GitHub release `v2.1.22` is live at https://github.com/fullinspect-source/Red/releases/tag/v2.1.22. Downloaded stable assets match SHA-256 `fdbce3a90329d920da4704d9f61b4da332738e348ad563015b69972b72dbabd5` for `Red-v2.1.22.zip` and `94724c7c5dea2777e27f856e6697818f04d74fe11e265ed3e65ac95c7578f49d` for `update_red.bat`. Dropbox tracked source and `Publish-current` were synced with verified parity after a reversible 9-file backup.

### 2026-09-01 — RED 2.1.21 — checklist drawer and AI OCR workflow fixes

- **Files changed:** `InspectionEditor.csproj`, `MainWindow.xaml.cs`, `tests/red_21_layout_static_test.py`, release metadata tests, release notes, and this handoff.
- **Interaction fix:** Photo Required remains a camera shortcut but no longer expands inline tools. First press selects and loads the fixed right pane; deliberate double-click toggles the row drawer; selecting another row closes the old drawer.
- **OCR fix:** Both classic and inline AI OCR result taps use one application path, immediately refresh the left checklist, extract the current row's matching value, and distribute matching paired fields such as U-Factor and SHGC using persisted inspection order rather than filtered visibility.
- **Verification/deployment:** Source commit/tag `f82f804245dcdb5ed33c0b3ad7ce92b5f08526bd` passed all 96 Python regression tests and built/published standalone win-x64 with 0 errors and the same 16 existing warnings. NuGet found no vulnerable packages, and source/package scans found no prohibited settings, license, inspection, generated-key source, or plaintext AI key. GitHub release `v2.1.21` is live at https://github.com/fullinspect-source/Red/releases/tag/v2.1.21. Downloaded stable assets match SHA-256 `9ca753734fc9ec0e3fb9de93aa80fdcea79606a20fa2c48796ba02831dd75d21` for `Red-v2.1.21.zip` and `94724c7c5dea2777e27f856e6697818f04d74fe11e265ed3e65ac95c7578f49d` for `update_red.bat`. Dropbox tracked source and `Publish-current` were synced with verified parity after a reversible 9-file backup.

### 2026-09-01 — RED 2.1.20 — restore 24-hour update interval

- **Files changed:** `InspectionEditor.csproj`, `App.xaml.cs`, `Services/AppUpdateService.cs`, startup/release metadata tests, release notes, and this handoff.
- **Decision:** Trent prefers the once-per-24-hours automatic app-update check. The fact that a same-day release requires triple-click or waiting for the interval is expected behavior, not a defect.
- **What changed:** Reverted 2.1.19's every-startup GitHub check and restored the 24-hour marker, while retaining triple-click as the immediate manual bypass and keeping network failures nonblocking.
- **Verification/deployment:** Source commit/tag `2e23c6cf6eece12a387bce7432aa829133167f85` built and published standalone win-x64 with 0 errors and the same 16 existing warnings; all 93 Python regression tests passed; dependency audit found no vulnerable packages; Gemini validation returned HTTP 200; package scans found no `settings.txt`, generated source, user license, INS data, or plaintext key. GitHub release `v2.1.20` is live at https://github.com/fullinspect-source/Red/releases/tag/v2.1.20. Downloaded stable assets match SHA-256 `b67fb0a3f36f8271e184f9e9d57b3f0e64df64436b3d8e8621389e7db24820cf` for `Red-v2.1.20.zip` and `94724c7c5dea2777e27f856e6697818f04d74fe11e265ed3e65ac95c7578f49d` for `update_red.bat`. Dropbox tracked source and `Publish-current` were synced with verified parity after a reversible 9-file backup.

### 2026-09-01 — RED 2.1.19 — automatic startup update check

- **Files changed:** `InspectionEditor.csproj`, `App.xaml.cs`, `Services/AppUpdateService.cs`, new `tests/startup_update_check_static_test.py`, release metadata tests, release notes, and this handoff.
- **Root cause:** RED wrote a `.last_app_update_check` marker after any successful GitHub check and skipped normal startup checks for 24 hours. If RED opened before a release was published, later openings that day did not see the new release; triple-click worked because force mode bypassed the marker.
- **What changed:** Removed the 24-hour throttle and marker write. RED now checks GitHub latest on every startup. Triple-click remains available as a manual retry, and network failures remain nonblocking.
- **Verification/deployment:** Source commit/tag `f23ccc597ec36de5d4ab72fade8389b335fa1606` built and published standalone win-x64 with 0 errors and the same 16 existing warnings; all 93 Python regression tests passed; dependency audit found no vulnerable packages; Gemini validation returned HTTP 200; package scans found no `settings.txt`, generated source, user license, INS data, or plaintext key. GitHub release `v2.1.19` is live at https://github.com/fullinspect-source/Red/releases/tag/v2.1.19. Downloaded stable assets match SHA-256 `38fc22844cebcc8ee92ded2ef46c949b95b407f60c843bf6c4fb2ae167eb8f77` for `Red-v2.1.19.zip` and `94724c7c5dea2777e27f856e6697818f04d74fe11e265ed3e65ac95c7578f49d` for `update_red.bat`. Dropbox tracked source and `Publish-current` were synced with verified parity after a reversible 8-file backup.

### 2026-09-01 — RED 2.1.18 — Plan Check safety hardening

- **Files changed:** `InspectionEditor.csproj`, `Models/PlanCheckModels.cs`, `PlanCheckWindow.xaml`, `PlanCheckWindow.xaml.cs`, `Services/PlanCheckService.cs`, `Services/SurgicalSaveService.cs`, `MainWindow.xaml.cs`, Plan Check/release metadata tests, release notes, and this handoff.
- **What changed:** Added attachment-load generation guards so a stale PDF cannot overwrite a newer selection; narrowed automatic marker phrases to avoid generic `steel`/`width`/`depth`/`slab` matches; records the attachment actually reviewed; adds NI / not applicable; marker taps now select without changing conclusions; and attachment persistence is gated to a completed `RedPlanCheck` result.
- **Verification/deployment:** Source commit/tag `172e34a243710457d649bb09a339a8ac46ba4354` built and published standalone win-x64 with 0 errors and the same 16 existing warnings; all 90 Python regression tests passed; dependency audit found no vulnerable packages; Gemini validation returned HTTP 200; package scans found no `settings.txt`, generated source, user license, INS data, or plaintext key. GitHub release `v2.1.18` is live at https://github.com/fullinspect-source/Red/releases/tag/v2.1.18. Downloaded stable assets match SHA-256 `2e06088d6d4181179850584ba16856a2fa7c130585f150a92a2488a89966e175` for `Red-v2.1.18.zip` and `94724c7c5dea2777e27f856e6697818f04d74fe11e265ed3e65ac95c7578f49d` for `update_red.bat`. Dropbox tracked source and `Publish-current` were synced with verified parity after a reversible 12-file backup.

### 2026-09-01 — RED 2.1.17 — CPP Plan Check Beta rescue

- **Files changed:** `InspectionEditor.csproj`, `MainWindow.xaml`, `MainWindow.xaml.cs`, `Services/SurgicalSaveService.cs`, new `PlanCheckWindow.xaml`, new `PlanCheckWindow.xaml.cs`, new `Models/PlanCheckModels.cs`, new `Services/PlanCheckService.cs`, new `tests/plan_check_static_test.py`, release metadata tests, release notes, and this handoff.
- **What changed:** Selectively transplanted the useful CPP Plan Check Beta from Trent's newer Dropbox work onto live GitHub RED 2.1.16 rather than publishing the stale 2.0.22 tree. CPP reports with embedded PDF attachments now expose five inspector-owned plan checks, movable plan markers, optional deficiency crops, optional flattened annotated-PDF attachment, and durable completion metadata. Existing attachment tokens are preserved and only newly created attachments are appended during surgical save.
- **Deliberately excluded:** The Dropbox tree's stale 2.0.22 version line and broad replacements to RED 2.1 split-pane UI, AI routing, OCR support, updater, datasets, energy logic, and quick comments were not copied because they would regress newer production behavior.
- **Verification/deployment:** Source commit/tag `9d5e96e85d32a630f29e1e8bbdf7f5187a22b776` built and published standalone win-x64 with 0 errors and the same 16 existing warnings; all 89 Python regression tests passed; dependency audit found no vulnerable packages; the embedded Gemini credential returned HTTP 200 without exposing the key; package scans found no `settings.txt`, generated source, user license, INS data, or plaintext key. GitHub release `v2.1.17` is live at https://github.com/fullinspect-source/Red/releases/tag/v2.1.17. The downloaded stable assets match SHA-256 `c875a7ffc568d12fa37c3a8421148f42f8765a1dd7cb247e04f950441b7edcc4` for `Red-v2.1.17.zip` and `94724c7c5dea2777e27f856e6697818f04d74fe11e265ed3e65ac95c7578f49d` for `update_red.bat`. Dropbox tracked source and `Publish-current` were synced with verified parity after preserving 17 differing local files in a reversible pre-sync backup.

### 2026-08-14 — RED 2.0.21 — Gemini 3.7 Flash low/medium routing

- **Files changed:** `InspectionEditor.csproj`, `Services/GrokApiClient.cs`, new `tests/gemini_37_routing_static_test.py`, `tests/camera_crash_safety_static_test.py`, `docs/RED-2.0-RELEASE-NOTES.md`, and this handoff.
- **What changed:** RED hard-codes Gemini 3.7 Flash for Get 3, Transcribe, and fact-check primary requests. Fast and Transcribe calls send `thinkingLevel: low` with an 800-token response budget; careful and grounded fact-check calls send `thinkingLevel: medium` with a 1,600-token budget. Gemini 2.5 Flash remains the fallback and receives no 3.7 thinking-level field.
- **Basis:** A 20-call paired RED-style benchmark scored Gemini 3.7 Flash at 94.3 quality versus 92.3 for 3.6, with 2.185-second average and 3.310-second P90 latency versus 2.437 and 4.740 seconds. The extra estimated cost was about $0.05 per 1,000 calls.
- **Verification/deployment:** All 53 Python regression tests passed; Debug compilation succeeded with 0 errors and 16 existing warnings. Live Gemini 3.7 probes returned HTTP 200/STOP for low-thinking structured vision, medium-thinking structured vision, and medium-thinking grounded search. Release build, publish, GitHub release, hashes, updater URLs, and Dropbox parity are pending in this entry until deployment is complete.

### 2026-08-11 — RED 2.0.20 — STRADA equipment-match airflow targets

- **Files changed:** `InspectionEditor.csproj`, `MainWindow.xaml.cs`, `Services/EnergyComplianceService.cs`, new `Services/EquipmentAirflowService.cs`, new `tests/EquipmentAirflowHarness/`, new `tests/equipment_airflow_ui_static_test.py`, `docs/RED-2.0-RELEASE-NOTES.md`, and this handoff.
- **What changed:** RED recognizes five approved STRADA condenser + air-handler model pairs from Energy Final equipment fields, displays the exact airflow as read-only guidance while the equipment is entered, and supplies that target to the newest same-job AFI/ACCA report. Unit 1 and unit 2 remain separate, newest Energy Final attempts are authoritative, and the feature never changes Pass/Fail.
- **Verification/deployment:** The behavior harness and all 47 Python regression tests passed; the Release build and standalone win-x64 publish succeeded with 0 errors and the existing warnings. The generated AI provider stayed gitignored, its decoded credential returned HTTP 200 from `gemini-2.5-flash`, and package scans found no `settings.txt`, generated source, user license, inspection data, or plaintext credential. Release tag `v2.0.20` points to source commit `da94b27173dc5ad7be6df94bb321406538a28571`; GitHub latest assets, stable URLs, byte sizes, and downloaded SHA-256 hashes were verified (`Red-v2.0.20.zip`: `aa57b88d2074d0d4890af381f0822639014518e46183407c2c05009e3d73ee2d`; updater: `94724c7c5dea2777e27f856e6697818f04d74fe11e265ed3e65ac95c7578f49d`). Dropbox tracked source and `Publish-current` are synced with byte parity.

### 2026-08-07 — RED 2.0.20 — restore NI for FSF lookup fields

- **Files changed:** `InspectionEditor.csproj`, `MainWindow.xaml.cs`, `tests/framing_design_extraction_static_test.py`, `tests/camera_crash_safety_static_test.py`, `docs/RED-2.0-RELEASE-NOTES.md`, and this handoff.
- **Root cause:** FSF 3.1.b and 3.1.c are `LookupNaNi` controls, but Inspect stores only the normal lookup values in each item's `ValueList`. RED's collapsed row added NI as a separate button at the end of a horizontally clipped control strip, while the expanded inline drawer rendered only `ValueList` and therefore omitted NI entirely.
- **What changed:** RED now injects NI into the real option collection for every NaNi lookup. Short lists expose NI as a normal option in collapsed and expanded views, and long lists include NI inside the ComboBox data source.
- **Verification/deployment:** All 43 static regression tests passed. Release build and standalone self-contained win-x64 publish succeeded with 0 errors and 16 existing warnings. The private generated provider remained gitignored, its decoded credential returned HTTP 200 from `gemini-2.5-flash`, and package checks found neither `settings.txt` nor generated private source. GitHub deployment is pending.

### 2026-08-06 — RED 2.0.19 — continuous camera and crash diagnostics

- **Files changed:** `InspectionEditor.csproj`, `App.xaml.cs`, `Services/CameraService.cs`, new `Services/DiagnosticLogService.cs`, new `tests/camera_crash_safety_static_test.py`, `docs/RED-2.0-RELEASE-NOTES.md`, and this handoff.
- **What changed:** Windows Camera remains open and visible after each successful capture so inspectors can take multiple photos continuously. RED still adds every photo to the active checklist item on the UI thread but no longer minimizes Camera or pulls RED to the foreground after success. Camera Roll `Created`/`Renamed` callbacks now route through a full exception boundary, preventing non-I/O watcher failures from escaping `async void` and terminating the process. Durable diagnostics now write best-effort to `%LOCALAPPDATA%\RED\red_errors.log`; WPF dispatcher, unobserved task, AppDomain, and Camera watcher failures all use the same thread-safe logger.
- **Incident context:** Juan Cantu reported RED 2.0.15 closed twice during or immediately after photo capture in a first-trip Energy Star Final, losing unsaved progress. The unguarded watcher path existed unchanged through 2.0.18. The exact exception was not recoverable from Juan's machine, but a minimal .NET reproduction confirmed this callback shape can terminate a process when a non-I/O exception escapes.
- **Verification/deployment:** All 42 static regression tests passed. Release build and standalone self-contained win-x64 publish succeeded with 0 errors and 16 existing warnings. The compiled `Red.dll` contains the nonempty generated AI payload/mask, contains no plaintext key, and the decoded credential returned HTTP 200 from `gemini-2.5-flash`. GitHub deployment is pending.

### 2026-07-19 — RED 2.0.15 — framing-plan design extraction

- **Files changed:** `InspectionEditor.csproj`, `MainWindow.xaml.cs`, new `Services/FramingDesignParser.cs`, new `Services/FramingDesignService.cs`, new `tests/FramingDesignParserHarness/`, new `tests/framing_design_extraction_static_test.py`, `docs/RED-2.0-RELEASE-NOTES.md`, and this handoff.
- **What changed:** RED now loads the same revision-selected engineering PDF shown by the UI for SWI/TFF/TPC/TRDI/TRSI/COH/FS/FSF/ME/MP reports, classifies SW/FR/FJ pages, runs native extraction plus targeted Tesseract OCR and a wind-box crop retry, caches by file identity/parser version, and exposes source-backed framing values as optional teal appendable badges. Existing values suppress matching complete-token badges. NI is directly available on collapsed NaNi value rows; floor type NI and floor product/species NI are separate and gated on complete absence of second-floor/FJ evidence. Pass/Fail and Yes/No controls never receive framing values.
- **T-Ply rules:** Structural T-Ply is `3" edge / 6" middle` regardless of location; exterior non-structural T-Ply acting as the thermal-boundary air barrier is `6" / 6"`; interior non-structural T-Ply outside the thermal boundary is `6" / 12"`. Non-structural classification requires the usage terms in the same focused source clause, and ambiguous T-Ply is suppressed.
- **Verification/deployment:** Pure parser harness passes #2/#3 lumber, wind, unrelated-MPH suppression, one/two-story and FJ2/plain-second-floor gating, incomplete-extraction NI suppression, I-joist/open-web product handling, all three T-Ply classes, unrelated-term suppression, categorical replacement, append behavior, and used-badge suppression. Representative OCR text produced verified values across digital and scanned plan sets, while the two-page detail-only sample produced no value/NI badges. All 37 Python static tests pass and Debug/Release win-x64 builds succeed with 0 errors and 16 existing warnings. PRs `#5` and `#6` merged to main commit `e0fd0abfa8b9fedeeb7b9fda1da2bae3f99caeac`. GitHub release `v2.0.15` is published at `https://github.com/fullinspect-source/Red/releases/tag/v2.0.15`; the downloaded ZIP matches SHA-256 `936c5a53fb8943c167feb50edd6da0ea4f51c86933acdc8f88fa377a4943d4f8`. Dropbox source and `Publish-current` are synced and verified. The standalone `update_red.bat` eventually uploaded after GitHub's 502/503 outage; it was downloaded and verified at SHA-256 `94724c7c5dea2777e27f856e6697818f04d74fe11e265ed3e65ac95c7578f49d`, and the temporary retry cron was removed.

### 2026-07-17 — RED 2.0.14 — restore Transcribe/Get 3 release packaging

- **Files changed:** `InspectionEditor.csproj`, `MainWindow.xaml.cs`, `tests/ai_release_packaging_static_test.py`, `docs/RED-2.0-RELEASE-NOTES.md`, and `HOMER_DEV_HANDOFF.md`. The private `EmbeddedApiKeyProvider.Generated.cs` is required during Release builds but remains gitignored and is never committed or packaged as source.
- **Root cause:** RED 2.0.13 was published from a clean checkout without the gitignored generated AI-key provider. `_grokClient` therefore remained null, and the inline UI disabled Transcribe/Get 3; the disabled WPF styling made their text appear blank across checklist items.
- **What changed:** Release builds now fail immediately when the generated provider is absent. Inline and classic AI controls remain visible whenever a photo is present, and a missing client produces an explicit updater message instead of a silent return. The production package is built with the current private generated provider present.
- **Verification/deployment:** Merged through PR #4 as `5885554096e2273700b8bc5e3e26f8c9def619af`. The missing-provider negative test failed with the expected MSBuild error; the generated provider's Gemini credential returned HTTP 200 and exposed `gemini-2.5-flash`; the Release build and self-contained win-x64 publish succeeded with 0 errors and 16 existing warnings; all 25 static tests passed; and decompilation of the build, publish output, downloaded GitHub asset, and Dropbox `Publish-current` confirmed nonempty generated payload/mask without printing key material. GitHub release v2.0.14 is live at https://github.com/fullinspect-source/Red/releases/tag/v2.0.14. The 90,879,250-byte ZIP SHA-256 is `0162c24a8c103fd6a109a17546c6815d0fb41660068d102bbdcec4308250fad3`; the 8,437-byte CRLF updater SHA-256 is `94724c7c5dea2777e27f856e6697818f04d74fe11e265ed3e65ac95c7578f49d`. The latest-release API, both stable download URLs, downloaded ZIP hash/version, private-source exclusion, and Dropbox sync were verified.

### 2026-07-15 — RED 2.0.13 — correct compact revision labels

- **Files changed:** `InspectionEditor.csproj`, `MainWindow.xaml.cs`, `tests/revision_filename_static_test.py`, `docs/RED-2.0-RELEASE-NOTES.md`, and `HOMER_DEV_HANDOFF.md`.
- **What changed:** `GetRevisionFromDesignPath()` now recognizes compact STRAND filenames where the document suffix immediately follows the revision, including `2528605R3EC`, `2528605R3FFP`, and address-named files containing `2528605R3EL`. Previously the EC chooser correctly selected the highest revision but the separate badge parser missed the filename and displayed R0.
- **Verification/deployment:** Source merged through PR #3 as `db406d8c0cf40776a46ad9cee2c409b7040f4338`. Release build and self-contained win-x64 publish succeeded with 0 errors and the same 16 existing compiler warnings. All 21 static tests passed, including compact EC/FFP/EL filename regression cases; `git diff --check`, changed-file secret scans, and package-content checks passed. GitHub release v2.0.13 is live at https://github.com/fullinspect-source/Red/releases/tag/v2.0.13. The 90,878,633-byte ZIP SHA-256 is `a76f8d553a8046febb954e1a75b549ac03602a3d463bcb0f4c58634b8cd5b9f9`; the 8,437-byte CRLF updater SHA-256 is `94724c7c5dea2777e27f856e6697818f04d74fe11e265ed3e65ac95c7578f49d`. GitHub latest-release API, tag target, asset digests, and both stable latest-download URLs were verified.

### 2026-07-12 — RED 2.0.12 — field workflow and foundation-plan rescue

- **Files changed:** `InspectionEditor.csproj`, `MainWindow.xaml.cs`, `Services/EnergyComplianceService.cs`, `Services/SlabEngineeringService.cs`, `Services/UserDataService.cs`, `tests/codex2_rescue_static_test.py`, and `docs/RED-2.0-RELEASE-NOTES.md`.
- **What changed:** Completed the verified remainder of the Codex 2 rescue on top of the high-confidence work already merged in `7bde489`. CPP interior SD items 8.13/8.16/8.19/8.22 now use extracted slab thickness as a minimum-depth design check; the mapping was confirmed against hundreds of active CPP records where those item numbers are NumberPad prompts named `Interior Measurements (Location N): SD`. The embedded hold-down table now receives its own heading-anchored OCR crop before full-page fallback. Prefixed comments display as `[trade] - comment`, and the photo placeholder/File/Camera controls share a 96-pixel height.
- **Already completed before this release pass:** Actionable **Photo Required**, child-control row-toggle protection, status-only design-assist filtering, dual-label design matching, prefix-focus restoration, slab-depth field aliases, bounded/reordered strand-count patterns, and removal of the unsafe hardware-line final-number fallback were already merged to `main` through PR #1; they are included in this release without being duplicated.
- **Deliberately not added:** Broad automatic NI injection and the older NI-only measurement heuristic were not ported because current CPP measurement rows are already NumberPad controls and the broad rule could assign NI where the report does not permit it. Tooltip removal was also rejected as an accessibility regression.
- **Verification/deployment:** Source merged through PR #2 as `88b32955289a4214d5315ed3f0f5e6ba07821a44`. Release build and self-contained win-x64 publish succeeded with 0 errors and the same 16 existing compiler warnings; all 18 static tests passed; `git diff --check`, changed-file secret scans, and package-content checks passed. GitHub release v2.0.12 is live at https://github.com/fullinspect-source/Red/releases/tag/v2.0.12. The 90,730,547-byte ZIP SHA-256 is `1487c6933648c8a784cb3be0f7889b1021f5394a3d2fbb0dd12a3856be2c7b8e`; the 8,437-byte CRLF updater SHA-256 is `94724c7c5dea2777e27f856e6697818f04d74fe11e265ed3e65ac95c7578f49d`. GitHub latest-release API and both stable latest-download URLs resolve to v2.0.12.

### 2026-07-12 — Codex 2 high-confidence rescue (candidate for next RED release)

- **Computer/workstream:** Homer rescue branch `rescue/codex2-high-confidence`, merged into `main` after RED 2.0.11.
- **Commit/PR:** Squash commit `7bde48967bd7d5630ea690c53e7cade6bf7c5c84`, PR https://github.com/fullinspect-source/Red/pull/1.
- **Files changed:** `MainWindow.xaml.cs`, `Services/EnergyComplianceService.cs`, `Services/SlabEngineeringService.cs`, `Services/UserDataService.cs`, and `tests/codex2_rescue_static_test.py`.
- **Resulting behavior:** The inline **Photo Required** control now opens Camera for that item; clicks on embedded buttons, text boxes, combo boxes, and sliders no longer toggle the checklist row; Yes/No and Pass/Fail controls are excluded from plan-value design assist; extraction matching uses both nonblank `DisplayLabel` and `Name`; selecting a trade prefix restores focus to the multiline comment editor; slab-depth aliases use the same actual-at-least-design comparison as slab thickness; reordered strand-count OCR and complete-number boundaries are supported; and the unsafe fallback that could treat the `14` in `STHD-14` as quantity 14 was removed. Saved comments now use the shared formatter, prefix-only selection retains a ready-to-type trailing space, and legacy `[trade] - comment` text can be parsed without changing the persisted punctuation policy.
- **Verification:** `dotnet build -c Release -r win-x64` succeeded with 0 errors and 16 existing warnings. All 17 static tests passed, including 6 new rescue regression checks. `git diff --check` passed.
- **Pushed to GitHub:** Yes, merged into `main` through PR #1.
- **Included in a GitHub release:** No. Production remains RED 2.0.11.
- **Deferred intentionally:** The focused hold-down OCR crop was not transplanted without its original source patch; CPP 8.13/8.16/8.19/8.22 slab-depth mapping, NI-only measurement heuristics, broad required-field NI injection, `[trade] - comment` persistence changes, 96-pixel photo layout, and tooltip removal remain excluded pending separate review.

### 2026-07-12 — Local RED Codex workstream (uncommitted; based on RED 2.0.7)

- **Computer/workstream:** This Windows Codex computer; dirty checkout at `C:\Users\grace\Documents\RedGit`.
- **Commit:** None. These changes are uncommitted and are not present on GitHub. The checkout was based on commit `e4f13cf` while `origin/main` was `d862d32` at reconciliation time, so the work must be reconciled with the released 2.0.8–2.0.11 changes before it can be committed.
- **Files changed:** `InspectionEditor.csproj`, `InspectionPickerWindow.xaml`, `MainWindow.xaml.cs`, `Services/EnergyComplianceService.cs`, `Services/SlabEngineeringService.cs`, `Services/UserDataService.cs`, and `scripts/update_red.bat`.
- **Exact behavior represented by the local diff:**
  - `MainWindow.xaml.cs` shows an item's existing comment inline after the collapsed checklist prompt instead of displaying a generic `Comment` badge; turns `Photo Required` into a button that expands the item and opens the camera; keeps numeric/measurement rows editable when their only listed status choices are NI/N/A; adds NI to required lookup/value lists; prevents row-expansion drag handling from consuming clicks on buttons, text boxes, combo boxes, and sliders; gives the no-photo placeholder and File/Camera controls matching 96-pixel heights; focuses the inline comment editor after a trade prefix is chosen; and excludes Yes/No and Pass/Fail rows from plan-value design-assist targets even when their prompt contains measurement words.
  - `Services/EnergyComplianceService.cs` maps interior items 8.13, 8.16, 8.19, and 8.22 to slab thickness/depth, labels them `Slab D`, and evaluates actual slab depth as passing when it meets or exceeds the extracted design minimum. It also recognizes `SLABDEPTH`/`SLABDEPTHINCHES` field keys.
  - `Services/SlabEngineeringService.cs` adds a focused OCR crop anchored on the embedded-hardware table heading, uses the focused result to extract hold-down quantities, tightens strand-count patterns so a four-digit year cannot be truncated into a false count, and requires a quantity after a complete hardware model token so the `14` in `STHD-14`/`STAD-14` is not treated as the quantity.
  - `Services/UserDataService.cs` formats trade-prefixed comments as `[trade] - comment`, strips a legacy leading dash while parsing the core comment, and preserves a trailing space after a prefix-only selection so typing can continue immediately.
  - `InspectionEditor.csproj`, `InspectionPickerWindow.xaml`, and `scripts/update_red.bat` contain local 2.0.8 version-label changes made before the later 2.0.8–2.0.11 production commits. These version hunks are stale and must not replace current 2.0.11 metadata or updater behavior.
- **Verification performed:** The diff and repository state were inspected against fetched `origin/main`. A Release build was attempted once but did not start because the documented SDK path `C:\Users\grace\Documents\Codex\dotnet-sdk\dotnet.exe` no longer exists. `tests\red_ai_prompt_static_test.py` was attempted once with the bundled Python runtime but exited before assertions because `pathlib.read_text()` used Windows cp1252 and could not decode byte `0x8f` in `Services\GrokApiClient.cs`. No retry or source repair was performed during this handoff-only pass.
- **Pushed to GitHub:** No.
- **Included in a GitHub release:** No.
- **Follow-up:** Reconcile the functional hunks against `origin/main` in a clean branch or worktree; discard the stale local version-label hunks; resolve overlaps in `InspectionEditor.csproj`, `InspectionPickerWindow.xaml`, `MainWindow.xaml.cs`, and `scripts/update_red.bat`; then run a current .NET 8 build and the static tests with explicit UTF-8 handling before considering a commit or release.

#### Reconciliation against RED 2.0.11 `origin/main`

The dirty checkout was compared read-only with `d862d32`. Preserve it until the following candidates are reviewed and selectively ported; do not merge the checkout wholesale.

**High-confidence preservation candidates**

- `MainWindow.xaml.cs`, `InlinePhotoRequiredButton_Click()` and collapsed-header construction: replace the passive **Photo Required** badge with a button that expands the item and opens Camera. **Classification:** missing from current main. **Recommendation:** preserve.
- `MainWindow.xaml.cs`, `InlineItemRow_MouseLeftButtonUp()`: ignore row-toggle handling when the original click is inside a `ButtonBase`, `TextBoxBase`, `ComboBox`, or `Slider`. **Classification:** missing. **Recommendation:** preserve so child controls do not expand/collapse the row.
- `MainWindow.xaml.cs`, `GetInlineDesignAssist()`/`IsInlineStatusOnlyDesignTarget()`: exclude Yes/No and Pass/Fail controls from plan-value application. **Classification:** missing. **Recommendation:** preserve.
- `MainWindow.xaml.cs`, `GetInlineDesignAssist()`: build extraction prompt text from every nonblank, distinct `DisplayLabel` and `Name`; current main's `DisplayLabel ?? Name` fails when `DisplayLabel` is an empty string. **Classification:** missing. **Recommendation:** preserve.
- `MainWindow.xaml.cs`, `InlinePrefixSuffixButton_Click()`: after selecting a trade prefix and rebuilding the inline UI, focus the multiline comment box and move its caret to the end. **Classification:** missing. **Recommendation:** preserve.
- `Services/EnergyComplianceService.cs`, `GetSlabFieldBannerState()`: handle `SLABDEPTH` and `SLABDEPTHINCHES` with the same actual-at-least-design comparison already used for `SLABTHICKNESS`. **Classification:** field lookup/label aliases exist, but comparison aliases are missing. **Recommendation:** preserve the consistency fix.
- `Services/SlabEngineeringService.cs`, `ExtractViaOcr()` and local `OcrHolddownTable()`: add a focused OCR pass anchored to the embedded-hardware table so model and quantity remain on one row. **Classification:** missing. **Recommendation:** preserve for a dedicated source review.
- `Services/SlabEngineeringService.cs`, `ExtractStrandCountFromCrop()`: support OCR order `TOTAL NUMBER <count> ... OF STRANDS` and use complete-number boundaries so a year such as 2024 cannot be truncated into a false count. **Classification:** partly missing. **Recommendation:** preserve.
- `Services/SlabEngineeringService.cs`, `ExtractHolddownCount()`: require a complete hardware model followed by a separate quantity and remove the fallback that treats the final number on a hardware line as quantity; this prevents the `14` in `STHD-14`/`STAD-14` from becoming Qty 14. **Classification:** missing. **Recommendation:** preserve.
- `Services/UserDataService.cs`, `SaveComment()`/`BuildComment()`: route saved prefixed-comment construction through `BuildComment()` instead of duplicating formatting, and preserve the trailing space after a prefix-only selection so the inspector can continue typing. **Classification:** missing. **Recommendation:** preserve the shared formatter and ready-to-type spacing, while deciding persisted punctuation separately.

**Manual review before preservation**

- `MainWindow.xaml.cs`, `CreateInlineStatusHeaderControl()` plus local `HasOnlyInlineNiOptions()`/`IsInlineMeasurementOrQuantityItem()`: allow free entry for measurement/quantity rows whose supplied choices are only NI/N/A. **Classification:** missing but heuristic-sensitive and potentially overlapping newer Numberpad defaults. **Recommendation:** manual template/UI review.
- `Services/EnergyComplianceService.cs`, `SlabItemMappings`, `SlabLabels`, and `GetSlabItemBannerState()`: interpret CPP items 8.13/8.16/8.19/8.22 as slab depth. **Classification:** unclear; current main explicitly treats them as non-derivable TOF-to-TOG. **Recommendation:** confirm against the live CPP template before porting.
- `Services/UserDataService.cs`, `BuildComment()`/`StripPrefixAndSuffix()`: persist comments as `[trade] - comment` and strip the separator during parsing. **Classification:** missing but changes stored display format. **Recommendation:** compatibility/product review.
- `MainWindow.xaml.cs`, `CreateInlinePhotosDrawer()`: make the empty-photo placeholder and File/Camera controls 96 pixels tall. **Classification:** missing, cosmetic. **Recommendation:** manual layout review.

**Discard or keep the newer main implementation**

- `InspectionEditor.csproj`, `InspectionPickerWindow.xaml`, and `scripts/update_red.bat`: local 2.0.8 labels are obsolete because production is 2.0.11. **Classification:** stale version-only changes. **Recommendation:** discard these hunks.
- `MainWindow.xaml.cs`, local `AddRequiredNiOption()` and classic required-lookup NI injection: automatically adding NI to every required value list is broader than current control semantics. **Classification:** unsafe/unclear. **Recommendation:** discard the broad rule; design a narrower policy if needed.
- `MainWindow.xaml.cs`, quick/saved-comment button construction: local removal of comment tooltips may reduce accessibility. **Classification:** unsafe/unclear. **Recommendation:** discard.
- `MainWindow.xaml.cs`, older collapsed-row comment rendering: current main already renders comments as dedicated `middleContent` and integrates the compact Numberpad slider. **Classification:** superseded. **Recommendation:** keep current main; evaluate removal of the remaining generic Comment badge separately if desired.

### 2026-07-12 — Codex 1 shared handoff baseline — `d862d324c5a494b135296dc63a3f53dd86fc3c79`

- **Files changed:** New `HOMER_DEV_HANDOFF.md` only.
- **Prior behavior:** RED had no maintained cross-assistant development handoff. Current production state, recent Codex 1 releases, source-of-truth files, security rules, verification commands, and known debt were distributed across code, stale documentation, Git history, and GitHub Releases.
- **Resulting behavior:** Added the shared handoff covering RED 2.0.8–2.0.11, version/release sources, updater architecture, Numberpad defaults and collapsed sliders, build/release commands, security-sensitive packaging rules, warnings, technical debt, and assumptions older RED documentation must not reintroduce.
- **Verification performed:** Verified all cited implementation/test files and commit hashes; checked the document with `git diff --check`; scanned it for common API-key, GitHub-token, xAI-key, and private-key patterns; confirmed the live v2.0.11 GitHub release and asset; confirmed the pushed `main` branch at this commit.
- **Committed:** Yes.
- **Pushed:** Yes, to `main`.
- **Included in a GitHub release:** No. Documentation-only commit after the v2.0.11 release.
- **Unresolved risks/follow-up:** The handoff must be updated with every meaningful RED change. Several older repository documents remain stale and should not override current code or this handoff.

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
- **Verification:** Before implementation, Codex 1 exhaustively scanned the available Dropbox inspection archive/review INS corpus across all discovered report types, grouped numeric-only prompt patterns, and reviewed observed ranges plus camera/comment usage. Release build and the static Numberpad/AI tests passed. The generated audit CSV/JSON files were local analysis artifacts and were not committed because they were derived from private inspection data.
- **Deployed to GitHub:** Yes. https://github.com/fullinspect-source/Red/releases/tag/v2.0.9

### 2026-07-12 — RED 2.0.8 — `7161e0bbc36d9334bc87927c2fb7132a458a1c9c`

- **Files changed:** `AppIdentity.cs`, `InspectionEditor.csproj`, `InspectionPickerWindow.xaml`, `MainWindow.xaml.cs`, `scripts/publish-release.sh` (present in that commit, no longer present on current `main`), and `scripts/update_red.bat`.
- **What changed:** Added checklist/section progress coloring and completion-state UI, and centralized runtime version/date display through assembly metadata and `AppIdentity`.
- **Verification:** Release build/publish was performed for the production release.
- **Deployed to GitHub:** Yes. https://github.com/fullinspect-source/Red/releases/tag/v2.0.8

## Current Production State

- **Production version:** 2.1.23.
- **Release date in the application:** 2026-09-01.
- **Latest GitHub production release:** v2.1.23, non-draft and non-prerelease.
- **Release URL:** https://github.com/fullinspect-source/Red/releases/tag/v2.1.23
- **Release asset:** `Red-v2.1.23.zip` (93,804,538 bytes), plus `update_red.bat` (8,437 bytes).
- **Production executable/install:** `Red.exe` in `C:\Red`.
- **Production user data:** `%LOCALAPPDATA%\RED` via `AppIdentity.LocalAppDataPath`.
- **Runtime:** .NET 8 WPF, target `net8.0-windows10.0.19041.0`, self-contained win-x64 production publish.

## Current Sources of Truth

Use code and live GitHub state before older prose documentation:

| Concern | Authoritative source |
|---|---|
| Application version | `<Version>` in `InspectionEditor.csproj` (currently 2.1.23). Do not hard-code a second version elsewhere. |
| Release date | `<ReleaseDate>` in `InspectionEditor.csproj`; emitted as assembly metadata (currently 2026-09-01). |
| Runtime identity/UI display | `AppIdentity.cs`: `Version`, `VersionDisplay`, `ReleaseDate`, `PublishedDateText`, `VersionWithPublishedDate`, window titles, and app-data folder selection. |
| Startup app update | `App.xaml.cs`, `OnStartup()` calling `AppUpdateService.CheckAndInstallIfAvailableAsync()`. |
| Shared updater | `Services/AppUpdateService.cs`: `AppUpdateResult`, `CheckAndInstallIfAvailableAsync`, version comparison, GitHub release parsing, download/extract, and elevated batch handoff. |
| Opening/About triple-click | `InspectionPickerWindow.xaml.cs`: `AboutLogo_MouseLeftButtonDown()` and `ForceAboutUpdateAsync()`; force mode bypasses the daily marker. |
| Editor update fallback | `MainWindow.xaml.cs`: `ShouldRunStartupAppUpdateCheck()`, `WelcomeLogo_MouseLeftButtonDown()`, and `CheckForUpdatesAsync()`. This is older duplicated logic and is not the primary startup implementation. |
| Numberpad product defaults | `Services/NumberpadDefaultService.cs`, especially `Get()`, `GetPrePourProfile()`, and `MigrationVersion`. |
| Numberpad integration/UI | `MainWindow.xaml.cs`: `ApplyNumberpadDefaultMigration()`, `CreateCollapsedInlineNumberpadSlider()`, `CreateInlineNumberpadTouchSlider()`, `SynchronizeInlineNumberpadSliders()`, and drawer preference helpers. |
| Build project/package inputs | `InspectionEditor.csproj`; `build-standalone.bat` is a convenience wrapper. |
| Manual field updater | `scripts/update_red.bat`; it backs up existing installation/user state, downloads GitHub's latest ZIP, installs to `C:\Red`, restores allowed local files, repairs the shortcut, and relaunches. |
| Release history | Git commits/tags plus GitHub Releases. `docs/RED-2.0-RELEASE-NOTES.md` includes the 2.1.22 complete checklist drawer and AI OCR isolation hardening. |

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

- A separate checkout on this computer (`C:\Users\grace\Documents\RedGit`) contains the seven uncommitted files described in the newest **Latest Changes** entry. The dirty checkout remains based on `e4f13cf` and must be compared against current `origin/main` before any future rescue work; do not commit or publish it as-is, and do not treat its 2.0.8 version strings as authoritative.

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
