# RED 2.0 Release Notes

## 2.3.7 — Pinch to zoom in RED's camera

- **Pinch to zoom:** in RED's camera screen, spread two fingers on the live picture to zoom in, pinch them together to
  zoom out. A mouse wheel or a touchpad pinch zooms too.
- **The photo shows what the screen showed:** when you press the shutter while zoomed in, the photo is the zoomed part,
  not the whole wide picture.
- **A small "2.0×" label** at the top shows the zoom. Tap it, or double-tap the picture, to go back to normal.
- The zoom stays when the tablet sleeps and wakes or the camera restarts by itself. It starts at normal (1×) each time
  you open the camera or switch cameras.

## 2.3.6 — Submit works like INSPECT: no extra questions, no Dropbox warning

- **No "first live Submit / is the owner present?" questions** before or after Submit (or Sync).
- **No Dropbox "remove from everyone's Dropbox?" warning.** RED no longer moves files out of the Dropbox folder: its own
  earlier versions of a submitted report are simply deleted (the report itself is safe in Outbox and Archive).
- **A successful Submit just closes the report**, with no "Sent to Outbox" box. A real problem still shows.
- RED's own local backup folder on the tablet is cleaned automatically: anything older than 30 days is deleted.

## 2.3.5 — Second round of tablet fixes: a bigger Add window, cleaner toolbar and Settings, plans that print your marks

- **Add is bigger and simpler.** Large text and buttons throughout. You always pick the address from the search (no
  "Skip this"), and it stays in view on the next pages. The inspection list shows the date only, newest first. The
  extra-information page keeps only Re-inspection / Attempt and Purchase Order. The last page says **Finished** and
  **Begin editing**.
- **Report editor:** no Open INS and no History buttons. Greyed-out buttons stay readable. A+ / A− now resize both
  sides of the screen.
- **Status only offers what this inspection allows** (the same choices as the result button). The signature box keeps
  your whole stroke instead of drawing dashes.
- **Scrolling by dragging:** press and drag scrolls the print preview, the End section and the Add lists on tablets
  where a swipe did nothing.
- **Plans you mark up print correctly:** the plan opens in your default PDF program; only the pages you changed are
  attached and printed, with your ink on them. The PDF program shows the plan's own name.
- **My List tabs are grouped:** My List, Outbox | Archive, Review | ORDERS, BUYER WALK | Settings, About | Close App.
- **Settings:** Import from INSPECT is at the top. Archive retention can be changed here and changes INSPECT's setting
  too. The Check buttons, Show Review and the iPad fee token are gone. Offline data downloads quietly every time RED
  starts.
- **ORDERS:** Assign is replaced by **Unassign**; adding an order far from today asks first, and adding one offers the
  other open orders at the same address that day.

## 2.3.4 — Fixes from the tablet test: Add, Sign and Submit, swipe to scroll, marked-up plans

- **Add on My List works on a fresh tablet.** When the offline lists are missing or old, Add now downloads them itself
  ("Receiving offline work requirements from Strand, please wait..." / "Stay Connected To WiFi") and then opens. RED
  also refreshes them at start and when you sign in. The "Please connect to WiFi..." message now shows only when there
  is no connection or no saved login.
- **Sign and Submit are easy to find.** A red **✍ Sign** button next to the result button takes you straight to the
  signature box (it turns green when signed). **Submit** sits right after Save, and when it is greyed out a short note
  says why ("Submit: only from My List", "Submit: save first"). The toolbar wraps onto a second line instead of
  hiding buttons.
- **Swipe to scroll.** The print preview scrolls with your finger, and a finger stroke on the signature box draws
  instead of scrolling the page.
- **Marked-up plans attach to the report, like INSPECT.** The plan button (Slab Eng, FD Plan and the others) and the
  ◀ Trip button now open a copy. Pages you mark up and save are attached to the report and only those pages print;
  a copy you did not mark up is not sent. The shared plan in Dropbox is never changed.
- **Housekeeping runs by itself.** Archived reports are cleaned up at each start, keeping one day more than INSPECT's
  setting (with 5 days: 5- and 6-day-old reports stay, 7 days and older go). Job folders of plans for reports no
  longer in My List, Outbox or Review are removed, as INSPECT does.
- **Photos no longer carry location, camera or time details,** as in INSPECT. They stay the right way up.
- **No Approve.** Approve is gone, Review reports show no Submit, and RED never submits, moves or changes Review files
  by itself.

