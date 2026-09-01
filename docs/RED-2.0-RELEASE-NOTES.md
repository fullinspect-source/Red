# RED 2.0 Release Notes

## 2.1.22 — Complete single-tap and OCR isolation hardening

- Fail/status badges and value-choice affordances now select/load their row without opening inline tools.
- Drawer close state is cleared before the close animation, preventing delayed callbacks or checklist rebuilds from leaving a drawer logically stuck open.
- OCR option sets are stored per checklist item, so tapping an older result cannot consume a different item's latest transcription.
- OCR chooses the option with the most parsed labeled pairs rather than merely counting slash characters.
- Pair matching searches both `DisplayLabel` and `Name`, so a short display label cannot hide a U-Factor, SHGC, model, or serial match from the full prompt.

## 2.1.21 — Checklist drawer and AI OCR workflow fixes

- The Photo Required badge now selects the checklist row, loads its fixed right-side editor, and launches the camera without opening the inline tool drawer.
- Checklist rows select on the first press without replacing the tapped row, so double-click reliably opens or collapses that row's drawer.
- Selecting any different checklist item automatically collapses the previously open inline drawer.
- Tapping an AI OCR result now updates the right-side value editor and immediately rebuilds the left checklist value/status surface.
- Paired OCR values now put U-Factor on the matching row and SHGC on the next matching checklist row when present, even when filters or collapsed sections hide that row.
- OCR pair parsing now accepts slash, pipe, semicolon, newline, labeled comma, and omitted-separator forms such as `U-Factor: 0.30 SHGC: 0.25`.

## 2.1.20 — Restore the preferred 24-hour update interval

- Restores RED's once-per-24-hours automatic GitHub update check at Trent's direction.
- Keeps triple-click as an immediate manual check that bypasses the daily marker.
- Retains nonblocking behavior when the internet is unavailable or slow.

## 2.1.19 — Check for app updates on every startup

- Removes the old 24-hour app-update throttle that could suppress a newly published RED release when the app had already checked earlier that day.
- Checks GitHub's latest RED release every time RED opens, while retaining the triple-click updater as a manual retry path.
- Keeps update failures nonblocking so offline or slow connections still allow RED to open normally.

## 2.1.18 — Plan Check safety hardening

- Prevents a slower, previously selected PDF from replacing the currently selected Plan Check attachment during rapid attachment changes.
- Uses narrower plan phrases for automatic marker suggestions, records the attachment actually reviewed, and adds NI / not applicable as a valid completed review state.
- Makes plan-marker taps select and reposition checks without accidentally changing their confirmed/deficient conclusion.
- Limits attachment persistence to inspections with a newly completed Plan Check result.

## 2.1.17 — CPP Plan Check Beta

- Adds a CPP-only **Plan Check Beta** that opens valid PDF attachments embedded in the INS file and keeps the original plan unchanged.
- Requires the inspector to confirm or flag five mandatory checks: steel/rebar callouts, beam design, slab thickness, hold-downs/straps, and cable counts.
- Lets the inspector reposition every suggested marker on the plan, creates marked deficiency crops for the selected checklist item, and can optionally add a flattened annotated PDF back to the INS file.
- Saves completion metadata even when optional exports are declined, while preserving existing attachment JSON and keeping all RED 2.1 split-pane, AI, OCR, camera, and updater behavior.

## 2.0.21 — Gemini 3.7 Flash upgrade

- Upgrades **Get 3**, photo transcription, and fact-checking to Gemini 3.7 Flash after a 20-call paired RED benchmark showed better quality and faster average/P90 responses than Gemini 3.6 Flash.
- Hard-codes low thinking for fast field requests and medium thinking with a larger output budget for careful requests so Gemini 3.7 does not waste the response budget or truncate useful output.
- Keeps Gemini 2.5 Flash as the explicit fallback and omits 3.7-only thinking settings when fallback is used.

## 2.0.20 — STRADA airflow targets and FSF NI restoration

