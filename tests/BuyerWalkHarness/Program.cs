// F-025 Buyer Walk harness (program/features/F-025-buyer-walk/ACCEPTANCE.md), mocked website only.
//   dotnet run -p:Platform=x64
// An HttpMessageHandler mock stands behind RED's website session (no request can reach a real host: every request is
// answered by the mock), a temporary Inspect folder under %TEMP%, a fake fee encoder, synthetic fixture F only.
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using InspectionEditor;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

return await Harness.Run();

sealed class MockSite : HttpMessageHandler
{
    public CookieContainer Cookies = new();
    public Uri Base = new("https://apps.strandsystems.com/");
    public readonly List<string> Requests = new();      // "W1", or "GET Inspect/…" (relative, with query)
    public readonly List<string> UpdateBodies = new();
    public Func<string, string, (int Status, string Body)> Handler = (_, _) => (404, "");
    public Func<string, bool> NetworkError = _ => false;
    public int Logons;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string relative = request.RequestUri!.PathAndQuery.TrimStart('/');
        if (relative.StartsWith("Security/LogOn", StringComparison.Ordinal))
        {
            Logons++;
            Requests.Add("W1"); // never the URL: it holds the password (§3.6)
            Cookies.Add(Base, new Cookie("ASP.NET_SessionId", "mock-session-" + Logons));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
        }
        string line = $"{request.Method} {relative}";
        Requests.Add(line);
        if (request.Content != null && request.Method == HttpMethod.Post)
            UpdateBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        if (NetworkError(line))
            throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made (mock).");
        var (status, body) = Handler(request.Method.Method, relative);
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
    }
}

static class Harness
{
    private static int _pass, _fail;
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

