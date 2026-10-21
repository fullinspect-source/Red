// ORDERS rules from the owner's request of 2026-10-20 (F-001): the Add to My List date check, the same-address
// checklist, Unassign, and the assign calls Add to My List and Unassign send (checked against a loopback listener;
// nothing reaches Strand). Run: dotnet run --project tests\OrdersRulesHarness
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using InspectionEditor.Services;
using Newtonsoft.Json.Linq;

// The expected date texts are en-US; pin the culture so a Region override on the PC cannot change them.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");

int passed = 0, failed = 0;
void Check(bool ok, string name)
{
    if (ok) passed++; else failed++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
}

DateTimeOffset At(int day, int hour, int minute = 0) =>
    new(DateTime.SpecifyKind(new DateTime(2026, 10, 20).AddDays(day).AddHours(hour).AddMinutes(minute), DateTimeKind.Local));
var now = At(0, 9);

// --- Date window: 0 to 30 hours ahead adds without a question ---
Check(OrdersAddRules.DateWindow(At(0, 17), true, now) == OrderDateWindow.Soon, "timed later today: no question");
Check(OrdersAddRules.DateWindow(At(0, 8), true, now) == OrderDateWindow.Soon, "timed earlier today: still today, no question");
Check(OrdersAddRules.DateWindow(At(1, 15), true, now) == OrderDateWindow.Soon, "timed exactly 30 hours ahead: no question (inclusive)");
Check(OrdersAddRules.DateWindow(At(1, 15, 1), true, now) == OrderDateWindow.Later, "timed 30 h 1 min ahead: asks");
Check(OrdersAddRules.DateWindow(At(0, 0), false, now) == OrderDateWindow.Soon, "date-only today: no question");
Check(OrdersAddRules.DateWindow(At(1, 0), false, now) == OrderDateWindow.Soon, "date-only tomorrow: no question");
Check(OrdersAddRules.DateWindow(At(1, 16), false, now) == OrderDateWindow.Soon, "date-only tomorrow stored at 4 PM: midnight counts (15 h), not the stored time (31 h)");
Check(OrdersAddRules.DateWindow(At(2, 0), false, now) == OrderDateWindow.Later, "date-only day after tomorrow at 9 AM: asks (39 h)");
Check(OrdersAddRules.DateWindow(At(2, 0), false, At(0, 18)) == OrderDateWindow.Soon, "date-only day after tomorrow at 6 PM: exactly 30 h, no question");
Check(OrdersAddRules.DateWindow(At(2, 20), false, At(0, 18)) == OrderDateWindow.Soon, "date-only stored at 8 PM two days on: midnight (30 h) counts, not 50 h");
Check(OrdersAddRules.DateWindow(At(-1, 16), true, now) == OrderDateWindow.Earlier, "timed yesterday: asks");
Check(OrdersAddRules.DateWindow(At(-1, 0), false, now) == OrderDateWindow.Earlier, "date-only yesterday: asks");
Check(OrdersAddRules.DateWindow(At(9, 0), false, now) == OrderDateWindow.Later, "next week: asks");
Check(OrdersAddRules.DateWindow(null, false, now) == OrderDateWindow.NoDate, "no date: asks");
var utc = new DateTimeOffset(At(1, 15).UtcDateTime, TimeSpan.Zero);
Check(OrdersAddRules.DateWindow(utc, true, now) == OrderDateWindow.Soon, "a UTC offset is compared as the same instant");

OrderAddItem Item(DateTimeOffset? date, bool timed) => new(Guid.NewGuid(), "FRM - Framing", "1 A St", "X", date, timed, 2);
Check(OrdersAddRules.NeedsDateQuestion(Item(At(-1, 0), false), now), "asks: yesterday");
Check(OrdersAddRules.NeedsDateQuestion(Item(null, false), now), "asks: no date");
Check(OrdersAddRules.NeedsDateQuestion(Item(At(1, 15, 1), true), now), "asks: 30 h 1 min ahead");
Check(!OrdersAddRules.NeedsDateQuestion(Item(At(0, 17), true), now), "no question: later today");
Check(!OrdersAddRules.NeedsDateQuestion(Item(At(0, 7), true), now), "no question: earlier today");
Check(!OrdersAddRules.NeedsDateQuestion(Item(At(1, 0), false), now), "no question: date-only tomorrow");

