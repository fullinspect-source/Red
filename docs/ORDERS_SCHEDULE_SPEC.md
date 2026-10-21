# ORDERS tab ← INSPECT2026 "Schedule" tab — behavior spec

Source: static analysis (REA) of `Inspect2026.dll` 2026.1002.1 (sha256 9756c65d…b1d8), .NET 10 WinForms + DevExpress.
Nothing was executed; every item below comes from metadata, string literals and call graphs. Items marked
*(inferred)* rest on call order or IL shape, not on direct observation.

## Where it lives
- `DashboardWindow` left nav: **"Schedule"** nav item → `LoadRequests()` → `requestGridControl` (service requests).
  The separate **"Buyer Walk"** nav item → `LoadSchedule()` → `scheduleGridControl` (buyer walks). The user-facing
  Schedule tab is the **service-request grid**, not the buyer-walk grid.

## Data model — `ServiceRequestItem`
Id (Guid), WorkOrderId (Guid?), ServiceId, ServiceName, ServiceTypeId, RequestTimeRequired (bool),
Date (DateTimeOffset) → LocalDate = Date.ToLocalTime().DateTime, StatusId, StatusName, AssignedUsername,
AssignedName, SpecialRequest, IsPendingJob, JobNumber, Address, City, Lot, Block, SubdivisionName, BuilderName.

Computed **InspectGroup** from StatusId (RequestStatus constants):
| StatusId | Group |
|---|---|
| 1 REQUESTED | Unassigned |
| 2 ASSIGNED | Assigned |
| 3 FIELD_COMPLETE | Field Complete |
| 0 DELETED | Deleted (last 7 days) |
| anything else (pass/complete/fail/cancelled/…) | Completed (last 7 days) |

## Loading (backend)
- `GET Inspect/Request/List?StartDate={today-7d}&EndDate={today+1 month}` on the Strand API (authenticated RestClient).
- On HTTP 200 it deserializes `List<ServiceRequestItem>`. For each item where RequestTimeRequired is set:
  `SpecialRequest = "(Scheduled for " + LocalDate.ToShortTimeString() + ")  " + SpecialRequest`.
- Nothing is cached on disk: the list is fetched live every time.
- **Auto refresh:** a 5-minute timer (300000 ms) calls LoadRequests again. Refresh keeps the focused row,
  the top visible row and the expanded/collapsed state of each group. The first load expands all groups.
- The ribbon **Refresh** button reloads the list.

## Grid layout
Columns (caption → field): Job (JobNumber) · Address · Subdivision (SubdivisionName) · Builder (BuilderName) ·
City · Service (ServiceName) · **Schedule Date** (LocalDate) · Assigned (AssignedName) · Special Request ·
Status (StatusName) · **Status Group** (InspectGroup, grouped) · Pending Job (IsPendingJob, checkbox) ·
Time Required (RequestTimeRequired). There is also a DevExpress checkbox-selector column for multi-select.
- **Grouped by Status Group** with a custom group order: Unassigned → Assigned → Field Complete →
  Completed (last 7 days) → Deleted (last 7 days).
- **Schedule Date display:** `M/d/yyyy hh:mm tt` when RequestTimeRequired is true. Otherwise the date only *(inferred)*.
- **Row style:** a row whose Pending Job cell is checked gets a Salmon→SeaShell background gradient.
- The grid is read-only (not editable in place). It has header filter buttons and column sort.
  The layout is saved to and restored from the registry, and "Reset Grid" / show-all-columns restores the default layout.

## Interactions
- **Left-click on the checkbox column:** toggles that row's selection.
- **Left-click on any other cell:** opens the request.
  - Status FIELD_COMPLETE (survey) → **Complete Survey Request** dialog, with Completed and Failed buttons
    (`Inspect/Request/Complete`) and point-file/picture downloads.
  - Any other status → **Edit Service Request** dialog. Fields: Job, Service, Subdivision, Lot, Block, Builder,
    Special Request, Assigned To (inspector combo), Assigned On, Requested For, Completed On, Scheduled date+time,
    Requestor, Superintendent, Reinspection, Attempt, PO Number, Requisition Number, Status, History, a Pending Job
    alert, plus Delete / Set Job Number / Add to My List / Save buttons.
    - Save validation: "Please set the scheduled date…". When a PO is required: "Please provide a purchase order or
      requisition number…".
    - Save sends `PATCH Inspect/Request` (RestSharp Method 6) with {Id, AssignedUsername, Date, IsReinspection,
      Attempt, PurchaseOrderRequired, PurchaseOrderNumber, RequisitionNumber}.
  - When the dialog closes, the list reloads.