    private static void Check(string id, bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id}  {what}");
    }

    private static string Row(int n, string job, string address, string group, string? ready, string? closing, string? first, string? second, int status, bool flagged, bool reinspection, string note) =>
        "{" + $"\"JobId\":\"00000000-0000-4000-8000-00000000000{n}\",\"JobNumber\":\"{job}\",\"BuilderName\":\"\",\"Subdivision\":\"Example Park\",\"Lot\":\"\",\"Block\":\"\"," +
        $"\"Address\":\"{address}\",\"City\":\"Exampleville\",\"PlanNumberAndElevation\":\"\",\"BuyerName\":\"\",\"OrderDate\":null," +
        $"\"ReadyDate\":{Q(ready)},\"ClosingDate\":{Q(closing)},\"ScheduleDate\":{Q(first)},\"Schedule2Date\":{Q(second)},\"ScheduleStatusId\":{status}," +
        $"\"ScheduleStatusName\":\"\",\"History\":\"\",\"Note\":\"{note}\",\"Flagged\":{(flagged ? "true" : "false")},\"GroupingField\":\"{group}\"," +
        $"\"InspectorName\":\"Sample Tester\",\"Report1Url\":\"\",\"Report2Url\":\"\",\"IsReinspection\":{(reinspection ? "true" : "false")}" + "}";

    private static string Q(string? s) => s == null ? "null" : $"\"{s}\"";

    private static readonly string B1 = "[" + string.Join(",",
        Row(1, "9900001A", "100 Example St", "A", "2026-10-15T00:00:00", "2026-10-30T00:00:00", "2026-10-20T09:30:00", null, 2, false, false, "Called"),
        Row(2, "9900002A", "102 Example St", "A", null, null, null, null, 0, true, false, ""),
        Row(3, "9900003A", "104 Example St", "B", "2026-10-01T00:00:00", "2026-10-29T00:00:00", "2026-10-01T10:00:00", "2026-10-22T14:00:00", 2, false, true, ""),
        Row(4, "9900004A", "106 Example St", "B", null, null, "2026-10-05T08:00:00", null, 5, false, false, "")) + "]";

    private const string B2Data = "{\"JobNumber\":\"9900001A\",\"ReadyDate\":\"2026-10-15T00:00:00\",\"ClosingDate\":\"2026-10-30T00:00:00\",\"BuyerName\":\"Pat Buyer\",\"BuyerPhone1\":\"(555) 010-0001\",\"BuyerPhone2\":\"\",\"BuyerEmail\":\"buyer@example.test\",\"Address\":\"100 Example St\",\"City\":\"Exampleville\",\"InspectorUsername\":\"tester@example.test\",\"ScheduleDate\":null,\"Requests\":\"\",\"OrderText\":\"Ordered\",\"ScheduleStatusId\":0,\"BuilderName\":\"Sample Builder\",\"SubdivisionName\":\"Example Park\",\"History\":\"\",\"Flagged\":false,\"Note\":\"Called\",\"Superintendent\":\"Sam Super\",\"SuperintendentEmail\":\"super@example.test\",\"SuperintendentAsstEmail\":null,\"SuperintendentPhone\":\"(555) 010-0002\",\"SalesEmail\":\"\",\"InspectorList\":[{\"Username\":\"inspector.two@example.test\",\"DisplayName\":\"Inspector Two\"},{\"Username\":\"tester@example.test\",\"DisplayName\":\"Sample Tester\"}],\"ScheduleStatusList\":[{\"Id\":0,\"Name\":\"Not Scheduled\"},{\"Id\":2,\"Name\":\"Scheduled\"},{\"Id\":5,\"Name\":\"Finaled\"},{\"Id\":6,\"Name\":\"Cancelled\"}],\"BuyerAgentName\":\"Alex Agent\",\"BuyerAgentPhone\":\"(555) 010-0003\",\"BuyerAgentEmail\":\"agent@example.test\",\"ElectricProvider\":null,\"GasProvider\":null,\"WaterProvider\":null,\"ElectricProviderPhone\":null,\"GasProviderPhone\":null,\"WaterProviderPhone\":null}";
    private static readonly string B2 = "{\"Success\":true,\"Message\":null,\"Data\":" + B2Data + "}";
    private const string W2 = "{\"WorkOrderId\":\"00000000-0000-4000-8000-0000000000f1\",\"ServiceId\":121,\"FormId\":106,\"InspectionCode\":\"BWT\",\"InspectionNumber\":\"9900001-BWT-1-ST\",\"Attempt\":1,\"IsPreinspection\":false,\"JobNumber\":\"9900001A\",\"Contact\":\"Pat Buyer\",\"Requests\":\"Scheduled for Oct 20th at 9:30. House closes on Oct 30th.\",\"InspectorUsername\":\"tester@example.test\",\"Sections\":[{\"Name\":\"Scheduling\",\"Items\":[{\"Name\":\"Superintendent\",\"Value\":\"Sam Super\"}]}]}";

    private static (StrandBuyerWalkService Service, MockSite Site) Site()
    {
        var site = new MockSite();
        var session = new StrandWebsiteSession(() => site.Base, () => ("tester@example.test", "mock-password-not-real"), site);
        site.Cookies = session.Cookies;
        site.Handler = (method, path) =>
        {
            if (path == "Inspect/BuyerWalk/GetBuyerWalks") return (200, B1);
            if (path.StartsWith("Inspect/BuyerWalk/Read?jobNumber=9900001A", StringComparison.Ordinal)) return (200, B2);
            if (path == "Inspect/BuyerWalk/Update") return (200, "");
            if (path.StartsWith("Inspect/Inspection/Create?jobNumber=9900001A", StringComparison.Ordinal)) return (200, W2);
            if (path.StartsWith("Inspect/Reinspection/Create?jobNumber=9900003A", StringComparison.Ordinal))
                return (200, W2.Replace("9900001-BWT-1-ST", "9900003-BWT-2-ST").Replace("\"JobNumber\":\"9900001A\"", "\"JobNumber\":\"9900003A\"").Replace("\"Attempt\":1", "\"Attempt\":2"));
            return (404, "");
        };
        return (new StrandBuyerWalkService(session), site);
    }

    private static StrandBuyerWalkEdit Edit(string? data = null) =>
        JsonConvert.DeserializeObject<StrandBuyerWalkEdit>(data ?? B2Data, new JsonSerializerSettings { DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind })!;

    private sealed class Ui
    {
        public readonly List<string> Questions = new(), Errors = new();
        public readonly Queue<bool> Answers = new();
        public Func<string, string>? OnError;
    }

    private static BuyerWalkAdder Adder(StrandBuyerWalkService service, string folder, Ui ui, string initials = "Sample Tester") => new()
    {
        Service = service,
        InspectorFolder = folder,
        UserKind = InspectUserKind.Inspector,
        InitialsName = initials,
        AskYesNo = q => { ui.Questions.Add(q); return ui.Answers.Count > 0 && ui.Answers.Dequeue(); },
        Error = m => ui.Errors.Add(ui.OnError?.Invoke(m) ?? m),
        IsOpenInRed = _ => false,
        ZeroFee = () => "FAKE-ZERO-FEE"
    };

    private static string NewFolder(string root, string name)
    {
        string folder = Path.Combine(root, name);
        foreach (string sub in new[] { "MyList", "Outbox", "Archive", "Review" }) Directory.CreateDirectory(Path.Combine(folder, sub));
        return folder;
    }

    private static BuyerWalkAddRow AddRow(StrandBuyerWalk r) => new() { JobNumber = r.JobNumber ?? "", IsReinspection = r.IsReinspection, StatusId = r.ScheduleStatusId, Date = r.AddDate };

    public static async Task<int> Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f025-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var output = new StringWriter();
        try
        {
            string pl = Path.Combine("..", "..");
            string xaml = File.ReadAllText(Path.Combine(pl, "InspectionPickerWindow.xaml"));
            int panelAt = xaml.IndexOf("x:Name=\"BuyerWalkPanel\"", StringComparison.Ordinal);
            string panel = panelAt < 0 ? "" : xaml.Substring(panelAt, xaml.IndexOf("</ListView>", panelAt, StringComparison.Ordinal) - panelAt);
            string tabCode = File.ReadAllText(Path.Combine(pl, "InspectionPickerWindow.BuyerWalk.cs"));
            string addCode = File.ReadAllText(Path.Combine(pl, "InspectionPickerWindow.BuyerWalkAddToMyList.cs"));
            string sheetCode = File.ReadAllText(Path.Combine(pl, "BuyerWalkScheduleWindow.cs"));
            string serviceCode = File.ReadAllText(Path.Combine(pl, "Services", "StrandBuyerWalkService.cs"));

            // ---------- A-025-01 List and grouping ----------
            var (service, site) = Site();
            var rows = await service.GetBuyerWalksAsync();
            var groups = rows.GroupBy(r => r.Group).OrderBy(g => g.Key, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("en-US"), false)).ToList();
            Check("A-025-01", site.Requests.Count(r => r == "GET Inspect/BuyerWalk/GetBuyerWalks") == 1 && groups.Count == 2 && groups[0].Key == "A" && groups[1].Key == "B" &&
                              groups[0].Select(r => r.JobNumber).SequenceEqual(new[] { "9900001A", "9900002A" }) && groups[1].Select(r => r.JobNumber).SequenceEqual(new[] { "9900003A", "9900004A" }) &&
                              !panel.Contains("<Expander") && !panel.Contains("ItemCount") && panel.Contains("BuyerWalkGroupCheckBox_Click"),
                "B1 once; groups A (R1, R2) then B (R3, R4); headers show the value and a group checkbox, no count, no expander");

            // ---------- A-025-06 Columns and formats ----------
            var headers = System.Text.RegularExpressions.Regex.Matches(panel, "<GridViewColumn (?:Header=\"([^\"]*)\" )?Width").Select(m => m.Groups[1].Value).ToList();
            var r1 = rows[0]; var r3 = rows[2];
            Check("A-025-06", headers.SequenceEqual(new[] { "", "Job", "Address", "Subdivision", "City", "Ready", "Closing", "Inspector", "1st", "2nd", "Note" }) &&
                              r1.ReadyText == "Oct 15" && r1.ClosingText == "Oct 30" && r1.FirstText == "Oct 20 9:30 AM" && r1.SecondText == "" && r3.SecondText == "Oct 22 2:00 PM" &&
                              panel.Contains("Value=\"Estimated Closing Date\"") && !panel.Contains("Header=\"Ordered\"") && !panel.Contains("Header=\"Critical\"") && !panel.Contains("Header=\"Flagged\""),
                $"columns (checkbox), Job … Note; R1 Oct 15 / Oct 30 / Oct 20 9:30 AM / empty; R3 2nd Oct 22 2:00 PM; Closing tooltip ({string.Join("|", headers)})");

            // ---------- A-025-07 Flagged row style ----------
            Check("A-025-07", panel.Contains("Binding=\"{Binding Flagged}\" Value=\"True\"") && panel.Contains("Color=\"Salmon\" Offset=\"0\"") && panel.Contains("Color=\"SeaShell\" Offset=\"1\"") &&
                              rows.Count(r => r.Flagged) == 1 && rows[1].Flagged,
                "flagged rows (R2 only) get the Salmon → SeaShell gradient (XAML trigger on Flagged)");

            // ---------- A-025-08 / 09 Row taps and Open order (source checks; UI not driven) ----------
            Check("A-025-08", tabCode.Contains("if (_pressedBuyerWalkInSelectColumn || BuyerWalkListView.SelectedItems.Count > 0)") &&
                              tabCode.Contains("item.IsSelected = !item.IsSelected;") && tabCode.Contains("await OpenBuyerWalkAsync(row);"),
                "row tap: toggles when any row is checked, else opens (source check)");
            Check("A-025-09", rows.Select(r => r.ServerIndex).SequenceEqual(new[] { 0, 1, 2, 3 }) && tabCode.Contains("OrderBy(r => r.ServerIndex)") &&
                              tabCode.Contains("CheckedBuyerWalks().FirstOrDefault()"),
                "Open uses the first checked row in server order (ServerIndex from B1; source check)");

            // ---------- A-025-10 Sheet contents ----------
            var edit = await service.ReadAsync("9900001A");
            var sheet = new BuyerWalkSheet(edit, isReinspection: false);
            Check("A-025-10", site.Requests.Contains("GET Inspect/BuyerWalk/Read?jobNumber=9900001A") && edit.BuyerAgentName == "Alex Agent" && edit.BuyerAgentPhone == "(555) 010-0003" &&
                              edit.BuyerAgentEmail == "agent@example.test" && sheet.Inspectors.Select(i => i.DisplayName).SequenceEqual(new[] { "Inspector Two", "Sample Tester" }) &&
                              sheet.Inspectors.First(i => i.Username == sheet.InspectorUsername).DisplayName == "Sample Tester" && sheet.StatusName(sheet.StatusId) == "Not Scheduled" &&
                              StrandBuyerWalkService.SheetDayText(sheet.Ready) == "10/15/2026" && StrandBuyerWalkService.SheetDayText(sheet.Closing) == "10/30/2026" &&
                              sheet.Scheduled == null && sheet.NoAnswerCount == 1 && sheet.LeftMessageCount == 1 && !sheet.Flagged &&
                              sheetCode.Contains("_agentName = ReadOnlyBox()") && sheetCode.Contains("_job = ReadOnlyBox()") && sheetCode.Contains("_history = ReadOnlyBox(multiLine: true)") &&
                              !sheetCode.Contains("[Unassigned]"),
                "sheet: agent fields read-only with loaded values, inspectors sorted (no [Unassigned]), Not Scheduled, 10/15/2026, 10/30/2026, empty Scheduled, counters 1, flag off");

            // ---------- A-025-02 Validation ----------
            var blankName = new BuyerWalkSheet(Edit(), false) { BuyerName = "" };
            Check("A-025-02", BuyerWalkSheet.ValidationText(blankName.Validate()) == "\nBuyer Name is required" &&
                              sheetCode.Contains("MessageBox.Show(this, BuyerWalkSheet.ValidationText(errors), \"Error(s)\"") &&
                              sheetCode.IndexOf("_sheet.Validate()", StringComparison.Ordinal) < sheetCode.IndexOf("_service.UpdateAsync", StringComparison.Ordinal),
                "Buyer Name blank: alert Error(s) with exactly \"\\nBuyer Name is required\", validated before any B3");

            // ---------- A-025-03 No answer counter ----------
            var noAnswer = new BuyerWalkSheet(Edit(), false);
            noAnswer.SetNoAnswerChecked(true);
            Check("A-025-03", noAnswer.Note == "No answer (1)" && !noAnswer.Flagged, "checking No answer: Note exactly \"No answer (1)\" (replaced), flag not set");

            // ---------- A-025-12 Full validation list ----------
            var empty = new BuyerWalkSheet(Edit(B2Data.Replace("\"InspectorUsername\":\"tester@example.test\"", "\"InspectorUsername\":null")), false)
            { BuyerName = "", BuyerPhone1 = "", Superintendent = "", SuperintendentEmail = "" };
            empty.SetStatus(2);
            Check("A-025-12", BuyerWalkSheet.ValidationText(empty.Validate()) ==
                              "\nBuyer Name is required\nBuyer Phone is required\nSuperintendent name is required\nSuperintendent email is required\nPlease set the scheduled date for the buyer walk.\nInspector is required",
                "the full list in INSPECT's order plus \"Inspector is required\"");

            // ---------- A-025-13 Length messages ----------
            var longOnes = new BuyerWalkSheet(Edit(), false) { Superintendent = new string('s', 101), SalesEmail = new string('e', 251) };
            Check("A-025-13", BuyerWalkSheet.ValidationText(longOnes.Validate()) ==
                              "\nSuperintendent field only allows a max of 100 characters.  Please correct\nSales email field only allows a max of 250 characters.  Please correct",
                "length messages with two spaces before \"Please\" and no final period");

            // ---------- A-025-14 Picking a schedule date ----------
            var pick = new BuyerWalkSheet(Edit(), false);
            var warnings = pick.SetScheduled(new DateTime(2026, 10, 20, 9, 30, 0));
            var early = new BuyerWalkSheet(Edit(), false); var earlyWarnings = early.SetScheduled(new DateTime(2026, 10, 1, 9, 0, 0));
            var late = new BuyerWalkSheet(Edit(), false); var lateWarnings = late.SetScheduled(new DateTime(2026, 10, 31, 9, 0, 0));
            Check("A-025-14", pick.StatusId == 2 && pick.StatusName(pick.StatusId) == "Scheduled" && pick.Note == "Scheduled for Oct 20th at 9:30 AM." && !pick.Flagged && warnings.Count == 0 &&
                              earlyWarnings.SequenceEqual(new[] { "Your schedule date is before the Ready date." }) && early.Note.StartsWith("Scheduled for Oct 1st") &&
                              lateWarnings.SequenceEqual(new[] { "Your schedule date is after the Closing date." }) &&
                              new[] { 1, 2, 3, 11, 12, 13, 21, 22, 23 }.Select(StrandBuyerWalkService.Ordinal).SequenceEqual(new[] { "1st", "2nd", "3rd", "11th", "12th", "13th", "21st", "22nd", "23rd" }),
                "Scheduled 10/20 09:30: status Scheduled, Note exact, flag cleared, no warning; 10/1 and 10/31 warn; ordinals");

            // ---------- A-025-15 Empty Ready date ----------
            var noReady = new BuyerWalkSheet(Edit(B2Data.Replace("\"ReadyDate\":\"2026-10-15T00:00:00\"", "\"ReadyDate\":null")), false);
            var noReadyWarnings = noReady.SetScheduled(new DateTime(2026, 10, 1, 9, 0, 0));
            noReady.SetStatus(6);
            Check("A-025-15", noReadyWarnings.Count == 0 && noReady.Scheduled == null && noReady.Note == "Changing status to Cancelled.",
                "ReadyDate null: no error, no before-Ready warning; Cancelled clears Scheduled and sets the Note");

            // ---------- A-025-16 Status changes ----------
            var status = new BuyerWalkSheet(Edit(), false);
            status.SetScheduled(new DateTime(2026, 10, 20, 9, 30, 0));
            status.SetStatus(0);
            bool zeroOk = status.Scheduled == null && status.Note == "Changing status to Not Scheduled.";
            status.SetScheduled(new DateTime(2026, 10, 20, 9, 30, 0));
            status.SetStatus(2);
            bool twoOk = status.Scheduled == new DateTime(2026, 10, 20, 9, 30, 0) && status.Note == "Changing status to Scheduled.";
            string noteBefore = status.Note;
            status.SetStatus(5);
            Check("A-025-16", zeroOk && twoOk && status.Note == noteBefore && status.Scheduled != null, "Not Scheduled clears; Scheduled notes only; Finaled changes nothing");

            // ---------- A-025-17 Counters from History ----------
            var c1 = BuyerWalkSheet.CountersFromHistory("Left message (1) … No answer (2) … Left message (3)");
            var c2 = BuyerWalkSheet.CountersFromHistory("Left message (2) … left message (3)");
            var c3 = BuyerWalkSheet.CountersFromHistory("No answer (12)");
            Check("A-025-17", c1 == (3, 4) && c2.LeftMessage == 1 && c3.NoAnswer == 2, $"counters follow the last occurrence: {c1}, {c2}, {c3}");

            // ---------- A-025-18 Counter flag rule and precedence ----------
            var counters = new BuyerWalkSheet(Edit(), false);
            counters.SetNoAnswerCount(3);
            bool first = counters.NoAnswerChecked && counters.Note == "No answer (3)" && counters.Flagged;
            counters.SetLeftMessageChecked(true);
            bool second = counters.Note == "No answer (3)" && !counters.Flagged;
            counters.ToggleFlag();
            Check("A-025-18", first && second && counters.Flagged, "No-answer counter 3 checks its box, notes and flags; Left message keeps No answer's note and clears the flag; the flag toggles");

            // ---------- A-025-19 Update body ----------
            var update = new BuyerWalkSheet(edit, false);
            update.SetScheduled(new DateTime(2026, 10, 20, 9, 30, 0));
            const string Expected = "{\"JobNumber\":\"9900001A\",\"ReadyDate\":\"2026-10-15T05:00:00Z\",\"ClosingDate\":\"2026-10-30T05:00:00Z\",\"BuyerName\":\"Pat Buyer\",\"BuyerEmail\":\"buyer@example.test\",\"BuyerPhone1\":\"(555) 010-0001\",\"BuyerPhone2\":\"\",\"InspectorUsername\":\"tester@example.test\",\"ScheduleDate\":\"10/20/2026 9:30 AM\",\"Requests\":\"\",\"ScheduleStatusId\":2,\"Flagged\":false,\"Note\":\"Scheduled for Oct 20th at 9:30 AM.\",\"Superintendent\":\"Sam Super\",\"SuperintendentPhone\":\"(555) 010-0002\",\"SuperintendentEmail\":\"super@example.test\",\"SuperintendentAsstEmail\":null,\"SalesEmail\":\"\",\"BuyerAgentName\":\"Alex Agent\",\"BuyerAgentEmail\":\"agent@example.test\",\"BuyerAgentPhone\":\"(555) 010-0003\",\"ElectricProvider\":null,\"GasProvider\":null,\"WaterProvider\":null,\"ElectricProviderPhone\":null,\"GasProviderPhone\":null,\"WaterProviderPhone\":null}";
            string body = update.UpdateBody(Chicago);
            string? saved = await service.UpdateAsync(body);
            Check("A-025-19", body == Expected && saved == null && site.Requests.Contains("POST Inspect/BuyerWalk/Update") && site.UpdateBodies.Last() == Expected,
                "POST Inspect/BuyerWalk/Update with exactly the 27-property body (order, nulls, UTC ISO Ready/Closing, M/d/yyyy h:mm a Scheduled)");

            // ---------- A-025-20 Flag survives a save ----------
            var flagged = new BuyerWalkSheet(Edit(B2Data.Replace("\"Flagged\":false", "\"Flagged\":true")), false) { Requests = "Gate code 1234" };
            Check("A-025-20", flagged.UpdateBody(Chicago).Contains("\"Flagged\":true"), "a loaded Flagged true is sent as true (BW-D15)");

            // ---------- A-025-21 Save failures ----------
            async Task<string?> UpdateWith(int status2, string reply, bool network = false, bool htmlTwice = false)
            {
                var (s, m) = Site();
                m.Handler = (method, path) => path == "Inspect/BuyerWalk/Update" ? (htmlTwice ? (200, "<html>login</html>") : (status2, reply)) : (404, "");
                if (network) m.NetworkError = line => line.StartsWith("POST", StringComparison.Ordinal);
                var result = await s.UpdateAsync(Expected);
                return result;
            }
            var (htmlService, htmlSite) = Site();
            htmlSite.Handler = (method, path) => path == "Inspect/BuyerWalk/Update" ? (200, "<html>login</html>") : (404, "");
            string? htmlResult = await htmlService.UpdateAsync(Expected);
            Check("A-025-21", await UpdateWith(500, "") == StrandBuyerWalkService.SaveFailedMessage && await UpdateWith(404, "") == StrandBuyerWalkService.SaveFailedMessage &&
                              await UpdateWith(200, "", network: true) == StrandBuyerWalkService.SaveFailedMessage &&
                              htmlResult == StrandBuyerWalkService.SaveFailedMessage && htmlSite.Logons == 2 && htmlSite.Requests.Count(r => r.StartsWith("POST")) == 2 &&
                              await UpdateWith(200, "") == null && StrandBuyerWalkService.SaveFailedMessage == "Could not save schedule. Please try again." &&
                              tabCode.Contains("if (window.Saved && !_pickerClosed)") && tabCode.Contains("await LoadBuyerWalksAsync();"),
                "500, 404, a network error and an HTML page twice (after a forced W1) fail with INSPECT's text; 200 empty saves and the list reloads");

            // ---------- A-025-22 Cancel ----------
            Check("A-025-22", sheetCode.Contains("_cancel.Click += (_, _) => Close(); // E9: nothing sent") && sheetCode.Contains("Saved = true; // E10"),
                "Cancel closes without B3 and without a reload (Saved stays false; source check)");

            // ---------- A-025-11 Read failure ----------
            var (failService, failSite) = Site();
            failSite.Handler = (method, path) => path.StartsWith("Inspect/BuyerWalk/Read") ? (200, "{\"Success\":false,\"Message\":\"Job not found\",\"Data\":null}") : (404, "");
            string notFound = "", network = "", blank = "";
            try { await failService.ReadAsync("9900001A"); } catch (Exception ex) { notFound = StrandBuyerWalkService.ReadFailureText("9900001A", ex); }
            failSite.NetworkError = line => line.Contains("Read?", StringComparison.Ordinal);
            try { await failService.ReadAsync("9900001A"); } catch (Exception ex) { network = StrandBuyerWalkService.ReadFailureText("9900001A", ex); }
            failSite.NetworkError = _ => false;
            failSite.Handler = (method, path) => (200, "{\"Success\":false,\"Message\":\"\",\"Data\":null}");
            try { await failService.ReadAsync("9900001A"); } catch (Exception ex) { blank = StrandBuyerWalkService.ReadFailureText("9900001A", ex); }
            Check("A-025-11", notFound == "Could not load the buyer walk for 9900001A: Job not found" &&
                              network == "Could not load the buyer walk for 9900001A: No connection could be made (mock)." &&
                              blank == "Could not load the buyer walk for 9900001A." && tabCode.Contains("ShowBuyerWalkAlert(StrandBuyerWalkService.ReadFailureText(job, ex)"),
                "Success false → the server's Message; a network error → the platform message; no sheet opens");

            // ---------- A-025-04 / 23 / 24 Refusals before Create ----------
            var (refuseService, refuseSite) = Site();
            string refuseFolder = NewFolder(root, "refuse");
            var refuseUi = new Ui();
            var refuser = Adder(refuseService, refuseFolder, refuseUi);
            int refusedCopies = await refuser.AddAsync(new[]
            {
                AddRow(rows[1]),
                AddRow(rows[3]),
                new BuyerWalkAddRow { JobNumber = "9900005A", StatusId = 4, Date = DateTime.Now },
                new BuyerWalkAddRow { JobNumber = "9900006A", StatusId = 6, Date = DateTime.Now },
                new BuyerWalkAddRow { JobNumber = "9900007A", StatusId = 2, Date = null }
            });
            Check("A-025-04", refuseUi.Errors.FirstOrDefault() == "Cannot add 9900002A to My List. The buyer walk for has not been scheduled." &&
                              refusedCopies == 0 && BuyerWalkAdder.Summary(0) == "" && !refuseSite.Requests.Any(r => r.Contains("/Create")) &&
                              Directory.GetFiles(Path.Combine(refuseFolder, "MyList")).Length == 0 && addCode.Contains("MessageBox.Show(owner, message, \"\", MessageBoxButton.OK, icon)"),
                "status 0: INSPECT's text with no title, no W2, no file, no summary");
            Check("A-025-23", refuseUi.Errors.Skip(1).Take(3).SequenceEqual(new[]
            {
                "Cannot add 9900004A to My List. The final buyer walk for already occured.",
                "Cannot add 9900005A to My List. The final buyer walk for already occured.",
                "Cannot add 9900006A to My List. The buyer walk request for has been cancelled."
            }), "status 5 and 4: \"already occured\"; 6: \"has been cancelled\"; no W2, no file");
            Check("A-025-24", refuseUi.Errors.ElementAtOrDefault(4) == "Cannot add 9900007A to My List. The schedule date is missing for the buyer walk." &&
                              !refuseSite.Requests.Any(r => r.Contains("/Create")),
                "status 2 with no date: refused before Create (BW-D08)");

            // ---------- A-025-05 / 26 Create uses FormId 67; the new form ----------
            var (addService, addSite) = Site();
            string addFolder = NewFolder(root, "add");
            var addUi = new Ui();
            int copied = await Adder(addService, addFolder, addUi).AddAsync(new[] { AddRow(rows[0]) });
            Check("A-025-05", addSite.Requests.SequenceEqual(new[] { "W1", "GET Inspect/Inspection/Create?jobNumber=9900001A&formId=67" }) &&
                              !addSite.Requests.Any(r => r.Contains("RequestFiles")),
                "W1 then GET Inspect/Inspection/Create?jobNumber=9900001A&formId=67; no RequestFiles, no API host");
            string newFile = Path.Combine(addFolder, "MyList", "9900001-BWT-1-ST.ins");
            var expectedForm = JObject.Load(new JsonTextReader(new StringReader(W2)) { DateParseHandling = DateParseHandling.None });
            var sections = expectedForm.Property("Sections")!;
            sections.AddBeforeSelf(new JProperty("SuperintendentName", "Sam Super"));
            sections.AddBeforeSelf(new JProperty("InspectionFee_Secure", "FAKE-ZERO-FEE"));
            sections.AddBeforeSelf(new JProperty("InspectorOtherFee1_Secure", "FAKE-ZERO-FEE"));
            sections.AddBeforeSelf(new JProperty("InspectorOtherFee2_Secure", "FAKE-ZERO-FEE"));
            byte[] fileBytes = File.Exists(newFile) ? File.ReadAllBytes(newFile) : Array.Empty<byte>();
            Check("A-025-26", copied == 1 && addUi.Errors.Count == 0 && fileBytes.Length > 3 && fileBytes[0] != 0xEF &&
                              Encoding.UTF8.GetString(fileBytes) == expectedForm.ToString(Formatting.None) && BuyerWalkAdder.Summary(1) == "Copied buyer walk to My List." &&
                              !addCode.Contains("UnselectAll") && !addCode.Contains("await LoadBuyerWalksAsync()"),
                $"(copied {copied}, errors {addUi.Errors.Count}, bytes equal {Encoding.UTF8.GetString(fileBytes) == expectedForm.ToString(Formatting.None)}) MyList/9900001-BWT-1-ST.ins = the W2 body with IsPreinspection in place and SuperintendentName + three FAKE-ZERO-FEE before Sections; no reload, selection kept");

            // ---------- A-025-27 / 38 Blank server number is built from the profile's initials ----------
            async Task<string[]> Built(string initials)
            {
                var (s, m) = Site();
                m.Handler = (method, path) => path.Contains("/Create") ? (200, W2.Replace("\"InspectionNumber\":\"9900001-BWT-1-ST\"", "\"InspectionNumber\":\"\"")) : (404, "");
                string folder = NewFolder(root, "built-" + initials.Replace(' ', '-'));
                await Adder(s, folder, new Ui(), initials).AddAsync(new[] { AddRow(rows[0]) });
                return Directory.GetFiles(Path.Combine(folder, "MyList")).Select(Path.GetFileName).ToArray()!;
            }
            var builtSt = await Built("Sample Tester");
            var builtPe = await Built("Pat Example");
            string builtText = File.ReadAllText(Path.Combine(root, "built-Sample-Tester", "MyList", "9900001-BWT-1-ST.ins"));
            Check("A-025-27", builtSt.SequenceEqual(new[] { "9900001-BWT-1-ST.ins" }) && builtText.Contains("\"InspectionNumber\":\"9900001-BWT-1-ST\""),
                "a blank server number is built: 9900001-BWT-1-ST (initials of Sample Tester)");
            Check("A-025-38", builtPe.SequenceEqual(new[] { "9900001-BWT-1-PE.ins" }) &&
                              addCode.IndexOf("ProfileGuard.Require(owner, \"Add to My List\")", StringComparison.Ordinal) > 0 &&
                              addCode.IndexOf("ProfileGuard.Require", StringComparison.Ordinal) < addCode.IndexOf("adder.AddAsync", StringComparison.Ordinal) &&
                              addCode.Contains("InitialsName = profile.InspectorName"),
                "initials come from the profile (Pat Example → PE, not the DisplayName); the F-003 O9 guard runs before any server call (source check)");

            // ---------- A-025-28 Replace in My List ----------
            string replaceFolder = NewFolder(root, "replace");
            string old = Path.Combine(replaceFolder, "MyList", "old-name.ins");
            File.WriteAllText(old, "{\"InspectionNumber\":\"9900001-BWT-1-ST\",\"Sections\":[]}");
            var (replaceService, _) = Site();
            var noUi = new Ui(); noUi.Answers.Enqueue(false);
            int replacedNo = await Adder(replaceService, replaceFolder, noUi).AddAsync(new[] { AddRow(rows[0]) });
            bool noKept = File.Exists(old) && !File.Exists(Path.Combine(replaceFolder, "MyList", "9900001-BWT-1-ST.ins"));
            var yesUi = new Ui(); yesUi.Answers.Enqueue(true);
            int replacedYes = await Adder(replaceService, replaceFolder, yesUi).AddAsync(new[] { AddRow(rows[0]) });
            Check("A-025-28", noUi.Questions.SequenceEqual(new[] { "My List already contains '9900001-BWT-1-ST'. Do you want to replace it?" }) && replacedNo == 0 && noKept && noUi.Errors.Count == 0 &&
                              replacedYes == 1 && !File.Exists(old) && File.Exists(Path.Combine(replaceFolder, "MyList", "9900001-BWT-1-ST.ins")) &&
                              BuyerWalkAdder.Caption == "Cannot Add Buyer Walk" && addCode.Contains("MessageBox.Show(owner, question, BuyerWalkAdder.Caption, MessageBoxButton.YesNo"),
                "INSPECT's question (title Cannot Add Buyer Walk, Yes/No): No changes nothing and shows no error; Yes deletes old-name.ins and creates the new file");

            // ---------- A-025-29 Copy in Outbox or Archive ----------
            async Task<(Ui Ui, int Copied, string Folder)> Other(string where, int copies, bool answer)
            {
                string folder = NewFolder(root, $"other-{where}-{copies}-{answer}");
                for (int i = 0; i < copies; i++)
                    File.WriteAllText(Path.Combine(folder, where, i == 0 ? "9900001-BWT-1-ST.ins" : $"9900001-BWT-1-ST (conflicted {i}).ins"), "{\"InspectionNumber\":\"9900001-BWT-1-ST\",\"Events\":[],\"Sections\":[]}");
                var ui = new Ui(); ui.Answers.Enqueue(answer);
                var (s, _) = Site();
                int n = await Adder(s, folder, ui).AddAsync(new[] { AddRow(rows[0]) });
                return (ui, n, folder);
            }
            var outboxYes = await Other("Outbox", 1, true);
            var outboxNo = await Other("Outbox", 1, false);
            var outboxTwo = await Other("Outbox", 2, true);
            var archiveYes = await Other("Archive", 1, true);
            string moved = Path.Combine(outboxYes.Folder, "MyList", "9900001-BWT-1-ST.ins");
            Check("A-025-29", outboxYes.Ui.Questions.SequenceEqual(new[] { "Outbox already contains '9900001-BWT-1-ST'. Move this copy to My List instead?" }) && outboxYes.Copied == 1 &&
                              File.Exists(moved) && File.ReadAllText(moved) == "{\"InspectionNumber\":\"9900001-BWT-1-ST\",\"Events\":[],\"Sections\":[]}" &&
                              outboxNo.Copied == 0 && outboxNo.Ui.Errors.Count == 0 && File.Exists(Path.Combine(outboxNo.Folder, "Outbox", "9900001-BWT-1-ST.ins")) &&
                              outboxTwo.Ui.Questions.Count == 0 && outboxTwo.Ui.Errors.SingleOrDefault()?.StartsWith("Cannot add to My List. Outbox has 2 files for 9900001-BWT-1-ST (") == true &&
                              archiveYes.Ui.Questions.SequenceEqual(new[] { "Archive already contains '9900001-BWT-1-ST'. Move this copy to My List instead?" }) && archiveYes.Copied == 1,
                "Outbox copy: INSPECT's question; Yes moves it unchanged (counts as copied), No changes nothing; two copies refuse before any question; Archive the same");

            // ---------- A-025-25 / 30 Re-inspection row; batch summary ----------
            var (batchService, batchSite) = Site();
            string batchFolder = NewFolder(root, "batch");
            var batchUi = new Ui();
            bool r1CopiedBeforeR2Alert = false;
            batchUi.OnError = m => { r1CopiedBeforeR2Alert = File.Exists(Path.Combine(batchFolder, "MyList", "9900001-BWT-1-ST.ins")); return m; };
            int batchCopied = await Adder(batchService, batchFolder, batchUi).AddAsync(new[] { AddRow(rows[0]), AddRow(rows[1]), AddRow(rows[2]) });
            Check("A-025-25", batchSite.Requests.Contains("GET Inspect/Reinspection/Create?jobNumber=9900003A&formId=67") &&
                              File.Exists(Path.Combine(batchFolder, "MyList", "9900003-BWT-2-ST.ins")) && rows[2].AddDate == rows[2].Schedule2Date,
                "R3 (re-inspection): the date check uses Schedule2Date and W2 goes to Inspect/Reinspection/Create");
            Check("A-025-30", batchCopied == 2 && batchUi.Errors.SequenceEqual(new[] { "Cannot add 9900002A to My List. The buyer walk for has not been scheduled." }) &&
                              r1CopiedBeforeR2Alert && BuyerWalkAdder.Summary(2) == "Copied 2 buyer walks to My List.",
                "R1, R2, R3: R2's refusal shows when reached (after R1's copy); then \"Copied 2 buyer walks to My List.\"");

            // ---------- A-025-31 Add from the sheet ----------
            var (sheetService, sheetSite) = Site();
            string sheetFolder = NewFolder(root, "sheet");
            var fromSheet = new BuyerWalkSheet(await sheetService.ReadAsync("9900001A"), false);
            fromSheet.SetScheduled(new DateTime(2026, 10, 20, 9, 30, 0));
            sheetSite.Requests.Clear();
            bool sheetSaved = fromSheet.Validate().Count == 0 && await sheetService.UpdateAsync(fromSheet.UpdateBody(Chicago)) == null;
            int sheetCopied = sheetSaved ? await Adder(sheetService, sheetFolder, new Ui()).AddAsync(new[]
                { new BuyerWalkAddRow { JobNumber = "9900001A", IsReinspection = false, StatusId = fromSheet.StatusId ?? 0, Date = fromSheet.Scheduled } }) : 0;
            string fromSheetCode = addCode.Substring(addCode.IndexOf("private async Task AddBuyerWalksFromSheetAsync", StringComparison.Ordinal));
            Check("A-025-31", sheetSaved && sheetCopied == 1 && sheetSite.Requests.SequenceEqual(new[] { "POST Inspect/BuyerWalk/Update", "GET Inspect/Inspection/Create?jobNumber=9900001A&formId=67" }) &&
                              fromSheetCode.IndexOf("BuyerWalkAddGatesAsync(window)", StringComparison.Ordinal) < fromSheetCode.IndexOf("window.SaveAsync(partOfAdd: true)", StringComparison.Ordinal) &&
                              fromSheetCode.IndexOf("window.SaveAsync(partOfAdd: true)", StringComparison.Ordinal) < fromSheetCode.IndexOf("AddBuyerWalksToMyListAsync", StringComparison.Ordinal) &&
                              fromSheetCode.Contains("window.CloseAfterAdd()") && sheetCode.Contains("public void CloseAfterAdd()\n        {\n            Saved = true;".Replace("\n", sheetCode.Contains("\r\n") ? "\r\n" : "\n")),
                "the sheet's Add: gates, then B3, then W2 and the file; it closes and the list reloads (BW-D09); a failed validation or B3 stops before W2 (source order)");

            // ---------- A-025-32 Create failures ----------
            var (createService, createSite) = Site();
            createSite.Handler = (method, path) => path.Contains("/Create") ? (500, "") : (404, "");
            var createUi = new Ui();
            int created500 = await Adder(createService, NewFolder(root, "create500"), createUi).AddAsync(new[] { AddRow(rows[0]) });
            var (htmlCreateService, htmlCreateSite) = Site();
            int calls = 0;
            htmlCreateSite.Handler = (method, path) => path.Contains("/Create") ? (++calls == 1 ? (200, "<html>login</html>") : (200, W2)) : (404, "");
            string htmlCreateFolder = NewFolder(root, "createhtml");
            int createdHtml = await Adder(htmlCreateService, htmlCreateFolder, new Ui()).AddAsync(new[] { AddRow(rows[0]) });
            Check("A-025-32", created500 == 0 && createUi.Errors.SequenceEqual(new[] { "Could not communicate with server." }) && createSite.Requests.Count(r => r.Contains("/Create")) == 2 &&
                              createdHtml == 1 && htmlCreateSite.Logons == 2 && File.Exists(Path.Combine(htmlCreateFolder, "MyList", "9900001-BWT-1-ST.ins")),
                "W2 500 twice → \"Could not communicate with server.\" and no file; an HTML page then JSON after a forced W1 → the file");

            // ---------- A-025-33 List load failure keeps rows ----------
            var (loadService, loadSite) = Site();
            var firstLoad = await loadService.GetBuyerWalksAsync();
            loadSite.Handler = (method, path) => (503, "");
            string loadFailure = "";
            try { await loadService.GetBuyerWalksAsync(); } catch (Exception ex) { loadFailure = StrandBuyerWalkService.LoadFailedText(ex.Message); }
            Check("A-025-33", firstLoad.Count == 4 && loadFailure == "Could not load buyer walks from Strand: The server answered 503." &&
                              StrandBuyerWalkService.LoadedText(4, new DateTime(2026, 10, 18, 9, 0, 0)) == "4 buyer walks. Updated 9:00 AM." &&
                              tabCode.Contains("BuyerWalkStatusText.Text = StrandBuyerWalkService.LoadFailedText(ex.Message); // BW-D01: rows kept"),
                "a 503 on Refresh: \"Could not load buyer walks from Strand: …\" and the rows stay; a load says \"4 buyer walks. Updated 9:00 AM.\"");

            // ---------- A-025-34 / 35 Signed out; no timer ----------
            Check("A-025-34", InspectionPickerWindow.BuyerWalkSignInPrompt == "Sign in to Strand to load buyer walks." &&
                              tabCode.Contains("_ = SignInAndLoadBuyerWalksAsync(trySavedLogin: true, promptIfNeeded: !_buyerWalksLoadedOnce);"),
                "signed out: the sign-in prompt on the first show; Cancel leaves \"Sign in to Strand to load buyer walks.\" (source check; dialog not driven)");
            Check("A-025-35", !tabCode.Contains("DispatcherTimer") && tabCode.Contains("if (StrandScheduleService.IsSignedIn)\n                _ = LoadBuyerWalksAsync();".Replace("\n", tabCode.Contains("\r\n") ? "\r\n" : "\n")),
                "no timer; every show and every Refresh loads once (source check)");

            // ---------- A-025-36 Filters ----------
            var picker = typeof(InspectionPickerWindow);
            var filterType = picker.GetNestedType("OrderColumnFilter", BindingFlags.NonPublic)!;
            var filter = Activator.CreateInstance(filterType, nonPublic: true)!;
            filterType.GetField("Text")!.SetValue(filter, "0003");
            var matches = picker.GetMethod("MatchesColumnFilter", BindingFlags.NonPublic | BindingFlags.Static)!;
            var shown = rows.Where(r => (bool)matches.Invoke(null, new object?[] { r.JobNumber ?? "", filter, false, null })!).Select(r => r.JobNumber).ToList();
            string filterCode = File.ReadAllText(Path.Combine(pl, "InspectionPickerWindow.BuyerWalkFilter.cs"));
            Check("A-025-36", shown.SequenceEqual(new[] { "9900003A" }) && filterCode.Contains("BuyerWalkListView.SelectedItems.Remove(hidden);") &&
                              filterCode.Contains("$\"Filtered: showing {BuyerWalkListView.Items.Count} of {total}\""),
                "Job Contains 0003 shows only R3; hidden rows leave the selection; the Filtered label (shared ORDERS matcher)");

            // ---------- Review fixes ----------
            // §3.3: no forced re-login retry after a non-2xx answer, even an HTML error page, so B3 is never posted twice.
            var (errService, errSite) = Site();
            errSite.Handler = (method, path) => path == "Inspect/BuyerWalk/Update" ? (500, "<html>Server Error</html>") : (404, "");
            string? errResult = await errService.UpdateAsync(Expected);
            Check("§3.3", errResult == StrandBuyerWalkService.SaveFailedMessage && errSite.Requests.Count(r => r.StartsWith("POST")) == 1 && errSite.Logons == 1,
                "a 500 with an HTML error page: one POST, no second login, INSPECT's save-failed text");
            // OC §10.8: an empty form JobNumber still counts (not null); the number keeps the form's Attempt.
            var (emptyJobService, emptyJobSite) = Site();
            emptyJobSite.Handler = (method, path) => path.Contains("/Create")
                ? (200, W2.Replace("\"InspectionNumber\":\"9900001-BWT-1-ST\"", "\"InspectionNumber\":\"\"").Replace("\"JobNumber\":\"9900001A\"", "\"JobNumber\":\"\""))
                : (404, "");
            string emptyJobFolder = NewFolder(root, "emptyjob");
            await Adder(emptyJobService, emptyJobFolder, new Ui()).AddAsync(new[] { AddRow(rows[0]) });
            Check("§10.3", Directory.GetFiles(Path.Combine(emptyJobFolder, "MyList")).Select(Path.GetFileName).SequenceEqual(new[] { "-BWT-1-ST.ins" }),
                "W2 with JobNumber \"\" and a blank number: the built number uses the form's empty JobNumber (OC §10.8), as INSPECT");
            // ---------- A-025-37 Privacy ----------
            string everything = output.ToString();
            Check("A-025-37", !serviceCode.Contains("DiagnosticLogService") && !serviceCode.Contains("Console.") && !tabCode.Contains("DiagnosticLogService") &&
                              !addCode.Contains("DiagnosticLogService") && !sheetCode.Contains("DiagnosticLogService"),
                "the buyer-walk code logs nothing (no password, cookie, W1 URL, body or customer field); the mock records W1 as \"W1\" only");
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine("FAIL harness  " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