- Recognizes the approved STRADA condenser and air-handler model matchups entered in Energy Final reports and shows the exact HVAC design airflow as read-only guidance.
- Uses the newest same-job Energy Final equipment matchup when an AFI/ACCA report is opened, while keeping unit 1 and unit 2 targets separate.
- Keeps the normal EC/tonnage airflow as a fallback when no approved matchup exists, never mixes equipment across report attempts, and never changes Pass/Fail automatically.

- Restores NI as a selectable value for `LookupNaNi` checklist items in both collapsed rows and expanded value drawers.
- Fixes FSF 3.1.b **Floor Type** and 3.1.c **Manufacturer or Size/Grade/Species**, whose stored lookup lists omit NI even though the report fields explicitly permit it.
- Keeps NI available inside long lookup dropdowns as well as short button lists so it cannot be hidden beyond a horizontal scroller.

## 2.0.19 — Continuous camera and crash diagnostics

- Keeps Windows Camera open, visible, and focused after a successful capture so inspectors can take several photos without reopening Camera after each one.
- Continues adding each captured photo to the active RED checklist item in the background.
- Contains all Camera Roll watcher exceptions so a file-access or watcher failure cannot escape the background callback and terminate RED.
- Writes durable, best-effort error diagnostics to `%LOCALAPPDATA%\RED\red_errors.log`, including WPF, background-task, AppDomain, and Camera Roll failures.

## 2.0.15 — Framing-plan design extraction

- Extracts focused project values from the selected framing engineering PDF for SWI, TFF, TPC, TRDI, TRSI, COH, FS, FSF, ME, and MP reports.
- Uses native PDF text, targeted SW/FR/FJ OCR, a focused wind-design crop retry, sanity checks, source-sheet provenance, and a persistent source-revision cache.
- Presents extracted wind, wall, plate/sill, rafter, ceiling-joist, floor-system, roof/floor sheathing, and structural-sheathing values as optional teal checklist badges. RED never changes Pass/Fail from the extraction.
- Supports multiple values per report field: tapping a badge appends it, and values already present in a loaded report no longer appear as unused badges.
- Keeps NI directly available on collapsed NaNi value rows. One-story plan sets can suggest NI for floor type/product only after the complete plan set lacks second-floor/FJ evidence; I-joist and open-web species/grade remain NI when unstated.
- Applies the confirmed T-Ply classifications: structural `3" edge / 6" middle`, exterior non-structural air-barrier `6" / 6"`, and interior non-structural outside the thermal boundary `6" / 12"`. Ambiguous T-Ply use produces no badge.

## 2.0.14 — Restore Transcribe and Get 3

- Restores the embedded Gemini client in the production package so **Transcribe** and **Get 3** work again.
- Keeps AI buttons visible and readable if a future installation is missing AI configuration, and gives a direct update message instead of silently showing blank controls.
- Adds a Release-build guard that refuses to build when the gitignored generated key provider is absent, preventing another AI-disabled package.

## 2.0.13 — Correct design-extraction revision labels

- Correctly recognizes compact STRAND revision filenames such as `2528605R3EC.pdf`, `2528605R3FFP.pdf`, and address-named files containing `2528605R3EL`.
- Shows the actual selected design revision in the **Design Extraction** badge instead of falling back to R0 when the revision number is immediately followed by a document-type suffix.
- Leaves source selection unchanged: EC extraction continues choosing the highest available EC revision.

## 2.0.12 — Field workflow and foundation-plan rescue

- Makes **Photo Required** actionable: selecting it opens Camera for that checklist item.
- Prevents embedded buttons, text boxes, combo boxes, and sliders from accidentally toggling checklist-row expansion.
- Excludes Yes/No and Pass/Fail controls from plan-value design assist and uses both prompt labels when matching extracted design values.
- Restores comment-box focus after selecting a trade prefix and standardizes displayed prefixed comments as `[trade] - comment`.
- Gives the empty-photo area and File/Camera controls matching 96-pixel heights.
- Maps CPP interior `SD` items 8.13, 8.16, 8.19, and 8.22 to extracted slab thickness and evaluates them as minimum-depth checks. This mapping was verified against the active CPP inspection corpus.
- Adds focused OCR around the embedded hold-down table and prevents hardware model numbers such as the `14` in `STHD-14` from being interpreted as quantities.
- Improves reordered strand-count OCR and prevents four-digit years from being truncated into false counts.
- Retains the existing Numberpad behavior for current CPP measurement prompts; the active templates already expose those rows as NumberPad controls, so no broad NI injection was added.