- **Assign** (multi-select; INSPECT only, removed from RED on 2026-10-20): blocked for a pending job, a deleted
  status or a completed status, each with its own message. If some selected rows are already assigned it warns "N of
  M items selected are already assigned…". It then opens the Assign dialog: inspectors come from
  `Inspect/User/Inspectors`, crew chiefs for surveys from `Inspect/User/CrewChiefs`, and the assignment posts to
  `Inspect/Request/assign` with `{RequestIds, AssignedUsername}`. Picking "[Unassigned]" sends that row's username:
  the server's own row from `Inspect/User/Inspectors` when it sends one, else "" (INSPECT's built-in row when the list
  fails). REA 2026-10-20, static.
- **Complete:** the ribbon button exists but INSPECT never shows it on the Schedule grid (ShowGrid keeps it hidden
  in every path). Surveys are completed from the Complete Survey dialog.
- **Delete** (multi-select): requires a reason, picked from a radio list ("The builder cancelled ahead of scheduled
  date/time." / "The inspection has already been completed." / "No purchase order was given." / "This service
  is not in contract." / Another reason + free text). It then deletes each request and reports successes.
- **Add to My List** (Inspect2026 2026.1012.2, `ServiceRequestManager.AddToMyList`):
  1. Each row is pre-checked: a pending job, StatusId 0, or a completed status ({4,5,6,7,8,9}) is refused.
  2. `PATCH Inspect/Request/assign` assigns all remaining rows to the current user.
  3. Per row: `GET` the request, `PATCH Inspect/Request`, then `GET` it again.
  4. `GET Inspect/Request/forms?ServiceId&AccountId={BuilderId}` lists the forms. With one, it is used; with several,
     a chooser opens; with none, "Form Not Found".
  5. A website cookie session is opened (`POST Security/LogOn?UserName&Password` on FormConfig.URL).
  6. `GET /Inspect/(Re)inspection/Create?jobNumber&formId` returns the form JSON.
  7. Fields are set: Contact, ContactNumber, Requests (with the "(Scheduled for …)" prefix), IsReinspection,
     Attempt, PO, Requisition, WorkOrderId, the Superintendent item, and InspectorName/Phone/Username.
     InspectionNumber is built as `{job7}-{code}-{attempt}-{initials}` when blank.
     The fee `*_Secure` values become `Encrypt("0.00")`.
  8. Duplicates are checked by header InspectionNumber in My List (replace?), Outbox, then Archive
     (move instead?).
  9. The form is saved as `{InspectFolder}\MyList\{InspectionNumber}.ins`: plain compact JSON, UTF-8 without a BOM.
  10. For UserType "Inspector", `PUT /Inspect/Jobs/RequestFiles?jobNumber={job7}`.
  11. Result: "Copied N service requests to My List." or "Only X out of Y …".

## Toolbar (Inspect's ShowGrid for the Schedule grid)
Inspect's Schedule ribbon shows Open, Delete, Add to My List, Assign, plus Settings, Refresh and Exit. Delete and
Assign depend on the Strand permissions "Service Request Edit - All", "Service Request Edit" and "Service Request Delete".
RED's ORDERS toolbar (owner 2026-10-20) has **Open, Add to My List, Delete…, Unassign**, with Refresh in the header.
The user asked that RED have no Settings or Exit, and **no Assign at all**: Add to My List assigns to the signed-in
user. Delete… keeps INSPECT's permission-based visibility. The order dialog's Assigned To box is read-only for
everyone, the super user included. The grid has group-row select checkboxes and Inspect's visible columns (Job,
Address, Subdivision, Builder, City, Service, Schedule Date, Assigned, Note).

### Owner rules of 2026-10-20 (`Services/OrdersAddRules.cs`, `OrdersTouchDialog.cs`)
- **Unassign** (`OrdersUnassignButton_Click`):
  - Re-reads `Inspect/Request/List` and judges each selected order by its current row.
  - Anyone may unassign their own orders; only the super user (`StrandScheduleService.IsSuperUser`) may unassign
    someone else's.
  - Pending, deleted and completed orders are skipped with INSPECT's Assign messages; so are orders that are not
    assigned or no longer on the schedule.
  - It then loads `Inspect/User/Inspectors` and sends one `PATCH Inspect/Request/assign` with the "[Unassigned]" row's
    username (`StrandScheduleService.UnassignAsync`).
  - It reloads, then lists the skipped orders in one message.
- **Date question** (Add to My List, toolbar and order dialog):
  - It asks once (Yes/No) for orders on an earlier day, more than 30 hours ahead, or with no date.
  - A date-only order counts from local midnight of its day, so today's and tomorrow's orders never ask.
  - For one order: "That service is for {date}. Are you sure you want to add it to My List?"
  - No leaves those orders out.
- **Same-address checklist** (Add to My List):
  - It lists other open orders (Requested or Assigned, no pending job) at the same address (normalized, same city),
    on the same local day and of the same service type.
  - It leaves out orders already in My List: a file named `{job7}-{service code}-…`.
  - It is a touch dialog: large rows, nothing ticked, "Add selected (n)" and "Add none".
  - Ticked orders join the same batch: one assign call, one summary.