// --- Date question wording ---
var one = new OrderAddItem(Guid.NewGuid(), "HER - HERS Energy Rough", "100 Example St", "Exampleville", At(9, 0), false, 2);
Check(OrdersAddRules.DateQuestion(new[] { one }, 1) == "That service is for 10/29/2026. Are you sure you want to add it to My List?",
    "one order: the owner's wording with the date");
var timed = one with { Date = At(9, 14, 30), TimeRequired = true };
Check(OrdersAddRules.DateQuestion(new[] { timed }, 1) == "That service is for 10/29/2026 02:30 PM. Are you sure you want to add it to My List?",
    "one timed order: the grid's Schedule Date text");
Check(OrdersAddRules.DateQuestion(new[] { one with { Date = null } }, 1) == "That service has no scheduled date. Are you sure you want to add it to My List?",
    "one order with no date");
string nl = Environment.NewLine;
var older = new OrderAddItem(Guid.NewGuid(), "FRM - Framing", "102 Example St", "Exampleville", At(-2, 0), false, 2);
Check(OrdersAddRules.DateQuestion(new[] { one, older }, 3) ==
      $"These services are not within the next 30 hours:{nl}{nl}HER - HERS Energy Rough for 100 Example St: 10/29/2026{nl}FRM - Framing for 102 Example St: 10/18/2026{nl}{nl}Are you sure you want to add them to My List?",
    "several orders: listed with their dates");
Check(OrdersAddRules.DateQuestion(new[] { one }, 2) ==
      $"This service is not within the next 30 hours:{nl}{nl}HER - HERS Energy Rough for 100 Example St: 10/29/2026{nl}{nl}Are you sure you want to add it to My List?",
    "one of several: names the order instead of \"That service\"");

// --- Same address, same day ---
Check(OrdersAddRules.AddressKey("  100  Example   St ", " exampleville ") == OrdersAddRules.AddressKey("100 example st", "Exampleville"),
    "address key: trimmed, spaces collapsed, case-insensitive");
Check(OrdersAddRules.AddressKey("100 Example St", "Exampleville") != OrdersAddRules.AddressKey("100 Example St", "Sampletown"),
    "address key: a different city is a different address");
Check(OrdersAddRules.AddressKey("  ", "Exampleville") == "", "address key: a blank address never matches");