## 2.0.10 — Image processing security maintenance

- Upgrades SixLabors.ImageSharp from 3.1.5 to 3.1.12.
- Resolves the known crafted-GIF crash and infinite-loop advisories reported against the older decoder.
- Retains the existing RED photo import, resize, enhancement, rotation, markup, and JPEG re-encoding behavior.

## 2.0.9 — Numberpad defaults and quick row sliders

- Adds archive-informed Numberpad ranges and increments for recurring numeric prompts across AFI, CPP, CPR, HEF, HER, HET, IEF, IER, and SRP reports.
- Applies the new N and N+Camera tool defaults once for all users, then remembers later user customization normally.
- Hides Comments on default numeric workflows and opens Photos alongside Numberpad where the normal workflow requires evidence.
- Adds a compact Numberpad slider to the checklist row when no comment preview is present.
- Keeps compact and full Numberpad sliders synchronized while either is being moved.
- Leaves an empty row unmodified until the user intentionally touches its parked slider handle.
- Adds explicit whole-number, quarter-step, and larger prompt-specific slider increments.
- Repairs the My List refresh handler and About-version binding so clean builds succeed.

RED 2.0 is the next major RED update for field inspectors. It is focused on faster tablet work, cleaner navigation, and fewer taps while keeping the classic RED workflow available during the transition.

## Highlights

- My List redesigned with bigger touch-friendly rows.
- My List remembers window placement, column sizing, hidden/sortable columns, grouping, and search visibility.
- Group My List by builder, subdivision, inspection type, or no grouping.
- New inspection editor layout with a full-width running checklist instead of the old left checklist/right dashboard split.
- Only one checklist item expands at a time.
- Swipe/tap item tools: swipe or tap right to open item tools, swipe left to close.
- Better touch scrolling across the editor.
- Inline status/value controls are right-justified for easier right-handed tablet use.
- Section/category headers can collapse temporarily and include bulk NI/Pass buttons.
- Comments drawer now includes prefixes, suffixes, quick comments, saved comments, clear/trash, and the red-flag escalation marker.
- Photos, camera, and file tools are built into inline item tools.
- AI tools carried forward from classic RED: Get 3, transcription, tone options, and fact-checking.
- EC report and foundation/slab PDF design extraction can surface values beside checklist items where applicable.
- Extracted design values can be clicked into report fields.
- Experimental numberpad tool includes a touch-friendly keypad and slider.
- User preferences for tool drawers, font size, window placement, and other UI settings are remembered.
- Classic UI remains available inside the editor as a fallback during transition.

## Deployment notes

- Version: `2.0.0`
- Main executable: `Red.exe`
- Install folder: `C:\Red`
- User data folder: `%LOCALAPPDATA%\RED`
- Release type: standalone/self-contained win-x64 build.
- Installer/updater BAT: `update_red.bat`

## Safety / migration

The updater backs up existing RED files before install, including:

- `C:\Red`
- `%LOCALAPPDATA%\RED`
- `%LOCALAPPDATA%\InspectionEditor`
- `%LOCALAPPDATA%\RED-2.0-Dev`

Backup/log location on each machine:

`%LOCALAPPDATA%\RED_Backups\before-red2-YYYYMMDD-HHMMSS\`

The updater is designed to preserve:

- RED license file
- RED settings/preferences
- saved comments
- custom prefixes/suffixes
- user templates/data under RED userdata folders
- Dropbox inspection data

## Manual test required before broad rollout

Because RED is a Windows WPF app and the updater is a Windows BAT, final launch/install proof must be completed on a Windows machine before sending to all users.