## Auth (traced)
- `POST {Apps2ApiURL}api/Auth` with body `{Username, Password, ClientId: "InspectClient"}`. Apps2ApiURL defaults
  to `https://api.strandae.com/`.
- The response `{Success, AccessToken, User, ErrorMessage}` supplies a JWT, which is sent as `Authorization: Bearer`.
  `User.Account.Id` is the AccountId used by `Inspect/User/Inspectors`.
- HTTP verbs, taken from the RestSharp Method values in the IL: List and Get use GET. Update, assign, Complete and
  Job use PATCH. Delete uses DELETE with a JSON body `{Id, Reason}`.

## Implementation in RED
| Piece | File |
|---|---|
| Strand API client + row/detail models | `Services/StrandScheduleService.cs` |
| ORDERS grid, grouping, sorting, refresh, actions | `InspectionPickerWindow.Orders.cs`, `InspectionPickerWindow.xaml` (OrdersPanel) |
| Edit / Complete Survey dialog | `OrderRequestWindow.xaml(.cs)` |
| Sign-in, form-chooser, Delete-reason dialogs | `OrdersDialogs.cs` |
| Date question and same-address checklist (touch dialog) | `OrdersTouchDialog.cs` |
| Unassign, date and same-address rules | `Services/OrdersAddRules.cs` (tests: `tests/OrdersRulesHarness`, `tests/orders_toolbar_static_test.py`) |
| Saved Strand login (DPAPI) + import from Inspect | `Services/StrandCredentialService.cs` |
| Settings > Strand Account card, silent sign-in | `InspectionPickerWindow.StrandAccount.cs` |

### Strand login
- **RED's own copy.** RED keeps its Strand login in `%LOCALAPPDATA%\RED\strand_account.json`
  (`RED-2.0-Dev` for dev builds). The file holds the username, the password protected with DPAPI
  (CurrentUser scope, entropy `RED.StrandAccount.v1`), the API URL, and the source.
- **Import from Inspect.** When RED has no saved login, it imports `Username`, `Password` and `Apps2ApiURL` from
  Inspect's `config.json`. It finds that file through `InspectorFolder` in `%LOCALAPPDATA%\Strand\Inspect\config.json`,
  or falls back to `Dropbox\Inspections`. Inspect 2022 and 2026 share these files and use the same password format:
  base64 of `[int32 IV length][IV][AES-256-CBC ciphertext]`, with the key derived by PBKDF2-SHA1 (1000 iterations)
  from Inspect's built-in secret and salt. RED decrypts the imported password the same way. This was verified
  against both binaries' IL, plus a synthetic round-trip test.
- **Silent sign-in when ORDERS opens.** RED tries the saved login first. Inspect's login is a fallback only when
  nothing is saved, or when it is for the same user (its password changed in Inspect). A different account saved
  in RED is never silently replaced. Any login that signs in is saved. The sign-in prompt appears only when no
  login works.
- **Settings > Strand Account.** Test & Save, Import from Inspect, and Clear. Clear deletes only RED's file and the
  session; RED imports from Inspect again the next time ORDERS opens.
- **API host.** `Apps2ApiURL` is accepted only for Strand-owned HTTPS hosts (`*.strandae.com`, `*.strandsystems.com`).
  Any other value falls back to `https://api.strandae.com/`.

### Add to My List in RED
The code is in `InspectionPickerWindow.AddToMyList.cs` and `Services/StrandMyListService.cs`. It follows Inspect's
steps above, with these deliberate safety differences:
- A failure on one request is reported and the batch continues. Inspect aborts the whole batch.
- RED asks every duplicate question first and acts only after all the answers are in, so a Cancel never leaves a
  deleted My List copy.
- RED refuses when Outbox or Archive holds several copies (for example Dropbox conflicted copies), and when a
  matched copy is open in RED.
- A move into My List never overwrites an existing file.
- New forms are created in place with `FileMode.CreateNew`, so Inspect's `*.ins` watcher (Created only, no
  Renamed handler) shows them right away.
- RED never overwrites same-named files in other folders. Inspect's Save does, including Review.
- A move from Archive counts as copied.
- The website login is URL-encoded, and is sent only to Strand-owned HTTPS hosts.

### Known gaps vs. INSPECT2026
- **Survey point-file and picture downloads** in the Complete Survey dialog are not ported. Completed/Failed
  send no attachment.
- **Crew-chief assignment for surveys** (`Inspect/User/CrewChiefs`) is not ported (surveys are out of scope; RED has
  no Assign since 2026-10-20).
- **Group-by panel** (drag a column to regroup) is not ported. The filter row is ported
  (`InspectionPickerWindow.OrdersFilter.cs`, Inspect's 12 operators).
- Column layout is not persisted, and there is no Reset Grid button (INSPECT saves its DevExpress layout to the registry).
- Builds with the .NET 8 SDK (0 errors). Not yet exercised against the live Strand API.