StrandScheduleRequest Row(int n, string service, string address, string city, DateTimeOffset? date, int status = StrandRequestStatus.Assigned,
                          bool pending = false, string job = "", string assigned = "", string assignedName = "", int type = 2) => new()
{
    Id = new Guid($"00000000-0000-4000-8000-0000000002{n:00}"), ServiceName = service, Address = address, City = city, Date = date,
    StatusId = status, IsPendingJob = pending, JobNumber = job.Length > 0 ? job : $"88000{n:00}A",
    AssignedUsername = assigned, AssignedName = assignedName, ServiceTypeId = type
};
var main = Row(1, "FRM - Framing", "200 Example Ave", "Exampleville", At(0, 0));
var loaded = new List<StrandScheduleRequest>
{
    main,
    Row(2, "HER - HERS Energy Rough", "200 example ave ", "EXAMPLEVILLE", At(0, 13), job: "8800001A"),                 // match, later time
    Row(3, "INS - Insulation", "200 Example Ave", "Exampleville", At(0, 8), status: StrandRequestStatus.Requested),      // match, earlier time
    Row(4, "FND - Foundation", "200 Example Ave", "Exampleville", At(1, 0)),                                           // another day
    Row(5, "FND - Foundation", "202 Example Ave", "Exampleville", At(0, 0)),                                           // another address
    Row(6, "FND - Foundation", "200 Example Ave", "Sampletown", At(0, 0)),                                             // another city
    Row(7, "FND - Foundation", "200 Example Ave", "Exampleville", At(0, 0), status: StrandRequestStatus.Deleted),      // deleted
    Row(8, "FND - Foundation", "200 Example Ave", "Exampleville", At(0, 0), status: StrandRequestStatus.FieldComplete),// field complete
    Row(9, "FND - Foundation", "200 Example Ave", "Exampleville", At(0, 0), status: 5),                                // completed
    Row(10, "FND - Foundation", "200 Example Ave", "Exampleville", At(0, 0), pending: true),                           // pending job
    Row(11, "SHR - Shear", "200 Example Ave", "Exampleville", At(0, 0), job: "8800011A"),                               // already in My List
    Row(12, "FNL - Final", "200 Example Ave", "Exampleville", At(0, 0), assigned: "other@example.test", assignedName: "Other Inspector"), // someone else's
    Row(13, "PLB - Plumbing", "200 Example Ave", "Exampleville", null),                                               // no date
    Row(14, "ROF - Roof", "200 Example Ave", "Exampleville", At(0, 0)),                                                // declined at the date question
    Row(15, "FBS - Form Board Survey", "200 Example Ave", "Exampleville", At(0, 0), assigned: "crew@example.test", assignedName: "Crew Chief", type: 7), // a survey
};
var adding = new[] { OrdersAddRules.ItemFor(main) };
var excluded = new HashSet<Guid> { main.Id, loaded[13].Id };
var myListFiles = new[] { "8800011-SHR-1-JQD", "8800001-FRM-1-JQD" };
var candidates = OrdersAddRules.SameAddressCandidates(adding, loaded, excluded, myListFiles);
Check(candidates.Select(c => c.ServiceName).SequenceEqual(new[] { "FNL - Final", "INS - Insulation", "HER - HERS Energy Rough" }),
    "same address and day: open orders only, by time then service; " + string.Join(", ", candidates.Select(c => c.ServiceName)));
Check(!candidates.Any(c => c.Id == main.Id), "the order being added is not offered again");
Check(!candidates.Any(c => c.Id == loaded[13].Id), "an order declined at the date question is not offered");
Check(candidates.Any(c => c.AssignedUsername == "other@example.test"), "someone else's order is offered (it shows who has it)");
Check(!candidates.Any(c => c.ServiceTypeId == 7), "a survey at the same address and day is not offered with an inspection");
Check(OrdersAddRules.SameAddressCandidates(new[] { OrdersAddRules.ItemFor(loaded[14]) }, loaded, new HashSet<Guid> { loaded[14].Id }, myListFiles).Count == 0,
    "a survey being added offers only surveys (none here)");
var savedItem = OrdersAddRules.ItemFor(main) with { Date = At(1, 10) };
var items = OrdersAddRules.ItemsFor(new[] { main, loaded[1] }, savedItem);
Check(items[0] == savedItem && items[1].Date == loaded[1].Date, "the order dialog's saved item replaces its own grid row only");
Check(OrdersAddRules.ItemsFor(new[] { main }, null)[0] == OrdersAddRules.ItemFor(main), "without a dialog item the grid rows are used");
Check(OrdersAddRules.SameAddressCandidates(adding, loaded, excluded, Array.Empty<string>()).Any(c => c.ServiceName == "SHR - Shear"),
    "without the My List file the Shear order is offered");
Check(OrdersAddRules.SameAddressCandidates(new[] { adding[0] with { Address = "" } }, loaded, excluded, myListFiles).Count == 0,
    "an order with a blank address finds nothing");
Check(OrdersAddRules.SameAddressCandidates(new[] { adding[0] with { Date = null } }, loaded, excluded, myListFiles).Count == 0,
    "an order with no date finds nothing");