## 2.3.3 — The report comes back when you close the camera; tidy backup photos

- **Closing the camera brings your report back to the front.** Before, the report could stay hidden behind RED's list
  window after you pressed Close, so you had to find it to see the new photos.
- **Backup photos have their own folder and clean themselves up.** RED's camera still keeps a backup copy of each photo,
  now in **Camera Roll\red-camera**. Copies older than twice your Archive setting are deleted automatically (for
  example, with Archive set to 8 days, backups are kept for 16 days). Only RED's own backup photos in that folder are
  ever deleted.

## 2.3.2 — A camera built into RED that keeps working after the tablet sleeps

- **RED has its own camera.** Press Camera on an item and RED's camera opens on the right of the screen, with a big
  round shutter that is always there. Photos go straight into the item, at the same size and brightness as before.
  Keep the camera open and take as many photos as you need; it follows the item you select.
- **It keeps working after sleep.** RED lets go of the camera when the tablet sleeps, the screen turns off, the PC
  locks or RED is minimized, and starts it fresh when you are back. If the picture freezes or goes black, RED shows
  "Restarting camera…" and fixes it by itself.
- **If the camera really is stuck,** RED says so plainly and offers Try again, Use Windows Camera app, and Reset camera
  (Windows asks for administrator permission first). Restarting the tablet always works too.
- **Each photo is also kept in your Camera Roll,** as the Windows Camera app does.
- **Switch camera** appears when the tablet has a front and a back camera, and RED remembers your choice.
- **Settings → Camera** can switch a device back to the Windows Camera app if RED's camera does not work there.
- RED now finds the Camera Roll folder exactly as INSPECT does (this matters only on PCs with more than one Camera Roll
  folder).

## 2.3.1 — My List tabs, a result button, and no "please confirm" popups

- **My List is quicker to use:**
  - The views are tabs on the right with their counts: My List, ORDERS, BUYER WALK, Outbox, Archive and Review.
    Review shows only for reviewers. Settings and About moved to the bottom.
  - The big "My List" heading is gone, so the list has more room. Add is green. "Reset size" resets the text size;
    Refresh reloads the list.
  - Each row has a tick box. Remove, Move to My List and Approve act on the ticked rows. Move to Archive and Move to
    Review are gone.
- **Result button in the editor.** The top of the checklist shows the report's result: NO RESULT, PASSED,
  FAIL (PO required) and so on. Tap it to set or change the result.
- **No more "please confirm" popups.** When RED fills in or changes values for you, it applies them and flashes the
  changed fields red 5 times. A changed field you can't see is named for a few seconds at the top of the checklist.
  Deleting, overwriting, Submit and Approve still ask first.
  - A window-rating sticker now fills both window fields (operable and fixed) when the other one is blank.
- **Forms with the same name**, for example two "HERS Energy Rough", show their form number in ORDERS and in Add.

## 2.3.0 — Submit, Approve, Buyer Walk and the rest of INSPECT's report workflow

- **Submit and Approve work as they do in INSPECT:**
  - Submit checks the report first and lists anything missing. "Go to" takes you to each problem.
  - A finished report goes to the Outbox and the Archive.
  - Review reports open with Approve instead of Submit.
  - The first live Submit and the first live Approve on each PC ask you to confirm before anything is sent.
- **Outbox, Archive and Review views.** My List has a view for each of these folders, showing each report's status and whether Strand has processed it.
  - Reports in these views open read-only. You can open items to see their photos and comments, but nothing can be changed.
  - "Move to My List" brings a report back.
- **Housekeeping waits for a supervised first run.**
  - Settings shows the Archive cleanup and job-folder purge switches, and a dry run that lists what would be removed.
  - RED does not yet clean the Archive, purge job folders or prune the Dropbox log; until the supervised first run it only notes that it is waiting.
  - "Remove from device" closes the report and deletes only its file.
- **More of the checklist works like INSPECT:**
  - Item-group grids (truck times, drainage, CPP slabs) are edited in place, with the number pad.
  - Ink items (customer signature, sketches and placement diagrams) have stamps, colours, undo and redo.
  - The Top and End sections show the status, next action, notes and markers.
  - The inspector signature can be drawn, or taken from your saved signature.
  - Concrete pour (CPR), BWT / New Home Orientation, Special Consult (SCI) and narrative forms follow INSPECT's rules.
