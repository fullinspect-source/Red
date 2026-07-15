# RED 2.0 Release Notes

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