var edited = adding[0] with { Date = At(1, 10) };
Check(OrdersAddRules.SameAddressCandidates(new[] { edited }, loaded, excluded, myListFiles).Select(c => c.ServiceName).SequenceEqual(new[] { "FND - Foundation" }),
    "the sheet's edited date is the one matched");

Check(OrdersAddRules.CandidateDetail(loaded[11], "me@example.test") == "Job 8800012A · 10/20/2026 · 200 Example Ave · Assigned to Other Inspector",
    "checklist row detail: job, date, address and who has it");
Check(OrdersAddRules.CandidateDetail(loaded[11], "OTHER@example.test") == "Job 8800012A · 10/20/2026 · 200 Example Ave",
    "checklist row detail: my own order does not say who has it");
Check(OrdersAddRules.CandidateDetail(loaded[2], "me@example.test") == "Job 8800003A · 10/20/2026 · 200 Example Ave",
    "checklist row detail: an unassigned order");

// --- In My List ---
Check(OrdersAddRules.IsInMyList(loaded[10], myListFiles), "My List file 8800011-SHR-1-JQD covers job 8800011A, SHR");
Check(!OrdersAddRules.IsInMyList(loaded[1], myListFiles), "a different code on the same job is not in My List");
Check(OrdersAddRules.IsInMyList(Row(20, "SHR - Shear", "x", "y", null, job: "8800011B"), new[] { "8800011-shr-P-2-AB" }),
    "match ignores case and the preinspection P part");
Check(!OrdersAddRules.IsInMyList(Row(21, "Shear", "x", "y", null, job: "8800011A"), myListFiles), "a service name without a code never matches");
Check(!OrdersAddRules.IsInMyList(Row(22, "SHR - Shear", "x", "y", null, job: "88001"), new[] { "88001-SHR-1-AB" }), "a job shorter than 7 never matches");

// --- Unassign ---
const string me = "me@example.test";
var mine = Row(30, "FRM - Framing", "1 A St", "X", At(0, 0), assigned: "ME@example.test", assignedName: "Me");
var theirs = Row(31, "HER - HERS Energy Rough", "2 B St", "X", At(0, 0), assigned: "other@example.test", assignedName: "Other Inspector");
var free = Row(32, "INS - Insulation", "3 C St", "X", At(0, 0), status: StrandRequestStatus.Requested);
var pendingRow = Row(33, "FND - Foundation", "4 D St", "X", At(0, 0), pending: true, assigned: me);
var deleted = Row(34, "FND - Foundation", "5 E St", "X", At(0, 0), status: StrandRequestStatus.Deleted, assigned: me);
var done = Row(35, "FND - Foundation", "6 F St", "X", At(0, 0), status: 7, assigned: me);
var survey = Row(36, "SRV - Survey", "7 G St", "X", At(0, 0), status: StrandRequestStatus.FieldComplete, assigned: me);
Dictionary<Guid, StrandScheduleRequest> Current(params StrandScheduleRequest[] rows) => rows.ToDictionary(r => r.Id);
var allCurrent = Current(mine, theirs, free, pendingRow, deleted, done, survey);
var plan = OrdersAddRules.PlanUnassign(new[] { mine, theirs, free, pendingRow, deleted, done, survey }, allCurrent, me, isSuperUser: false);
Check(plan.Rows.SequenceEqual(new[] { mine, survey }), "Unassign: my own orders (Field Complete too), any case of my username");
Check(plan.Skipped.SequenceEqual(new[]
{
    "A selected HER - HERS Energy Rough for 2 B St is assigned to Other Inspector. Only the super user can unassign someone else's order.",
    "A selected INS - Insulation for 3 C St is not assigned.",
    "A selected FND - Foundation for 4 D St can not be assigned due to a pending job.",
    "A selected FND - Foundation for 5 E St has a deleted status and can not be updated.",
    "A selected FND - Foundation for 6 F St has a completed status and can not be updated.",
}), "Unassign: skipped rows, in selection order, with INSPECT's Assign messages");
var superPlan = OrdersAddRules.PlanUnassign(new[] { mine, theirs, free }, allCurrent, me, isSuperUser: true);
Check(superPlan.Rows.SequenceEqual(new[] { mine, theirs }) && superPlan.Skipped.Count == 1, "Unassign: the super user may unassign someone else's order");
// The grid can be minutes old: each order is judged by the server's current row.
var nowTheirs = Row(30, "FRM - Framing", "1 A St", "X", At(0, 0), assigned: "other@example.test", assignedName: "Other Inspector");
var nowDone = Row(32, "INS - Insulation", "3 C St", "X", At(0, 0), status: 5, assigned: me);
var stale = OrdersAddRules.PlanUnassign(new[] { mine, free, survey }, Current(nowTheirs, nowDone), me, isSuperUser: false);
Check(stale.Rows.Count == 0 && stale.Skipped.SequenceEqual(new[]
{
    "A selected FRM - Framing for 1 A St is assigned to Other Inspector. Only the super user can unassign someone else's order.",
    "A selected INS - Insulation for 3 C St has a completed status and can not be updated.",
    "A selected SRV - Survey for 7 G St is no longer on the schedule.",
}), "Unassign: reassigned or closed since the grid loaded, or gone from the server, is skipped");
var fresh = OrdersAddRules.PlanUnassign(new[] { free }, Current(Row(32, "INS - Insulation", "3 C St", "X", At(0, 0), assigned: me)), me, isSuperUser: false);
Check(fresh.Rows.Count == 1 && fresh.Rows[0].AssignedUsername == me, "Unassign: an order assigned to me since the grid loaded is unassigned (the current row is sent)");
Check(!OrdersAddRules.IsAssigned(Row(37, "s", "a", "c", null, assigned: "placeholder-id", assignedName: "[Unassigned]")),
    "an order showing [Unassigned] counts as not assigned");