- **Job files and PDFs.**
  - A Job Files browser shows the job's documents. You can add them as reference attachments.
  - PDFs open in the PDF app chosen in INSPECT's settings.
  - RED saves the pages you annotated, as INSPECT does.
- **Adding work offline.** RED keeps forms, templates and jobs on this PC. The Add Service wizard, including reinspections, works from them.
- **Report preview, print and PDF.** Preview a report, print it, save it as a PDF or start an e-mail.
- **Photos.** Import several photos at once, sized as INSPECT sizes them. Mark them up with drawings and labels.
- **Comment libraries.** The comment drawer shows the Company and My INSPECT comment libraries.
- **Buyer Walk tab.** A BUYER WALK tab after ORDERS lists your buyer walks from Strand. Open one to schedule it, save it or add it to My List.
- **History.** Each report's History lists its events in INSPECT's words.
- **Also:**
  - The ORDERS "Is like" filter now shows both wildcards, "%" and "_".
  - RED's error log is now one file per day in RED's Logs folder, kept for 30 days.
  - Test copies of RED keep their activity log on the PC, so they never add lines to the team's log.

## 2.2.3 — Every checklist item edits like INSPECT and saves INSPECT's values

- **Answer choices match INSPECT:**
  - Pass/Fail items offer Pass, Fail and NI, with no N/A.
  - Yes/No items offer Yes and No.
  - Items INSPECT shows as Pass/Fail/NI do the same in RED, even when their type says "Yes/No".
  - Walk checklists offer Discussed and NA.

  Clicking the chosen answer again clears it. An older answer INSPECT doesn't offer (such as N/A) is shown as it was saved, and kept until you pick another.
- **"NA" is shown as NI**, as INSPECT does, and saved as NI the next time you change something in that report. Opening and closing a report without edits still writes nothing. Walk checklists keep NA.
- **New editors for items that used to be plain text boxes:**
  - a time picker that saves "5:00 AM", and a date picker that saves "3/3/2026", in INSPECT's format on any Windows region setting;
  - Home Readiness 1 to 10;
  - Upload Picture, Upload File, and the form and info buttons.

  Customer and sales-agent phone numbers are formatted like INSPECT's: (555) 010-0100.
- **The superintendent can be picked from the list, or added with "Add New".**
- **The number pad works like INSPECT's:**
  - ¼, ½ and ¾ are added to the whole number;
  - no digits can follow a fraction;
  - values above 99999 are refused with INSPECT's "Value Too Large" message.

  If INSPECT's number pad is turned off, RED shows a number box instead.
- **Memo answers** (Special Consult narratives and similar) are typed in the Comment box, up to 1000 characters, and recorded the way INSPECT records them.
- **No checklist item is hidden any more** because of its picture or comment button settings.
- **On a reinspection,** choosing Pass or Discussed on an item with comments or pictures asks INSPECT's "Item Fixed Confirmation".
- **Section "All" buttons** (YES/NO, DISCUSSED/NA) appear on checklist sections that have no Pass/NI buttons.
- **Copies:**
  - Remove is offered on any item that has a copy in its section.
  - The concrete-pour truck log cannot be copied, and shows its times and numbers as text.
- Templates, the energy-report fill and AI transcription now enter only values the item can hold.
- Items of a kind INSPECT cannot show stay visible and editable, marked "Unsupported control".

## 2.2.2 — Inspector profile: INSPECT's Archive retention, and Add to My List asks only for the essentials

- **Settings shows INSPECT's Archive retention.** Settings > Inspector Profile shows "Archive retention: N days (from INSPECT's settings)" and the date on or before which archived inspections are removed. The value is read from INSPECT's settings each time and never copied into RED. If INSPECT's value is 0 days or less, Settings says RED will not clean the Archive until it is 1 or more. RED does not delete anything from the Archive yet; INSPECT still does that cleanup.
- **Add to My List checks only the essentials.** It now needs only your Strand username, your full name (first and last) and your INSPECT user type. If one is missing, it says "Complete your inspector profile in RED Settings first." and opens Settings. A missing saved password or default state no longer blocks it. RED's start-up check is unchanged.
- **RED creates folders only when it saves into them.** My List is created when the first inspection is saved there. RED never creates INSPECT's Temp, Documents, Data or Jobs folders.
- INSPECT's own settings files are only read, never written. This was checked again against the real files.

## 2.2.1 — Saving that keeps INSPECT's files as INSPECT wrote them

