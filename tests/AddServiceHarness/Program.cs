// F-021 Add Service harness (program/features/F-021-add-service-wizard/ACCEPTANCE.md), service level.
//   dotnet run -p:Platform=x64
// A loopback mock Strand website, sandbox Inspections and cache folders under %TEMP%; synthetic jobs, forms and templates.
// The wizard window is driven off-screen only for the page changes (Back/Next reuse their controls) and the job grid's
// columns; its dialogs and the full page flow (A-021-03 to 06, 25) are not driven.
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

return await Harness.Run();

sealed class MockStrand : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    public ConcurrentQueue<string> Requests { get; } = new();
    public Func<string, string, (int Status, string Body)> Handler { get; set; } = (_, _) => (404, "");
    public bool Offline { get; set; }
    public Uri Base { get; }

    public MockStrand()
    {
        _listener.Start();
        Base = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
                _ = Task.Run(() => Serve(client));
            }
        });
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            if (Offline) { client.Client.LingerState = new LingerOption(true, 0); return; }
            var stream = client.GetStream();
            var header = new MemoryStream();
            int b, state = 0;
            while (state < 4 && (b = stream.ReadByte()) >= 0)
            {
                header.WriteByte((byte)b);
                state = (b == '\r' && (state == 0 || state == 2)) || (b == '\n' && (state == 1 || state == 3)) ? state + 1 : 0;
            }
            var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;
            var first = lines[0].Split(' ');
            int length = 0;
            foreach (var line in lines.Skip(1))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[15..].Trim(), out length);
            if (length > 0) { var body = new byte[length]; int read = 0; while (read < length) { int r = await stream.ReadAsync(body.AsMemory(read)); if (r <= 0) break; read += r; } }
            string method = first[0], path = first.Length > 1 ? first[1] : "/";
            Requests.Enqueue(method + " " + path);
            bool logOn = path.StartsWith("/Security/LogOn", StringComparison.OrdinalIgnoreCase);
            var (status, text) = logOn ? (200, "") : Handler(method, path);
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            string extra = logOn ? "Set-Cookie: ASP.NET_SessionId=mock1; path=/\r\nSet-Cookie: .ASPXAUTH=mock2; path=/\r\n" : "";
            string head = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\n{extra}Connection: close\r\n\r\n";
            try { await stream.WriteAsync(Encoding.ASCII.GetBytes(head)); await stream.WriteAsync(bytes); } catch { }
        }
    }

    public void Dispose() { _stop.Cancel(); _listener.Stop(); }
}

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    private static readonly DateTime Now = new(2026, 10, 17, 9, 30, 0);
    private static CompleteProfile Profile(string folder) => new(folder, InspectUserKind.Inspector, "Pat Q Example", "inspector@example.com", "000-000-0000", "TX");

    private static JObject Job(string number, string address) => new()
    {
        ["JobNumber"] = number, ["BuilderAccountId"] = "00000000-0000-0000-0000-0000000000b1", ["BuilderName"] = "Example Homes",
        ["City"] = "Exampleton", ["State"] = "TX", ["Subdivision"] = "Example Park", ["Lot"] = "4", ["Block"] = "2", ["Address"] = address,
        ["Plan"] = "P1", ["BuyerName"] = "Alex Example", ["BuyerPhone1"] = null, ["BuyerPhone2"] = null, ["BuyerEmail"] = "buyer@example.com",
        ["AgentName"] = "Agent Example", ["AgentPhone"] = "0000000000", ["AgentEmail"] = "agent@example.com", ["Superintendent"] = "Super Example",
        ["SuperintendentPhone"] = "0000000000", ["ScheduleDate"] = "2026-10-20T09:00:00", ["ClosingDate"] = "2026-11-01T00:00:00", ["Requests"] = "Gate code 0000",
        ["IsBusy"] = false, ["IsSelfBusy"] = false
    };

    private static JObject Item(int id, string number, string name, string control, object? value = null) => new()
    {
        ["ItemId"] = id, ["Name"] = name, ["Number"] = number, ["ControlName"] = control, ["Value"] = value == null ? null : JToken.FromObject(value),
        ["Comments"] = "", ["ValueList"] = new JArray(), ["Pictures"] = new JArray(), ["Required"] = false
    };

    private static JObject Template(int formId, string code, int serviceId, params JObject[] items) => new()
    {
        ["FormId"] = formId, ["InspectionCode"] = code, ["DocumentTypeId"] = "00000000-0000-0000-0000-0000000000d" + formId % 10, ["ServiceId"] = serviceId,
        ["Name"] = code + " form", ["StatusList"] = new JArray(new JObject { ["Id"] = 0, ["Name"] = "Ordered" }, new JObject { ["Id"] = 3, ["Name"] = "Fail" }),
        ["Sections"] = new JArray(new JObject { ["SectionId"] = 4, ["Name"] = "Main", ["ControlName"] = "SectionStandardControl", ["Number"] = "4", ["Items"] = new JArray(items) })
    };

    private static readonly JArray Templates = new(
        Template(20, "CPP", 50, Item(401, "4.1", "Forms", "PassFailNaNi"), Item(402, "4.2", "Steel", "PassFailNaNi")),
        Template(30, "CPS", 51, Item(301, "4.1", "Superintendent", "SearchLookup")),
        Template(121, "BWT", 121, Item(1, "1.1", "Customer Name", "Text"), Item(2, "1.2", "Customer Phone", "Text"), Item(3, "1.3", "Superintendent", "SearchLookup")));

    private static readonly JArray Forms = new(
        new JObject { ["ServiceId"] = 50, ["FormId"] = 20, ["Name"] = "Concrete Pre Pour", ["Abbreviation"] = "CPP", ["Attempt"] = 1 },
        new JObject { ["ServiceId"] = 51, ["FormId"] = 30, ["Name"] = "Concrete Pour Supers", ["Abbreviation"] = "CPS", ["Attempt"] = 1 },
        new JObject { ["ServiceId"] = 121, ["FormId"] = 121, ["Name"] = "New Home Orientation", ["Abbreviation"] = "BWT", ["Attempt"] = 1 },
        new JObject { ["ServiceId"] = 99, ["FormId"] = 999, ["Name"] = "Unknown", ["Abbreviation"] = "UNK", ["Attempt"] = 1 });

    private static JObject Read(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    private sealed class Box : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "red-f021-" + Guid.NewGuid().ToString("N"));
        public string IF => Path.Combine(Root, "Inspections");
        public Box() { foreach (var f in new[] { "MyList", "Archive", "Outbox", "Review" }) Directory.CreateDirectory(Path.Combine(IF, f)); }
        public string Place(string folder, JObject doc, string? name = null)
        {
            string path = Path.Combine(IF, folder, (name ?? (string)doc["InspectionNumber"]!) + ".ins");
            File.WriteAllText(path, doc.ToString(Formatting.None), new UTF8Encoding(false));
            return path;
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    // InspectCipher is internal to RED; the harness calls the real Decrypt (in memory only, never printed).
    private static string? Decrypt(string cipher) => (string?)typeof(SurgicalSaveService).Assembly.GetType("InspectionEditor.Services.InspectCipher")!
        .GetMethod("Decrypt", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, new object[] { cipher });

    private static AddFormRow Form(int formId) => AddServiceService.FormsFromJson(Forms).First(f => f.FormId == formId);

    public static async Task<int> Run()
    {
        using var mock = new MockStrand();
        var session = StrandWebsiteSession.ForLoopbackMock(mock.Base, () => ("mockuser", "mock-" + Guid.NewGuid().ToString("N")));
        var redLog = new List<string>();
        var job = Job("1234567", "100 Example St");
        var nine = Job("7654321A1", "200 Example St");
        var cachedJobs = new JArray(job, nine);

        // ---------- A-021-01 / 02 Gates ----------
        {
            string root = Path.Combine(Path.GetTempPath(), "red-f021-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(OfflineCacheService.Folder(root));
            File.WriteAllText(OfflineCacheService.PathOf(OfflineCache.Forms, root), Forms.ToString());
            File.WriteAllText(OfflineCacheService.PathOf(OfflineCache.Templates, root), Templates.ToString());
            var at = new DateTime(2026, 10, 17, 0, 1, 0);
            foreach (var c in new[] { OfflineCache.Forms, OfflineCache.Templates }) File.SetLastWriteTime(OfflineCacheService.PathOf(c, root), at.AddMinutes(-1));
            Check("A-021-01", !AddServiceService.CachesReady(at, root) &&
                              AddServiceService.MissingText == "Please connect to WiFi and open your Settings Window, then click the \"Download Offline Data\" button and try again." &&
                              AddServiceService.MissingCaption == "Missing Requirements For Offline Work",
                "Jobs cache missing: refused with INSPECT's text and caption");
            File.WriteAllText(OfflineCacheService.PathOf(OfflineCache.Jobs, root), cachedJobs.ToString());
            File.SetLastWriteTime(OfflineCacheService.PathOf(OfflineCache.Jobs, root), at.AddMinutes(-1));
            File.SetLastWriteTime(OfflineCacheService.PathOf(OfflineCache.Templates, root), new DateTime(2026, 10, 16, 23, 59, 0));
            Check("A-021-02", !AddServiceService.CachesReady(at, root), "a Templates cache from yesterday 23:59 is stale at 00:01");
            Check("A-021-03", AddServiceService.ReceivingText == "Receiving offline work requirements from Strand, please wait..." &&
                              AddServiceService.ReceivingCaption == "Stay Connected To WiFi", "the Receiving text and caption");
            try { Directory.Delete(root, true); } catch { }
        }

        // ---------- A-021-28 / A-021-29 / A-021-30 Add downloads the offline data itself (owner tablet test 2026-10-20) ----------
        {
            Check("A-021-28", AddServiceService.Gate(cachesReady: true, downloading: false, online: false, hasLogin: false) == AddGate.Open &&
                              AddServiceService.Gate(false, downloading: true, online: true, hasLogin: true) == AddGate.Wait &&
                              AddServiceService.Gate(false, false, online: true, hasLogin: true) == AddGate.Download &&
                              AddServiceService.Gate(false, false, online: false, hasLogin: true) == AddGate.Missing &&
                              AddServiceService.Gate(false, false, online: true, hasLogin: false) == AddGate.Missing,
                "fresh: open; a download running: wait; stale, online and a login: download now; offline or no login: the Missing message");

            string root = Path.Combine(Path.GetTempPath(), "red-f021-e2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(OfflineCacheService.Folder(root));
            File.WriteAllText(OfflineCacheService.PathOf(OfflineCache.Forms, root), Forms.ToString());
            File.SetLastWriteTime(OfflineCacheService.PathOf(OfflineCache.Forms, root), DateTime.Now.AddDays(-3)); // stale; Jobs and Templates missing
            var stale = OfflineCacheService.StaleCaches(DateTime.Now, root);
            mock.Handler = (_, path) => path.Contains("/Inspect/Forms/GetOfflineList") ? (200, Forms.ToString())
                : path.Contains("/Inspect/Templates/GetList") ? (200, Templates.ToString())
                : path.Contains("/Inspect/Jobs/GetOfflineList") ? (200, cachedJobs.ToString()) : (404, "");
            int finished = 0;
            Action onFinished = () => finished++;
            OfflineCacheService.DownloadFinished += onFinished;
            var download = OfflineCacheService.RefreshAsync(session, stale, root, redLog.Add);
            bool runningAtOnce = OfflineCacheService.IsDownloading; // the Add button listens before it starts the download
            var result = await download;
            OfflineCacheService.DownloadFinished -= onFinished;
            Check("A-021-29", stale.Count == 3 && runningAtOnce && finished == 1 && !OfflineCacheService.IsDownloading &&
                              result.Saved.Count == 3 && AddServiceService.CachesReady(DateTime.Now, root),
                "stale Forms and missing Jobs/Templates: one download of all three; it counts as running from the start, ends with one DownloadFinished, and the wizard's gate is then open");

            File.SetLastWriteTime(OfflineCacheService.PathOf(OfflineCache.Templates, root), DateTime.Now.AddDays(-1));
            mock.Offline = true;
            var failed = await OfflineCacheService.RefreshAsync(session, OfflineCacheService.StaleCaches(DateTime.Now, root), root, redLog.Add);
            mock.Offline = false;
            Check("A-021-30", failed.Failed.SequenceEqual(new[] { OfflineCache.Templates }) && !AddServiceService.CachesReady(DateTime.Now, root),
                "no connection: the download fails, the gate stays closed and Add shows the Missing message");
            try { Directory.Delete(root, true); } catch { }
        }

        // ---------- A-021-07 / 08 / 09 Search ----------
        {
            mock.Offline = true;
            var online = await AddServiceService.SearchOnlineAsync(session, "100 Example");
            var offline = AddServiceService.SearchOffline(cachedJobs, "100 Example");
            mock.Offline = false;
            Check("A-021-07", online == null && AddServiceService.OfflineSearchText == "Device is not online and cannot contact Strand for most recent jobs.",
                "offline: the online search gives nothing, so RED shows INSPECT's offline message");
            Check("P1", offline.Count == 0, "SPEC P1: the query is not lower-cased, so \"100 Example\" matches no lower-cased Address (ACCEPTANCE now searches \"100 example\")");
            var lower = AddServiceService.SearchOffline(cachedJobs, "100 example");
            Check("A-021-07", lower.Count == 1 && (string?)lower[0]["JobNumber"] == "1234567" && AddServiceService.BlockLot(lower[0]) == "2/4",
                "a lower-case query matches the lower-cased Address: job 1234567, Blk/Lot 2/4");
            Check("A-021-08", AddServiceService.SearchOffline(cachedJobs, "100 EXAMPLE").Count == 0 &&
                              AddServiceService.NotFoundText("100 EXAMPLE") == "No jobs for \"100 EXAMPLE\" were found, we are sorry for the inconvenience." + Environment.NewLine + Environment.NewLine +
                              "Please connect to the internet, close Inspect, and then try again. Contact IT Support if issue persists.",
                "upper case finds nothing; INSPECT's not-found text");
            Check("A-021-09", AddServiceService.SearchOffline(cachedJobs, "100 example").Count == 1 && AddServiceService.SearchOffline(cachedJobs, "1234567").Count == 1,
                "after a blank search, \"100 example\" (and a job number) still match: searching is never locked");
            mock.Handler = (_, path) => path.StartsWith("/Inspect/Jobs/Read") ? (200, new JArray(new JObject { ["JobNumber"] = "1234567", ["Address"] = "100 EXAMPLE ST", ["City"] = " exampleton ", ["Subdivision"] = null }).ToString()) : (404, "");
            var found = await AddServiceService.SearchOnlineAsync(session, "100 Ex&ample");
            Check("P1", found != null && (string?)found[0]["Address"] == "100 Example St" && (string?)found[0]["City"] == "Exampleton" && (string?)found[0]["Subdivision"] == "" &&
                        mock.Requests.Any(r => r == "GET /Inspect/Jobs/Read?searchTerms=100%20Ex%26ample"),
                "online: the query is URL-encoded; Address, City, Subdivision are title-cased (null → \"\")");
            Check("P1", AddServiceService.BlockLot(new JObject { ["Lot"] = "4" }) == "Lot 4" && AddServiceService.BlockLot(new JObject { ["Block"] = "2" }) == "Blk 2" &&
                        AddServiceService.BlockLot(new JObject()) == " ", "Blk/Lot forms");
        }

        // ---------- A-021-10 New inspection header ----------
        using (var box = new Box())
        {
            mock.Handler = (_, _) => (404, "");
            var request = new AddRequest { Job = job, Form = Form(20), Contact = "Sam Example", AttemptText = "1" };
            var doc = await AddServiceService.BuildAsync(request, Profile(box.IF), "Inspector", Templates, session, Now, redLog: redLog.Add);
            string path = AddServiceService.Save(box.IF, doc);
            byte[] raw = File.ReadAllBytes(path);
            var file = Read(path);
            var names = file.Properties().Select(p => p.Name).ToList();
            Check("A-021-10", Path.GetFileName(path) == "1234567-CPP-1-PQE.ins" && raw[0] == (byte)'{' && names.Last() is "Sections" or "Events" or "Attachments" &&
                              names.IndexOf("Sections") > names.IndexOf("InternalNotes"),
                "MyList\\1234567-CPP-1-PQE.ins, UTF-8 without BOM, every header key before Sections");
            Check("A-021-10", (string?)file["JobNumber"] == "1234567" && (string?)file["InspectorName"] == "Pat Q Example" && (string?)file["Address"] == "100 Example St" &&
                              (string?)file["Project"] == "Example Park" && (string?)file["PlanNumber"] == "P1" && (string?)file["State"] == "TX" && (int)file["Attempt"]! == 1 &&
                              (int)file["StatusId"]! == 0 && file["DateInspected"]!.Type == JTokenType.Null && (string?)file["Contact"] == "Sam Example" &&
                              file["Signature"]!.Type == JTokenType.Null && file["InspectVersion"]!.Type == JTokenType.Null && (bool)file["ManuallyEnteredAddress"]! == false &&
                              Guid.TryParse((string?)file["WorkOrderId"], out _) && (string)file["WorkOrderId"]! == ((string)file["WorkOrderId"]!).ToLowerInvariant(),
                "header from the job, profile and page 3; StatusId 0, DateInspected null, InspectVersion null, a new lower-case WorkOrderId");
            Check("A-021-10", new[] { "InspectionFee_Secure", "InspectorOtherFee1_Secure", "InspectorOtherFee2_Secure" }.All(k => Decrypt((string)file[k]!) == 0m.ToString("F2")),
                "the three fees are encrypted 0.00 (checked by decrypting in memory only)");
            var events = (JArray)file["Events"]!;
            Check("A-021-10", events.Count == 1 && (int)events[0]["EventTypeId"]! == 30 && events[0]["AdditionalInfo"]!.Type == JTokenType.Null &&
                              ((JArray)file["Sections"]![0]!["Items"]!).Count == 2,
                "exactly one event 30 with AdditionalInfo null; the template's sections");
        }

        // ---------- A-021-11 / 21 Nine-character job; RequestFiles rule ----------
        using (var box = new Box())
        {
            var formsReq = await AddServiceService.FormsAsync(session, AddServiceService.Job7(nine), Forms);
            var request = new AddRequest { Job = nine, Form = Form(20), AttemptText = "1" };
            var doc = await AddServiceService.BuildAsync(request, Profile(box.IF), "Inspector", Templates, session, Now, redLog: redLog.Add);
            string path = AddServiceService.Save(box.IF, doc);
            Check("A-021-11", mock.Requests.Contains("GET /Inspect/Forms/Read?jobNumber=7654321") && Path.GetFileName(path) == "7654321-CPP-1-PQE.ins" &&
                              (string?)Read(path)["JobNumber"] == "7654321A1" && formsReq.Count == 4,
                "Forms/Read uses job7; the file is 7654321-CPP-1-PQE with JobNumber 7654321A1 (the cache is the fallback)");
            Check("A-021-21", AddServiceService.ShouldRequestFiles(request, "Inspector") && !AddServiceService.ShouldRequestFiles(request, "Preinspector") &&
                              !AddServiceService.ShouldRequestFiles(new AddRequest { Job = null, Form = Form(20) }, "Inspector"),
                "RequestFiles only for a selected job and UserType exactly Inspector (not Preinspector, not after Skip)");
        }

        // ---------- A-021-12 / 13 Manual address ----------
        using (var box = new Box())
        {
            int formsReads = mock.Requests.Count(r => r.StartsWith("GET /Inspect/Forms/Read"));
            var forms = await AddServiceService.FormsAsync(null, null, Forms);
            var doc = await AddServiceService.BuildAsync(new AddRequest { Job = null, Form = Form(20), AttemptText = "1" }, Profile(box.IF), "Inspector", Templates, session, Now, redLog: redLog.Add);
            var file = Read(AddServiceService.Save(box.IF, doc));
            Check("A-021-12", forms.Count == 4 && mock.Requests.Count(r => r.StartsWith("GET /Inspect/Forms/Read")) == formsReads &&
                              (string?)file["InspectionNumber"] == "0000000-CPP-1-PQE" && (string?)file["JobNumber"] == "0000000" && (bool)file["ManuallyEnteredAddress"]! &&
                              (string?)file["State"] == "TX" && new[] { "Address", "City", "BuilderName", "Project", "PlanNumber", "Lot", "Block" }.All(k => file[k]!.Type == JTokenType.Null),
                "Skip: every cached form, no Forms/Read; 0000000-CPP-1-PQE with ManuallyEnteredAddress true, State TX, the address keys null");
            var nho = await AddServiceService.BuildAsync(new AddRequest { Job = null, Form = Form(121), AttemptText = "1" }, Profile(box.IF), "Inspector", Templates, session, Now, redLog: redLog.Add);
            string text = nho.ToString();
            Check("A-021-13", (string?)nho["JobNumber"] == "0000000" && !text.Contains("Alex Example") && !text.Contains("Agent Example") && !text.Contains("Super Example"),
                "Skip after a job: no customer, agent or superintendent values");
        }

        // ---------- A-021-14 / 15 History ----------
        using (var box = new Box())
        {
            var review = new JObject
            {
                ["InspectionNumber"] = "1234567-CPP-1-PQE", ["JobNumber"] = "1234567", ["ServiceId"] = 50, ["Attempt"] = 1, ["StatusId"] = 3,
                ["StatusList"] = new JArray(new JObject { ["Id"] = 0, ["Name"] = "Ordered" }, new JObject { ["Id"] = 3, ["Name"] = "Fail" }),
                ["WorkOrderId"] = "00000000-0000-0000-0000-00000000aaaa", ["DateInspected"] = "2026-10-10T10:00:00-05:00", ["Sections"] = new JArray()
            };
            box.Place("Review", review);
            var forms = AddServiceService.FormsFromJson(Forms);
            AddServiceService.ApplyHistory(forms, box.IF, "1234567", includeReview: true, redLog.Add);
            var cpp = forms.First(f => f.FormId == 20);
            Check("A-021-14", cpp.LastReport == "1234567-CPP-1-PQE" && cpp.LastStatus == "Fail" && cpp.Attempt == 2 && AddServiceService.DefaultPoRequired(cpp),
                "the Review file gives Last Report and Last Status Fail; page 3 proposes Attempt 2 with PO Required");
            var archived = (JObject)review.DeepClone();
            archived["InspectionNumber"] = "7654321-CPP-1-PQE"; archived["JobNumber"] = "7654321A1";
            box.Place("Archive", archived);
            var forms2 = AddServiceService.FormsFromJson(Forms);
            AddServiceService.ApplyHistory(forms2, box.IF, "7654321", includeReview: false, redLog.Add);
            Check("A-021-15", forms2.First(f => f.FormId == 20).LastReport == "7654321-CPP-1-PQE" && forms2.First(f => f.FormId == 20).Attempt == 2,
                "a file that stores the 9-character job is found by its first 7 characters");
        }

        // ---------- A-021-16 Reinspection from a local file ----------
        using (var box = new Box())
        {
            var previous = new JObject
            {
                ["InspectionNumber"] = "1234567-CPP-1-PQE", ["JobNumber"] = "1234567", ["ServiceId"] = 50, ["FormId"] = 20, ["InspectionCode"] = "CPP", ["Attempt"] = 1,
                ["WorkOrderId"] = "00000000-0000-0000-0000-00000000bbbb", ["Signature"] = "/9j/AA==", ["NextActionId"] = 1, ["NoCharge"] = true, ["StatusId"] = 3,
                ["Address"] = "100 Example St",
                ["Sections"] = new JArray(new JObject { ["SectionId"] = 4, ["Name"] = "Main", ["Items"] = new JArray(Item(401, "4.1", "Forms", "PassFailNaNi", "Pass"), Item(402, "4.2", "Steel", "PassFailNaNi", "Fail")) }),
                ["Events"] = new JArray(new JObject { ["EventTypeId"] = 30 }, new JObject { ["EventTypeId"] = 31 }),
                ["Attachments"] = new JArray(new JObject { ["Id"] = "00000000-0000-0000-0000-0000000000a1", ["Filename"] = "a.pdf" }, new JObject { ["Id"] = "00000000-0000-0000-0000-0000000000a2", ["Filename"] = "b.pdf" })
            };
            string prior = box.Place("Archive", previous);
            var form = Form(20);
            form.LastWorkOrderId = "00000000-0000-0000-0000-00000000bbbb"; form.LastLocalPath = prior; form.Attempt = 2;
            var doc = await AddServiceService.BuildAsync(new AddRequest { Job = job, Form = form, Reinspection = true, AttemptText = "2", PurchaseOrderRequired = false },
                Profile(box.IF), "Inspector", Templates, session, Now, redLog: redLog.Add);
            var file = Read(AddServiceService.Save(box.IF, doc));
            var ids = ((JArray)file["Attachments"]!).Select(a => (string?)a["Id"]).ToList();
            Check("A-021-16", (string?)file["InspectionNumber"] == "1234567-CPP-2-PQE" && (string?)file["Sections"]![0]!["Items"]![1]!["Value"] == "Fail" &&
                              file["Signature"]!.Type == JTokenType.Null && (bool)file["IsReinspection"]! && (int)file["Attempt"]! == 2 && (int)file["NextActionId"]! == 0 &&
                              (int)file["StatusId"]! == 0 && (bool)file["NoCharge"]! && (bool)file["PurchaseOrderRequired"]! == false &&
                              (string?)file["WorkOrderId"] != "00000000-0000-0000-0000-00000000bbbb" && ids.Count == 2 && !ids.Contains("00000000-0000-0000-0000-0000000000a1") &&
                              ((JArray)file["Events"]!).Count == 1 && (int)file["Events"]![0]!["EventTypeId"]! == 30,
                "item 4.2 Fail kept; Signature null; IsReinspection, Attempt 2, NextActionId 0, StatusId 0, NoCharge kept, PO false; new WorkOrderId and attachment Ids; one event 30");
        }

        // ---------- A-021-17 / 18 Reinspection from the server ----------
        using (var box = new Box())
        {
            var form = Form(20);
            form.LastWorkOrderId = "00000000-0000-0000-0000-00000000cccc"; form.Attempt = 3;
            mock.Handler = (_, path) => path.StartsWith("/Inspect/Reinspection/Fetch2") ? (404, "") : (404, "");
            var doc = await AddServiceService.BuildAsync(new AddRequest { Job = job, Form = form, Reinspection = true, AttemptText = "3" }, Profile(box.IF), "Inspector", Templates, session, Now, redLog: redLog.Add);
            Check("A-021-17", mock.Requests.Contains("GET /Inspect/Reinspection/Fetch2/?jobNumber=1234567&workOrderId=00000000-0000-0000-0000-00000000cccc") &&
                              (bool)doc["IsReinspection"]! && (int)doc["Attempt"]! == 3 && ((JArray)doc["Sections"]![0]!["Items"]!).Count == 2,
                "Fetch2 404: the template with IsReinspection true and Attempt 3");
            int logOns = mock.Requests.Count(r => r.Contains("/Security/LogOn"));
            mock.Handler = (_, path) => path.StartsWith("/Inspect/Reinspection/Fetch2") ? (200, "<html>log on</html>") : (404, "");
            var html = await AddServiceService.BuildAsync(new AddRequest { Job = job, Form = form, Reinspection = true, AttemptText = "3" }, Profile(box.IF), "Inspector", Templates, session, Now, redLog: redLog.Add);
            Check("A-021-18", mock.Requests.Count(r => r.Contains("/Security/LogOn")) > logOns && (bool)html["IsReinspection"]! && ((JArray)html["Sections"]![0]!["Items"]!).Count == 2,
                "Fetch2 HTML twice: one more logon, one retry, then the template; no exception");
        }

        // ---------- A-021-19 / 20 Duplicates ----------
        using (var box = new Box())
        {
            box.Place("Archive", new JObject { ["InspectionNumber"] = "1234567-CPP-1-PQE" });
            var duplicates = AddServiceService.FindDuplicates(box.IF, "1234567-CPP-1-PQE");
            Check("A-021-19", duplicates.Count == 1 && AddServiceService.DuplicateQuestion(duplicates[0].Number, duplicates[0].FolderCaption) ==
                              "1234567-CPP-1-PQE inspection already exists in your Archive. Would you like to remove it?" &&
                              AddServiceService.DuplicateRefused == "Cannot create inspection as it already exists on device. Remove the duplicate inspections or change the Attempt number.",
                "the Archive copy is found by file name; INSPECT's question and RED's refusal text");
            box.Place("Review", new JObject { ["InspectionNumber"] = "7654321-CPP-1-PQE", ["JobNumber"] = "7654321" });
            var review = AddServiceService.FindDuplicates(box.IF, "7654321-CPP-1-PQE");
            Check("A-021-20", review.Count == 1 && review[0].FolderCaption == "Review list", "a Review copy of the 7-character job is found for job 7654321A1 (file name only)");
            var again = await AddServiceService.BuildAsync(new AddRequest { Job = job, Form = Form(20), AttemptText = "1" }, Profile(box.IF), "Inspector", Templates, session, Now, redLog: redLog.Add);
            File.WriteAllText(Path.Combine(box.IF, "MyList", "1234567-CPP-1-PQE.ins"), "{\"existing\":true}");
            bool refused = false;
            try { AddServiceService.Save(box.IF, again); } catch (IOException) { refused = true; }
            Check("S2", refused && File.ReadAllText(Path.Combine(box.IF, "MyList", "1234567-CPP-1-PQE.ins")) == "{\"existing\":true}",
                "the save is CreateNew: an existing My List file is never overwritten");
        }

        // ---------- A-021-22 Requests text and date warning ----------
        Check("A-021-22", AddServiceService.RequestsText(true, false, true, " gate code ") == "Call in advance, take photos. gate code" &&
                          AddServiceService.RequestsText(false, false, false, "  ") == "" &&
                          AddServiceService.DateWarning(Now.AddDays(7), Now) == "You have scheduled this inspection 7 day(s) in the future." &&
                          AddServiceService.DateWarning(Now.AddDays(-2), Now) == "You have scheduled this inspection 2 day(s) in the past." &&
                          AddServiceService.DateWarning(Now.AddDays(6), Now) == null,
            "D5 text; D3 warnings at +7 and -2 days, none at +6");

        // ---------- A-021-23 NHO with no buyer phone ----------
        {
            var (contact, phone, other) = AddServiceService.NhoDefaults(job, 121);
            Check("A-021-23", contact == "Alex Example" && phone == "" && other == "Gate code 0000" && AddServiceService.NhoDefaults(job, 50) == ("", "", ""),
                "Contact from the buyer, an empty phone with no error, Other from the job's Requests");
            Check("D1", AddServiceService.FormatPhone("5550100100") == "(555) 010-0100" && !AddServiceService.DefaultPoRequired(new AddFormRow { ServiceId = 121, Attempt = 3 }),
                "10 digits are formatted as typed; ServiceId 121 never presets PO Required");
        }

        // ---------- A-021-24 Superintendents ----------
        using (var box = new Box())
        {
            mock.Handler = (_, path) => path.StartsWith("/Inspect/Jobs/GetSupers") ? (200, "[\"Zed Example\",\"Amy Example\"]") : (404, "");
            var doc = await AddServiceService.BuildAsync(new AddRequest { Job = job, Form = Form(30), AttemptText = "1" }, Profile(box.IF), "Inspector", Templates, session, Now, redLog: redLog.Add);
            var list = ((JArray)doc["Sections"]![0]!["Items"]![0]!["ValueList"]!).Select(v => (string)v!).ToList();
            Check("A-021-24", mock.Requests.Contains("GET /Inspect/Jobs/GetSupers?jobNumber=1234567") && list.SequenceEqual(new[] { "Amy Example", "Zed Example" }),
                "GetSupers names sorted and appended to the SUPERINTENDENT item's ValueList");
            mock.Handler = (_, _) => (500, "");
            var failed = await AddServiceService.BuildAsync(new AddRequest { Job = job, Form = Form(30), AttemptText = "1" }, Profile(box.IF), "Inspector", Templates, session, Now, redLog: redLog.Add);
            Check("A-021-24", failed["InspectionNumber"] != null, "a failing GetSupers still builds the inspection");
        }

        // ---------- N5 New Home Orientation extras ----------
        using (var box = new Box())
        {
            mock.Handler = (_, _) => (404, "");
            var doc = await AddServiceService.BuildAsync(new AddRequest { Job = job, Form = Form(121), AttemptText = "1", Requests = "" }, Profile(box.IF), "Inspector", Templates, session, Now, redLog: redLog.Add);
            var items = (JArray)doc["Sections"]![0]!["Items"]!;
            Check("N5", (string?)items[0]["Value"] == "Alex Example" && (string?)doc["SuperintendentName"] == "Super Example" && (string?)items[2]["Value"] == "Super Example" &&
                        (int)doc["StatusId"]! == 0 && !((string?)doc["Requests"] ?? "").Contains("Scheduled for"),
                "customer and superintendent prefilled; no schedule prefix (the job has ScheduleDate, INSPECT reads Schedule1Date)");
        }

        // ---------- A-021-26 Unknown FormId ----------
        using (var box = new Box())
        {
            string message = "";
            try { await AddServiceService.BuildAsync(new AddRequest { Job = job, Form = Form(999), AttemptText = "1" }, Profile(box.IF), "Inspector", Templates, session, Now, redLog: redLog.Add); }
            catch (InvalidOperationException ex) { message = ex.Message; }
            Check("A-021-26", message == "Cannot find template for form id: 999." && Directory.GetFiles(Path.Combine(box.IF, "MyList")).Length == 0,
                "\"Cannot find template for form id: 999.\" and nothing written");
        }

        // ---------- A-021-27 Privacy ----------
        Check("A-021-27", !redLog.Any(l => l.Contains("1234567") || l.Contains("Example St") || l.Contains("Alex Example") || l.Contains("Agent Example") || l.Contains("Super Example")),
            "no job number, address, buyer, agent or superintendent value in RED's log lines");

        // ---------- Review fix: Back and Next reuse the pages; the Job column shows the job number ----------
        {
            string? failure = null;
            string jobPath = "", formNamePath = "";
            var ui = new Thread(() =>
            {
                try
                {
                    var window = new InspectionEditor.AddServiceWindow(Profile(Path.GetTempPath()), "Inspector", StrandWebsiteSession.ForLoopbackMock(new Uri("http://127.0.0.1:9/"), () => (null, null)));
                    var goTo = typeof(InspectionEditor.AddServiceWindow).GetMethod("GoTo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    foreach (int page in new[] { 1, 3, 1, 3, 1 })
                        goTo.Invoke(window, new object[] { page, false }); // the same controls are shown again without a second parent
                    var jobs = (System.Windows.Controls.ListView)typeof(InspectionEditor.AddServiceWindow).GetField("_jobs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
                    var column = ((System.Windows.Controls.GridView)jobs.View).Columns.Last();
                    jobPath = ((System.Windows.Data.Binding)column.DisplayMemberBinding).Path.Path;
                    var formsList = (System.Windows.Controls.ListView)typeof(InspectionEditor.AddServiceWindow).GetField("_formsList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!;
                    formNamePath = ((System.Windows.Data.Binding)((System.Windows.Controls.GridView)formsList.View).Columns[0].DisplayMemberBinding).Path.Path;
                    window.Close();
                }
                catch (Exception ex) { failure = (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message; }
            });
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            ui.Join();
            Check("W1", failure == null && jobPath == "JobNumber",
                $"pages 1 and 3 shown again after Back and Next without an error; the Job column binds the job number ({failure ?? jobPath})");
            Check("DUP-4", failure == null && formNamePath == nameof(AddFormRow.DisplayName),
                $"page 2's Name column shows the labelled name ({failure ?? formNamePath})");
        }

        // ---------- Owner fix list 2026-10-19: forms with the same name (HERS Energy Rough, FormId 96 and 299) ----------
        {
            var sameName = new JArray(
                new JObject { ["ServiceId"] = 306, ["FormId"] = 96, ["Name"] = "HERS Energy Rough", ["Abbreviation"] = "HER", ["Attempt"] = 1 },
                new JObject { ["ServiceId"] = 305, ["FormId"] = 95, ["Name"] = "HERS Energy Final", ["Abbreviation"] = "HEF", ["Attempt"] = 1 },
                new JObject { ["ServiceId"] = 306, ["FormId"] = 299, ["Name"] = "HERS Energy Rough ", ["Abbreviation"] = "HER", ["Attempt"] = 1 });
            var rows = AddServiceService.FormsFromJson(sameName);
            Check("DUP-1", rows.Count == 3 &&
                           rows.Select(r => r.DisplayName).SequenceEqual(new[] { "HERS Energy Rough  (form 96)", "HERS Energy Final", "HERS Energy Rough  (form 299)" }) &&
                           rows[0].Name == "HERS Energy Rough" && rows[2].Name == "HERS Energy Rough ",
                "same-named forms (trailing space ignored) show their form number; none is hidden; Name, which the new file gets, is unchanged");
            Check("DUP-2", AddServiceService.FormsFromJson(Forms).All(r => r.DisplayName == r.Name), "a name that occurs once is shown unchanged");
            var chooser = FormChoiceLabels.Labels(new List<(string, int)> { ("HER - HERS Energy Rough", 96), ("HER - HERS Energy Rough", 299), ("HET - HERS Energy Testing", 263) });
            Check("DUP-3", chooser.SequenceEqual(new[] { "HER - HERS Energy Rough  (form 96)", "HER - HERS Energy Rough  (form 299)", "HET - HERS Energy Testing" }),
                "the labels for ORDERS' Choose Form list");

            // The real ORDERS Choose Form window (internal): its radio buttons show the labels and keep each form's id.
            string? chooserFailure = null;
            var shown = new List<(string Text, int Id)>();
            int chosen = -2;
            var chooserUi = new Thread(() =>
            {
                try
                {
                    var forms = new List<StrandFormOption> { new() { Id = 96, Name = "HER - HERS Energy Rough" }, new() { Id = 299, Name = "HER - HERS Energy Rough" } };
                    var type = typeof(AddServiceService).Assembly.GetType("InspectionEditor.OrderFormChooserWindow")!;
                    var window = (System.Windows.Window)Activator.CreateInstance(type, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                        null, new object[] { forms, "HER - HERS Energy Rough" }, null)!;
                    var radios = ((List<System.Windows.Controls.RadioButton>)type.GetField("_options", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!);
                    foreach (var radio in radios) shown.Add(((string)radio.Content, (int)radio.Tag));
                    radios[1].IsChecked = true;
                    chosen = (int)type.GetProperty("ChosenFormId")!.GetValue(window)!;
                    window.Close();
                }
                catch (Exception ex) { chooserFailure = (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message; }
            });
            chooserUi.SetApartmentState(ApartmentState.STA);
            chooserUi.Start();
            chooserUi.Join();
            Check("DUP-5", chooserFailure == null && chosen == 299 &&
                           shown.SequenceEqual(new[] { ("HER - HERS Energy Rough  (form 96)", 96), ("HER - HERS Energy Rough  (form 299)", 299) }),
                $"OrderFormChooserWindow shows the labels and returns the picked form's id ({chooserFailure ?? "ok"})");
        }

        Console.WriteLine();
        Console.WriteLine($"F-021 harness: {_passed} passed, {_failed} failed");        return _failed == 0 ? 0 : 1;
    }
}