Check(OrdersAddRules.UnassignResult(1) == "1 request was unassigned." && OrdersAddRules.UnassignResult(3) == "3 requests were unassigned.",
    "Unassign result text");

// --- What the assign calls send (loopback listener; the token and base are set for the test only) ---
var listener = new HttpListener();
int port = 18000 + Environment.ProcessId % 1000;
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
var captured = new List<(string Method, string Path, string Body)>();
string inspectorsReply = "[{\"UserName\":\"placeholder-id\",\"DisplayName\":\"[Unassigned]\"},{\"UserName\":\"me@example.test\",\"DisplayName\":\"Me\"}]";
int inspectorsStatus = 200;
bool signInDuringInspectors = false;
var serve = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync(); } catch { break; }
        string body = new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEnd();
        string path = ctx.Request.Url!.PathAndQuery;
        lock (captured) captured.Add((ctx.Request.HttpMethod, path, body));
        bool inspectors = path.StartsWith("/Inspect/User/Inspectors");
        if (inspectors && signInDuringInspectors)
        {
            // Another sign-in completes while the inspector list is loading.
            var generation = typeof(StrandScheduleService).GetProperty(nameof(StrandScheduleService.SessionGeneration))!;
            generation.SetValue(null, (int)generation.GetValue(null)! + 1);
        }
        string reply = inspectors ? inspectorsReply : "";
        var bytes = Encoding.UTF8.GetBytes(reply);
        ctx.Response.StatusCode = inspectors ? inspectorsStatus : 200;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }
});
var service = typeof(StrandScheduleService);
service.GetField("_apiBase", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, new Uri($"http://127.0.0.1:{port}/"));
service.GetField("_accessToken", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, "harness-token");
service.GetProperty("Username")!.SetValue(null, me);