- **Saving changes only what you edited.** Everything else in the .ins file stays byte for byte as INSPECT wrote it. RED no longer adds its own item or header keys (the one RED-only addition left is the Plan Check result on CPP reports), keeps empty comments as they were, and leaves unchanged pictures untouched. Opening and closing a report without edits writes nothing.
- **Copied items and result order follow INSPECT.** Duplicated items are made the way INSPECT makes them, and the result order is recomputed with INSPECT's rule when you save.
- **A report changed elsewhere is never overwritten silently.** If the file changed on disk after RED opened it (in INSPECT or by Dropbox), Save stops and offers:
  - **Reload**, to show the newer file;
  - **Save As Copy**, outside the Inspections folder, then reload;
  - **Overwrite**, with a second "use with caution" warning; RED first keeps an exact copy of the newer file in its local Recovery folder, and the fees stay as in the newer file;
  - **Keep editing**, which pauses autosave until you choose.
- **A moved or deleted report is never recreated.** You can save a copy and close, close without saving, or keep it open.
- **Opening a report** that is still syncing shows "The inspection file is busy (probably syncing). Try again in a moment." A damaged file says so and is left unchanged. My List drops a file that disappears while it loads.
- Temporary and backup files for Outbox, Archive and Review saves go in a hidden `.red-save-staging` folder inside My List, never next to those reports. This works on any drive or network share.
- **Your edits are not lost:**
  - While saving is paused after a conflict, RED keeps one local recovery copy of your edits and updates it as you edit.
  - If a PDF edit can't be saved into a report that changed or moved, Save and Close show the conflict choices first. Reload or closing a moved report can leave the edited PDF file where it is instead of blocking.
  - Reload and "close without saving" make a fresh recovery copy first, and a Reload that cannot finish leaves the report open with your edits.
  - If the Inspections folder or drive is temporarily unavailable, Save says so and keeps your edits; RED does not treat it as a moved report.
- Save and Close wait while a report is still opening. Reloading the same report does not show its rules again.

## 2.2.0 — ORDERS: the live Strand schedule, like INSPECT 2026

- The **ORDERS** tab now shows the live Strand service-request list, matching INSPECT 2026's Schedule tab:
  - groups Unassigned, Assigned, Field Complete, Completed and Deleted
  - pending jobs highlighted
  - sorting, and a 5-minute auto refresh that keeps your place
- **Toolbar**, like INSPECT's Schedule ribbon:
  - **Open** opens the order dialog: edit, Set Job Number, or Completed/Failed for surveys.
  - **Delete…** is shown only to users with the Strand "Service Request Delete" or "Edit - All" permission.
  - **Add to My List** assigns the order to you, downloads its inspection form from Strand and saves it to your Inspect My List folder, where INSPECT sees it right away.
  - **Assign…** is reserved for the owner's account.
- **Filter row** under every column, with INSPECT's 12 operators (Contains by default). Dates compare by day. Shows "Filtered: showing N of M" with Clear Filters.
- **Settings > Inspector Profile**: RED keeps its own copy of your profile. The first time 2.2.0 starts, it imports everything from INSPECT on this PC: full name, phone, default state, Inspections folder, user type, your Strand login, and INSPECT's other settings. **Import from INSPECT** brings them over again at any time; when values differ, RED lists each change and asks first. Name, phone, state and folder can be edited in RED. The user type is read-only: change Type in INSPECT Settings, then press Import from INSPECT.
- **Profile required**: if anything INSPECT itself requires at start is missing (full name, Strand username and password, default state, user type, an existing Inspections folder), RED opens on Settings with "Please insert missing profile information before anything else." My List and ORDERS stay locked until the profile is complete.
- My List now follows the profile's Inspections folder, the same folder Add to My List saves into.
- **Settings > Strand Account**: RED saves your Strand login protected for your Windows account and signs ORDERS in silently. Use Test & Save or Clear to manage it. Without a connection, Test & Save keeps the login untested.
- As in INSPECT, a Preinspector's new forms are marked as preinspections, and only Inspectors ask Strand for the job's files.
- Owner only: an **iPad fee token** generator. The token is shown once and never saved.
- RED never shows, edits or recalculates fees.

## 2.1.23 — Enforce one selected checklist row

- Every item-selection path now clears the previous left-pane highlight before painting the newly selected row.
- This covers row taps, Photo Required, Fail/status badges, value-choice controls, classic tree selection, and previous/next navigation.
- Selecting another item still synchronously closes any drawer owned by the previous item.

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