var ids = new[] { new Guid("00000000-0000-4000-8000-000000000301"), new Guid("00000000-0000-4000-8000-000000000302") };
await StrandScheduleService.AssignAsync(ids, StrandScheduleService.Username);
var assign = captured.Last();
var assignBody = JObject.Parse(assign.Body);
// The call Add to My List makes once for its batch (orders_toolbar_static_test pins that it does, before any per-order work).
Check(assign.Method == "PATCH" && assign.Path == "/Inspect/Request/assign", "assign-to-me call: PATCH Inspect/Request/assign");
Check(assignBody.Properties().Select(p => p.Name).SequenceEqual(new[] { "RequestIds", "AssignedUsername" })
      && assignBody["RequestIds"]!.Values<string>().SequenceEqual(ids.Select(i => i.ToString()))
      && (string?)assignBody["AssignedUsername"] == me,
    "assign-to-me call: every id, AssignedUsername = the signed-in user");

// Unassign sends what INSPECT's Assign dialog sends for its "[Unassigned]" row (REA 2026-10-20): the server's own
// row's username when Inspect/User/Inspectors has one, else "" (INSPECT's built-in row when the list fails).
(string Method, string Path, string Body) LastAssign() => captured.Last(c => c.Path == "/Inspect/Request/assign");
captured.Clear();
await StrandScheduleService.UnassignAsync(ids);
Check(captured.Select(c => $"{c.Method} {c.Path.Split('?')[0]}").SequenceEqual(new[] { "GET /Inspect/User/Inspectors", "PATCH /Inspect/Request/assign" }),
    "Unassign: loads the inspector list, then one assign call");
var unassignBody = JObject.Parse(LastAssign().Body);
Check(unassignBody.Properties().Select(p => p.Name).SequenceEqual(new[] { "RequestIds", "AssignedUsername" })
      && unassignBody["RequestIds"]!.Values<string>().SequenceEqual(ids.Select(i => i.ToString()))
      && (string?)unassignBody["AssignedUsername"] == "placeholder-id",
    "Unassign: every id, AssignedUsername = the server's [Unassigned] username");

inspectorsReply = "[{\"UserName\":\"me@example.test\",\"DisplayName\":\"Me\"}]";
await StrandScheduleService.UnassignAsync(ids);
Check((string?)JObject.Parse(LastAssign().Body)["AssignedUsername"] == "", "Unassign: no [Unassigned] row from the server sends \"\"");

inspectorsStatus = 500; inspectorsReply = "";
await StrandScheduleService.UnassignAsync(ids);
Check((string?)JObject.Parse(LastAssign().Body)["AssignedUsername"] == "", "Unassign: an inspector-list failure sends \"\", as INSPECT's built-in row");
var nullCheck = JObject.Parse(LastAssign().Body)["AssignedUsername"];
Check(nullCheck != null && nullCheck.Type == JTokenType.String, "Unassign: AssignedUsername is a string, never JSON null");

// A good inspector list this time, so the sign-in lands during a successful read (not the failure fallback).
inspectorsStatus = 200; signInDuringInspectors = true;
inspectorsReply = "[{\"UserName\":\"placeholder-id\",\"DisplayName\":\"[Unassigned]\"},{\"UserName\":\"me@example.test\",\"DisplayName\":\"Me\"}]";
captured.Clear();
string? changedError = null;
try { await StrandScheduleService.UnassignAsync(ids); } catch (InvalidOperationException ex) { changedError = ex.Message; }
Check(changedError == "The Strand account changed while this request was running. Refresh to try again." && !captured.Any(c => c.Path == "/Inspect/Request/assign"),
    "Unassign: a sign-in during the inspector list stops before the assign call");
signInDuringInspectors = false;

inspectorsStatus = 401;
captured.Clear();
string? authError = null;
try { await StrandScheduleService.UnassignAsync(ids); } catch (StrandAuthRequiredException ex) { authError = ex.Message; }
Check(authError == "Your Strand session expired. Please sign in again." && !captured.Any(c => c.Path == "/Inspect/Request/assign"),
    "Unassign: an expired session stops before the assign call");

listener.Stop();
service.GetField("_accessToken", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null);

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
