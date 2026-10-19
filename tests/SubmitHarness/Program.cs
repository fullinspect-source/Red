// F-013 Submit harness (program/features/F-013-submit-outbox/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// Sandbox Inspections folders under %TEMP% and a loopback mock Strand website (a tiny HTTP server on 127.0.0.1) only.
// Never touches Dropbox\Inspections or live Strand. Credentials are generated per run and never stored.
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

return await Harness.Run();

sealed record MockRequest(string Method, string PathAndQuery, Dictionary<string, string> Headers, byte[] Body);

// V3a: scripted replies per path; records every request.
sealed class MockStrand : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    public ConcurrentQueue<MockRequest> Requests { get; } = new();
    public Func<MockRequest, (int Status, string ContentType, byte[] Body, int DelayMs)> Handler { get; set; } = _ => (404, "text/plain", Array.Empty<byte>(), 0);
    public bool RefuseAll { get; set; }
    public Uri Base { get; }

    public MockStrand()
    {
        _listener.Start();
        Base = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            if (RefuseAll) { client.Client.LingerState = new LingerOption(true, 0); return; }
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
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1)) { int c = line.IndexOf(':'); if (c > 0) headers[line[..c].Trim()] = line[(c + 1)..].Trim(); }
            byte[] body = Array.Empty<byte>();
            if (headers.TryGetValue("Content-Length", out var len) && int.TryParse(len, out int n) && n > 0)
            {
                body = new byte[n];
                int read = 0;
                while (read < n) { int r = await stream.ReadAsync(body.AsMemory(read, n - read)); if (r <= 0) break; read += r; }
            }
            var request = new MockRequest(first[0], first.Length > 1 ? first[1] : "/", headers, body);
            Requests.Enqueue(request);
            var (status, type, responseBody, delay) = Handler(request);
            if (delay > 0) { try { await Task.Delay(delay, _stop.Token); } catch { return; } }
            var extra = request.PathAndQuery.StartsWith("/Security/LogOn", StringComparison.OrdinalIgnoreCase)
                ? "Set-Cookie: ASP.NET_SessionId=mock1; path=/\r\nSet-Cookie: .ASPXAUTH=mock2; path=/\r\n" : "";
            var head = $"HTTP/1.1 {status} X\r\nContent-Type: {type}\r\nContent-Length: {responseBody.Length}\r\n{extra}Connection: close\r\n\r\n";
            try
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(responseBody);
            }
            catch { }
        }
    }

    public void Dispose() { _stop.Cancel(); _listener.Stop(); }

    public static (int, string, byte[], int) Json(string json, int delay = 0) => (200, "application/json", Encoding.UTF8.GetBytes(json), delay);
}

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    private const string Username = "inspector@example.com";

    private static CompleteProfile Profile(string folder, InspectUserKind kind = InspectUserKind.Inspector, string name = "Pat Q Example") =>
        new(folder, kind, name, Username, "000-000-0000", "TX");

    private static JObject Item(int id, string number, string name, string control, object? value, string? comments = "", int pictures = 0,
        bool required = false, bool copied = false) => new()
    {
        ["ItemId"] = id, ["Name"] = name, ["Number"] = number, ["ControlName"] = control, ["ControlFormatString"] = null,
        ["ItemGroupId"] = "", ["ItemGroupControlName"] = null, ["PictureSizeId"] = 0, ["IsCopied"] = copied, ["Hide"] = false,
        ["DisplayLabel"] = number + " " + name, ["MaxIncidents"] = 0, ["CommentList"] = new JArray(), ["ValueList"] = new JArray(),
        ["SortOrder"] = 0, ["Value"] = value == null ? JValue.CreateNull() : JToken.FromObject(value), ["ResultSortOrder"] = 0,
        ["Comments"] = comments, ["Incidents"] = null,
        ["Pictures"] = new JArray(Enumerable.Range(0, pictures).Select(_ => new JObject { ["Path"] = null, ["Caption"] = "Picture", ["Description"] = null, ["Image"] = "/9j/AA==" })),
        ["Required"] = required, ["AlwaysPrint"] = false, ["OnlyPrint"] = "", ["Template"] = null, ["ButtonText"] = null,
        ["HideCommentsButton"] = false, ["HidePicturesButton"] = false, ["HideAddButton"] = false, ["IsPictureRequired"] = false
    };

    // fixture-valid: a complete, signed CPP inspection for job 1234567 in the INSPECT-local dialect.
    private static JObject Fixture(string number = "1234567-CPP-1-PQE", string job = "1234567", int attempt = 1)
    {
        var doc = new JObject
        {
            ["InspectVersion"] = null, ["WorkOrderId"] = "00000000-0000-0000-0000-000000000001", ["ServiceId"] = 1, ["FormId"] = 20,
            ["IsTransfer"] = true, ["IsPreinspection"] = false, ["IsApproval"] = false, ["NoCharge"] = false, ["NoChargeReason"] = null,
            ["NextActionId"] = 0, ["NextActionText"] = null, ["InspectionName"] = "Concrete Pre Pour", ["InspectionNumber"] = number,
            ["InspectionCode"] = "CPP", ["Attempt"] = attempt, ["StatusId"] = 2,
            ["StatusList"] = new JArray(new JObject { ["Id"] = 2, ["Name"] = "Passed" }, new JObject { ["Id"] = 3, ["Name"] = "Failed" }),
            ["PlanNumber"] = "P1", ["BuilderName"] = "Example Builder", ["BuilderAccountId"] = "00000000-0000-0000-0000-000000000002",
            ["Contact"] = "Example Contact", ["ContactNumber"] = "000-000-0000", ["JobNumber"] = job, ["Project"] = "Example Project",
            ["Address"] = "100 Example St", ["Lot"] = "1", ["Block"] = "A", ["City"] = "Example City", ["State"] = "TX",
            ["InspectorUsername"] = Username, ["InspectorName"] = "Pat Q Example", ["InspectorPhone"] = "000-000-0000",
            ["SubmittorUserName"] = null, ["DateInspected"] = "2026-10-17T10:00:00-05:00", ["TimePeriodInspected"] = "",
            ["StructureTypeId"] = 0, ["CreatedDate"] = "2026-10-01T08:00:00-05:00", ["LastModifiedDate"] = "2026-10-01T08:00:00-05:00",
            ["LastDBDate"] = null, ["Version"] = 0, ["ErrorMessage"] = null, ["LastModifiedUsername"] = null, ["IsReinspection"] = false,
            ["PurchaseOrderRequired"] = false, ["PurchaseOrderNumber"] = "", ["RequisitionNumber"] = null, ["SuperintendentName"] = null,
            ["Subcontractor"] = null, ["ManuallyEnteredAddress"] = false, ["Requests"] = null, ["WhoNotified"] = null, ["WhenNotified"] = null,
            ["Signature"] = "/9j/AA==", ["DocumentTypeId"] = "00000000-0000-0000-0000-000000000003",
            ["InspectionFee_Secure"] = "PLACEHOLDERCIPHERTEXTA0==", ["InspectorOtherFee1_Secure"] = null, ["InspectorOtherFee2_Secure"] = null,
            ["OtherFeeDescription1"] = null, ["OtherFeeDescription2"] = null, ["InternalNotes"] = null,
            ["Sections"] = new JArray(
                new JObject { ["SectionId"] = 1, ["Name"] = "Administrative", ["ControlName"] = "Simple", ["Number"] = "1",
                    ["Items"] = new JArray(Item(101, "1.1", "Superintendent", "SearchLookup", "Super A")) },
                new JObject { ["SectionId"] = 5, ["Name"] = "Final", ["ControlName"] = "SectionStandardControl", ["Number"] = "5",
                    ["Items"] = new JArray(Item(501, "5.1", "Cleanup", "PassFailNaNi", "Pass", required: true)) }),
            ["Events"] = new JArray(
                new JObject { ["Date"] = "2026-10-01T08:00:00-05:00", ["EventTypeId"] = 30, ["Username"] = Username, ["UserDisplayName"] = "Pat Q Example", ["AdditionalInfo"] = null },
                new JObject { ["Date"] = "2026-10-17T09:00:00-05:00", ["EventTypeId"] = 40, ["Username"] = Username, ["UserDisplayName"] = "Pat Q Example", ["AdditionalInfo"] = "Inspect Version 2026.1012.2" }),
            ["Attachments"] = new JArray()
        };
        return doc;
    }

    private static JObject Read(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "red-f013-" + Guid.NewGuid().ToString("N"));
        public string IF => Path.Combine(Root, "Inspections");
        public string MyList => Path.Combine(IF, "MyList");
        public string Outbox => Path.Combine(IF, "Outbox");
        public string Archive => Path.Combine(IF, "Archive");
        public string Local => Path.Combine(Root, "RedLocal");
        public Sandbox(bool withFolders = true)
        {
            Directory.CreateDirectory(MyList);
            if (withFolders) { Directory.CreateDirectory(Outbox); Directory.CreateDirectory(Archive); }
            Directory.CreateDirectory(Local);
        }
        public string Place(JObject doc, string? name = null)
        {
            string path = Path.Combine(MyList, (name ?? (string)doc["InspectionNumber"]!) + ".ins");
            File.WriteAllText(path, doc.ToString(Formatting.None), new UTF8Encoding(false));
            return path;
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    private sealed class ListLog : ISubmitLog
    {
        public List<string> Lines { get; } = new();
        public void Inspect(string line) => Lines.Add("I " + line);
        public void Red(string line) => Lines.Add("R " + line);
    }

    private static async Task<(SubmitResult Result, SurgicalSaveService Saver, InspectionFile Model, ListLog Log)> Submit(Sandbox box, string path,
        InspectUserKind kind = InspectUserKind.Inspector, string? rawType = "Inspector", Action<InspectionFile, SurgicalSaveService>? edit = null,
        bool openAttachment = false, IStrandWebsite? website = null, IEnumerable<(string, string?)>? workingCopies = null)
    {
        var saver = new SurgicalSaveService();
        var model = saver.Load(path);
        edit?.Invoke(model, saver);
        var log = new ListLog();
        var ctx = new SubmitContext
        {
            Model = model, Saver = saver, SourcePath = path, Profile = Profile(box.IF, kind), RawUserType = rawType,
            HasOpenAttachment = () => openAttachment, LocalRoot = box.Local, Log = log, Website = website, Sleep = _ => { },
            WorkingCopies = workingCopies == null ? null : () => workingCopies
        };
        var result = await SubmitService.SubmitAsync(ctx);
        return (result, saver, model, log);
    }

    public static async Task<int> Run()
    {
        // ---------- A-013-01 / 02 / 03 File placement, stamps, dialect ----------
        using (var box = new Sandbox())
        {
            string path = box.Place(Fixture());
            var (result, _, _, firstLog) = await Submit(box, path);
            string archive = Path.Combine(box.Archive, "1234567-CPP-1-PQE.ins"), outbox = Path.Combine(box.Outbox, "1234567-CPP-1-PQE.ins");
            Check("A-013-01", result.Stage == SubmitStage.Done && File.Exists(archive) && File.Exists(outbox) && !File.Exists(path) &&
                              File.ReadAllBytes(archive).SequenceEqual(File.ReadAllBytes(outbox)) &&
                              Directory.GetFiles(box.Outbox).Length == 1 && Directory.GetFiles(box.Archive).Length == 1,
                $"Archive and Outbox hold identical bytes, My List copy gone, nothing else ({result.Stage}: {result.Message})");
            Check("A-013-01", result.Message == "Sent to Outbox. Strand usually picks it up within a few minutes.", "step 17 text");
            var sent = Read(outbox);
            Check("A-013-02", (string?)sent["SubmittorUserName"] == Username && sent["IsTransfer"]!.Value<bool>() == false &&
                              (int)sent["Version"]! == 0 && sent["LastDBDate"]!.Type == JTokenType.Null &&
                              ((JArray)sent["Events"]!).Select(e => (int)e["EventTypeId"]!).SequenceEqual(new[] { 30, 40 }) &&
                              !File.ReadAllText(outbox).Contains("SubmitDate") && !File.ReadAllText(outbox).Contains("\"Hash\"") &&
                              !File.ReadAllText(outbox).Contains("RedPlanCheck") && !File.ReadAllText(outbox).Contains("ItemResultId"),
                "SubmittorUserName stamped, IsTransfer false, Version/LastDBDate/events unchanged, no extra keys");
            byte[] raw = File.ReadAllBytes(outbox);
            Check("A-013-03", sent.Properties().Select(p => p.Name).SequenceEqual(InspectProjectionWriter.TopLevelKeys) &&
                              raw[0] == (byte)'{' && raw[^1] == (byte)'}' && !(raw[0] == 0xEF && raw[1] == 0xBB),
                $"INSPECT's {InspectProjectionWriter.TopLevelKeys.Count} top-level keys in order; no BOM, no trailing newline");
            Check("A-013-10", (string?)sent["SuperintendentName"] == "Super A", "SuperintendentName copied from the Superintendent item");
            Check("A-013-14", !firstLog.Lines.Any(l => l.Contains("/Inspect/Inspection/Submit")) && firstLog.Lines.Contains("I Submitting inspection 1234567-CPP-1-PQE"),
                "the Inspector path makes no website request (no upload line in the log)");
            Check("L7", firstLog.Lines.Contains("I Checking for open attachments...") && firstLog.Lines.Contains("I    No open attachments found.") &&
                        firstLog.Lines.Contains("I    Saving inspection before submit.") &&
                        firstLog.Lines.Contains($"I Copied inspection 1234567-CPP-1-PQE to {archive}") &&
                        firstLog.Lines.Contains($"I Copied inspection 1234567-CPP-1-PQE to {outbox}") &&
                        firstLog.Lines.Contains($"I Removed inspection 1234567-CPP-1-PQE from {path}"),
                "F-019 L7 Submit lines: attachment check, save, both copies and the removal");
        }

        // A-013-03 from the server dialect (69 keys).
        using (var box = new Sandbox())
        {
            var server = Fixture();
            foreach (var key in new[] { "ElectricProvider", "GasProvider", "WaterProvider", "Rejected", "ErrorCount", "Filename" })
                server[key] = key is "Rejected" ? false : key is "ErrorCount" ? 0 : null;
            string path = box.Place(server);
            var (result, _, _, _) = await Submit(box, path);
            var sent = Read(Path.Combine(box.Outbox, "1234567-CPP-1-PQE.ins"));
            Check("A-013-03", result.Succeeded && sent.Properties().Select(p => p.Name).SequenceEqual(InspectProjectionWriter.TopLevelKeys),
                "a server-format source lands in the INSPECT-local dialect");
        }

        // ---------- A-013-04 Invalid inspection is not moved ----------
        using (var box = new Sandbox())
        {
            var doc = Fixture(); doc["Signature"] = null;
            string path = box.Place(doc);
            var (result, _, _, _) = await Submit(box, path);
            var after = Read(path);
            Check("A-013-04", result.Stage == SubmitStage.Invalid && result.Message == InspectionValidator.SubmitRefused &&
                              result.Problems!.Blocking.Any(p => p.Text == "Signature required.") &&
                              (string?)after["SubmittorUserName"] == Username && Directory.GetFiles(box.Outbox).Length == 0 &&
                              Directory.GetFiles(box.Archive).Length == 0,
                "refused with INSPECT's text and the problem list; My List re-saved with SubmittorUserName only; nothing moved");
        }

        // ---------- A-013-05 / 06 / 07 Blank duplicate rows ----------
        using (var box = new Sandbox())
        {
            var doc = Fixture();
            var final = (JArray)doc["Sections"]![1]!["Items"]!;
            final.Clear();
            final.Add(Item(501, "5.1", "Cleanup", "PassFailNaNi", "Fail", comments: "Debris"));
            final.Add(Item(501, "5.1", "Cleanup", "PassFailNaNi", "Pass", copied: true));
            final.Add(Item(501, "5.1", "Cleanup", "PassFailNaNi", "Pass", copied: true));
            final.Add(Item(502, "5.2", "Forms", "PassFailNaNi", "Pass"));
            final.Add(Item(502, "5.2", "Forms", "PassFailNaNi", "Fail", pictures: 1, copied: true));
            final.Add(Item(503, "5.3", "Stakes", "PassFailNaNi", "Pass"));
            string path = box.Place(doc);
            var (result, _, _, _) = await Submit(box, path);
            var items = (JArray)Read(Path.Combine(box.Outbox, "1234567-CPP-1-PQE.ins"))["Sections"]![1]!["Items"]!;
            var i501 = items.Where(i => (int)i["ItemId"]! == 501).ToList();
            var i502 = items.Where(i => (int)i["ItemId"]! == 502).ToList();
            Check("A-013-05", result.Succeeded && i501.Count == 1 && (string?)i501[0]["Value"] == "Fail" && (int)i501[0]["ResultSortOrder"]! == 0,
                "two blank Pass copies removed; the Fail item stays with ResultSortOrder 0");
            Check("A-013-06", i502.Count == 1 && (string?)i502[0]["Value"] == "Fail", "a blank original goes when its copy has content");
            Check("A-013-07", items.Any(i => (int)i["ItemId"]! == 503), "a single Pass item stays");
        }

        // ---------- A-013-08 Open attachment guard ----------
        using (var box = new Sandbox())
        {
            string path = box.Place(Fixture());
            var (result, _, _, _) = await Submit(box, path, openAttachment: true);
            Check("A-013-08", result.Stage == SubmitStage.OpenAttachment && result.Message == "You have an open attachment. Please save and close it before continuing." &&
                              File.Exists(path) && Directory.GetFiles(box.Outbox).Length == 0 && Directory.GetFiles(box.Archive).Length == 0,
                "open attachment: INSPECT's message, nothing moved");
        }

        // ---------- A-013-09 / 11 / 25 Attachments ----------
        using (var box = new Sandbox())
        {
            var doc = Fixture(); doc["Attempt"] = 2; doc["InspectionNumber"] = "1234567-CPP-2-PQE";
            ((JArray)doc["Attachments"]!).Add(new JObject { ["InspectVersion"] = null, ["Id"] = "00000000-0000-0000-0000-00000000000a", ["EditPath"] = null, ["FileData"] = "JVBERi0=", ["FileType"] = "pdf", ["Filename"] = "Reference.pdf", ["AnnotatedPages"] = new JArray(), ["IncludedPages"] = new JArray(), ["ReferenceOnly"] = true, ["LastWriteTime"] = "0001-01-01T00:00:00", ["CanExcludePages"] = true });
            ((JArray)doc["Attachments"]!).Add(new JObject { ["InspectVersion"] = null, ["Id"] = "00000000-0000-0000-0000-00000000000b", ["EditPath"] = null, ["FileData"] = "JVBERi0=", ["FileType"] = "pdf", ["Filename"] = "1234567 Plans - insp 1-ZZ.pdf", ["AnnotatedPages"] = new JArray(), ["IncludedPages"] = new JArray(), ["ReferenceOnly"] = false, ["LastWriteTime"] = "0001-01-01T00:00:00", ["CanExcludePages"] = true });
            ((JArray)doc["Attachments"]!).Add(new JObject { ["InspectVersion"] = null, ["Id"] = "00000000-0000-0000-0000-00000000000c", ["EditPath"] = null, ["FileData"] = "JVBERi0=", ["FileType"] = "pdf", ["Filename"] = "Other.pdf", ["AnnotatedPages"] = new JArray(), ["IncludedPages"] = new JArray(), ["ReferenceOnly"] = false, ["LastWriteTime"] = "0001-01-01T00:00:00", ["CanExcludePages"] = false });
            string engineering = Path.Combine(box.IF, "Jobs", "1234567", "Engineering");
            Directory.CreateDirectory(engineering);
            string working = Path.Combine(box.Root, "working-plans.pdf");
            File.WriteAllBytes(working, Encoding.ASCII.GetBytes("%PDF-1.4 working copy"));
            string path = box.Place(doc, "1234567-CPP-2-PQE");
            var (result, _, _, _) = await Submit(box, path, workingCopies: new (string, string?)[] { ("1234567 Plans - insp 1-ZZ.pdf", working), ("Other.pdf", null) });
            var sent = Read(Path.Combine(box.Outbox, "1234567-CPP-2-PQE.ins"));
            var names = ((JArray)sent["Attachments"]!).Select(a => (string?)a["Filename"]).ToList();
            Check("A-013-09", result.Succeeded && !names.Contains("Reference.pdf") && names.Contains("1234567 Plans - insp 1-ZZ.pdf"),
                "reference-only attachment dropped, the others kept");
            string stamped = Path.Combine(engineering, "1234567 Plans - insp 2-PQE.pdf");
            Check("A-013-11", File.Exists(stamped) && File.ReadAllBytes(stamped).SequenceEqual(File.ReadAllBytes(working)) &&
                              Directory.GetFiles(engineering).Length == 1,
                "the working copy is copied as \"1234567 Plans - insp 2-PQE.pdf\"; Other.pdf (no working copy) is not copied");
            Check("A-013-11", SubmitService.StampedAttachmentName("1234567 Plans - insp 1-ZZ.pdf", 2, "PQE") == "1234567 Plans - insp 2-PQE.pdf" &&
                              SubmitService.StampedAttachmentName("Plan.pdf", 1, "PQE") == "Plan - insp 1-PQE.pdf", "stamped name rule");
        }
        using (var box = new Sandbox())
        {
            string engineering = Path.Combine(box.IF, "Jobs", "1234567", "Engineering");
            Directory.CreateDirectory(engineering);
            File.WriteAllText(Path.Combine(engineering, "Plan - insp 1-PQE.pdf"), "existing");
            var doc = Fixture();
            ((JArray)doc["Attachments"]!).Add(new JObject { ["Id"] = "00000000-0000-0000-0000-00000000000b", ["FileData"] = "JVBERi0=", ["FileType"] = "pdf", ["Filename"] = "Plan.pdf", ["ReferenceOnly"] = false, ["CanExcludePages"] = true });
            string working = Path.Combine(box.Root, "w.pdf"); File.WriteAllText(working, "new");
            string path = box.Place(doc);
            await Submit(box, path, workingCopies: new (string, string?)[] { ("Plan.pdf", working) });
            Check("A-013-11", File.ReadAllText(Path.Combine(engineering, "Plan - insp 1-PQE.pdf")) == "existing", "an existing stamped file is not overwritten");
        }
        using (var box = new Sandbox())
        {
            var doc = Fixture(job: "1234567A9");
            ((JArray)doc["Attachments"]!).Add(new JObject { ["Id"] = "00000000-0000-0000-0000-00000000000b", ["FileData"] = "JVBERi0=", ["FileType"] = "pdf", ["Filename"] = "Plan.pdf", ["ReferenceOnly"] = false });
            string working = Path.Combine(box.Root, "w.pdf"); File.WriteAllText(working, "new");
            string path = box.Place(doc);
            var (result, _, _, log) = await Submit(box, path, workingCopies: new (string, string?)[] { ("Plan.pdf", working) });
            Check("A-013-25", result.Succeeded && !Directory.Exists(Path.Combine(box.IF, "Jobs")) &&
                              log.Lines.Contains("R Engineering folder missing for 1234567A9; attachment copy skipped (as INSPECT)"),
                "no Jobs folder is created and the RED-only line is logged; A-013-01 holds");
        }

        // ---------- A-013-12 Archive replaced with a local backup ----------
        using (var box = new Sandbox())
        {
            string archive = Path.Combine(box.Archive, "1234567-CPP-1-PQE.ins");
            File.WriteAllText(archive, "{\"old\":\"server copy\"}");
            string path = box.Place(Fixture());
            var (result, _, _, _) = await Submit(box, path);
            string backups = Path.Combine(box.Local, SubmitService.SubmitBackupsFolder);
            Check("A-013-12", result.Succeeded && File.ReadAllBytes(archive).SequenceEqual(File.ReadAllBytes(Path.Combine(box.Outbox, "1234567-CPP-1-PQE.ins"))) &&
                              Directory.GetFiles(backups).Any(f => File.ReadAllText(f) == "{\"old\":\"server copy\"}"),
                "the Archive file holds the new bytes; the old bytes are in RED's SubmitBackups");
        }

        // ---------- A-013-13 / 24 Atomic Outbox placement; staging never in Dropbox ----------
        using (var box = new Sandbox())
        {
            var inOutbox = new ConcurrentBag<(string Name, long Size)>();
            var inMyList = new ConcurrentBag<string>();
            using var outWatch = new FileSystemWatcher(box.Outbox) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
            using var myWatch = new FileSystemWatcher(box.MyList) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
            void Record(string full) { try { inOutbox.Add((Path.GetFileName(full), File.Exists(full) ? new FileInfo(full).Length : -1)); } catch { inOutbox.Add((Path.GetFileName(full), -2)); } }
            outWatch.Created += (_, e) => Record(e.FullPath); outWatch.Changed += (_, e) => Record(e.FullPath); outWatch.Renamed += (_, e) => Record(e.FullPath);
            myWatch.Created += (_, e) => inMyList.Add(e.Name ?? ""); myWatch.Renamed += (_, e) => inMyList.Add(e.Name ?? "");
            outWatch.EnableRaisingEvents = true; myWatch.EnableRaisingEvents = true;
            string path = box.Place(Fixture());
            var (result, _, _, _) = await Submit(box, path);
            await Task.Delay(300);
            long finalSize = new FileInfo(Path.Combine(box.Outbox, "1234567-CPP-1-PQE.ins")).Length;
            Check("A-013-13", result.Succeeded && inOutbox.All(s => s.Name == "1234567-CPP-1-PQE.ins") &&
                              inOutbox.Where(s => s.Size >= 0).All(s => s.Size == finalSize),
                $"only 1234567-CPP-1-PQE.ins was ever seen in Outbox, always at its final size ({string.Join(", ", inOutbox.Select(s => s.Name).Distinct())})");
            // My List shows only F-002's own save sidecars (".red-<guid>.tmp", ".red-save-backup-*"), never an Outbox staging name.
            Check("A-013-24", !inMyList.Any(n => n.Contains(".red-outbox.tmp")) && !inOutbox.Any(s => s.Name.EndsWith(".tmp")),
                "no Outbox staging name ever appears in MyList or Outbox (staging is in RED's local folder on the same drive)");
        }

        // ---------- A-013-15 Outbox failure leaves My List intact ----------
        using (var box = new Sandbox(withFolders: false))
        {
            Directory.CreateDirectory(box.Archive);
            File.WriteAllText(box.Outbox, "not a folder");
            string path = box.Place(Fixture());
            var (result, _, _, _) = await Submit(box, path);
            Check("A-013-15", result.Stage == SubmitStage.OutboxFailed && result.Message.StartsWith("Cannot submit inspection. Move to Outbox folder failed. ") &&
                              File.Exists(path) && File.Exists(Path.Combine(box.Archive, "1234567-CPP-1-PQE.ins")),
                $"Outbox unusable: INSPECT's message; My List and Archive copies exist ({result.Message})");
        }

        // ---------- A-013-19 RED sidecars leave My List ----------
        using (var box = new Sandbox())
        {
            string path = box.Place(Fixture());
            string sidecar = path + ".red-save-backup-" + Guid.NewGuid().ToString("N") + ".completed";
            File.WriteAllText(sidecar, "backup");
            await Submit(box, path);
            Check("A-013-19", !File.Exists(sidecar) && Directory.GetFiles(Path.Combine(box.Local, SubmitService.SubmitBackupsFolder)).Any(f => f.EndsWith(".completed")),
                "backup sidecars move from My List to RED's local backup folder");
        }

        // ---------- A-013-21 Missing Outbox and Archive folders are created ----------
        using (var box = new Sandbox(withFolders: false))
        {
            string path = box.Place(Fixture());
            var (result, _, _, _) = await Submit(box, path);
            Check("A-013-21", result.Succeeded && File.Exists(Path.Combine(box.Outbox, "1234567-CPP-1-PQE.ins")) && File.Exists(Path.Combine(box.Archive, "1234567-CPP-1-PQE.ins")),
                "both folders are created and A-013-01 holds");
        }

        // ---------- A-013-22 Attempt changed in Top before submit ----------
        using (var box = new Sandbox())
        {
            string path = box.Place(Fixture());
            var (result, _, _, _) = await Submit(box, path, edit: (m, s) =>
            {
                s.SetHeaderValue("Attempt", new JValue(2));
                s.SetHeaderValue("InspectionNumber", new JValue(HeaderRules.BuildInspectionNumber("1234567", "CPP", 2, "Pat Q Example", false)));
            });
            Check("A-013-22", result.Succeeded && File.Exists(Path.Combine(box.Outbox, "1234567-CPP-2-PQE.ins")) &&
                              File.Exists(Path.Combine(box.Archive, "1234567-CPP-2-PQE.ins")) && !File.Exists(path) &&
                              (string?)Read(Path.Combine(box.Outbox, "1234567-CPP-2-PQE.ins"))["InspectionNumber"] == "1234567-CPP-2-PQE",
                "Outbox and Archive hold 1234567-CPP-2-PQE.ins; the old-named My List file is gone");
        }

        // ---------- A-013-23 Deferred delete is conditional ----------
        using (var box = new Sandbox())
        {
            string path = box.Place(Fixture());
            var saver = new SurgicalSaveService();
            var model = saver.Load(path);
            SubmitResult result;
            using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // blocks the delete
            {
                var ctx = new SubmitContext { Model = model, Saver = saver, SourcePath = path, Profile = Profile(box.IF), RawUserType = "Inspector", LocalRoot = box.Local, Log = new ListLog(), Sleep = _ => { } };
                // The final write needs the file; release just before step 14 is not possible from here, so emulate step 14 alone.
                result = new SubmitResult();
            }
            byte[] submitted = File.ReadAllBytes(path);
            using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Check("A-013-23", !SubmitService.RemoveSource(path, submitted, box.Local, _ => { }) && SubmitService.PendingPaths(box.Local).Count == 1,
                    "a locked My List copy becomes a pending delete");
            File.WriteAllText(path, "{\"changed\":true}");
            var messages = SubmitService.ProcessPendingDeletes(box.Local);
            Check("A-013-23", File.Exists(path) && messages.SequenceEqual(new[] { "The My List copy of 1234567-CPP-1-PQE changed after it was submitted. RED left it in place." }),
                "changed bytes: the file stays and RED says so");
            File.WriteAllBytes(path, submitted);
            using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                SubmitService.RemoveSource(path, submitted, box.Local, _ => { });
            SubmitService.ProcessPendingDeletes(box.Local);
            Check("A-013-23", !File.Exists(path), "unchanged bytes: deleted at the next launch");

            // RED safety: a pending delete is forgotten when RED writes that path again (unsubmit Move, Sync download).
            File.WriteAllBytes(path, submitted);
            using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                SubmitService.RemoveSource(path, submitted, box.Local, _ => { });
            SubmitService.ForgetPendingDelete(path, box.Local);
            SubmitService.ProcessPendingDeletes(box.Local);
            Check("SAFE", File.Exists(path) && SubmitService.PendingPaths(box.Local).Count == 0, "a path written again is not deleted at the next launch");
            File.WriteAllText(path, "{\"edited\":1}");
            Check("SAFE", !SubmitService.RemoveSource(path, submitted, box.Local, _ => { }) && File.Exists(path),
                "step 14 never deletes a My List copy whose bytes changed after the submit");
            File.Delete(path);
            Check("SAFE", !SubmitService.IsSafeReportName(@"..\x") && !SubmitService.IsSafeReportName("a/b") && !SubmitService.IsSafeReportName("..") &&
                          SubmitService.IsSafeReportName("1234567-CPP-1-PQE"), "an inspection number with a path in it is never used as a file name");
        }

        // ---------- Preinspector (A-013-27 to 31) and Outbox kinds (A-013-32) ----------
        string user = "pat+" + Random.Shared.Next(1000, 9999) + "@example.com";
        string password = "p&w+" + Guid.NewGuid().ToString("N")[..6] + " x";
        using (var mock = new MockStrand())
        {
            StrandWebsiteSession NewSession() => StrandWebsiteSession.ForLoopbackMock(mock.Base, () => (user, password));
            string accepted = "{\"Success\":true,\"Message\":null,\"Data\":null}";

            // A-013-27 / 31
            using (var box = new Sandbox())
            {
                var doc = Fixture(number: "1234567-CPP-P-1-PQE");
                string path = box.Place(doc);
                mock.Handler = r => r.PathAndQuery.StartsWith("/Security/LogOn") ? MockStrand.Json("{}") : r.PathAndQuery == "/Inspect/Inspection/Submit" ? MockStrand.Json(accepted) : (404, "text/plain", Array.Empty<byte>(), 0);
                var (result, _, _, log) = await Submit(box, path, InspectUserKind.Preinspector, "Preinspector", website: new StrandWebsiteUploader(NewSession()));
                var requests = mock.Requests.ToList();
                var post = requests.SingleOrDefault(r => r.PathAndQuery == "/Inspect/Inspection/Submit");
                string archive = Path.Combine(box.Archive, "1234567-CPP-P-1-PQE.ins");
                string bodyText = post == null ? "" : Encoding.UTF8.GetString(post.Body);
                bool onePart = post != null && System.Text.RegularExpressions.Regex.Matches(bodyText, "Content-Disposition").Count == 1 &&
                               bodyText.Contains("Content-Disposition: form-data; name=\"inspectionFile\"; filename=\"1234567-CPP-P-1-PQE.ins\"\r\nContent-Type: application/octet-stream\r\n\r\n") &&
                               post.Headers.TryGetValue("Cookie", out var cookie) && cookie.Contains("mock1") && cookie.Contains("mock2");
                byte[] archived = File.Exists(archive) ? File.ReadAllBytes(archive) : Array.Empty<byte>();
                bool bytesMatch = post != null && IndexOf(post.Body, archived) >= 0;
                Check("A-013-27", result.Stage == SubmitStage.Done && result.Message == "Submitted to Strand." && onePart && bytesMatch &&
                                  !File.Exists(path) && Directory.GetFiles(box.Outbox).Length == 0 &&
                                  Read(archive)["IsPreinspection"]!.Value<bool>() && !((JArray)Read(archive)["Events"]!).Any(e => (int)e["EventTypeId"]! == 31),
                    $"one multipart POST with the session cookies, bytes = Archive copy, IsPreinspection true, My List gone, Outbox empty ({result.Message})");
                var logon = requests.FirstOrDefault(r => r.PathAndQuery.StartsWith("/Security/LogOn"));
                var query = logon == null ? null : System.Web.HttpUtility.ParseQueryString(new Uri(mock.Base, logon.PathAndQuery).Query);
                Check("A-013-31", logon != null && requests.IndexOf(logon) < requests.IndexOf(post!) && query!["UserName"] == user && query["Password"] == password &&
                                  !log.Lines.Any(l => l.Contains(password) || l.Contains(user) || l.Contains("mock1")),
                    "LogOn before the POST with exactly the encoded test values; neither value nor a cookie is logged");
            }

            // A-013-28 rejected
            while (mock.Requests.TryDequeue(out _)) { }
            using (var box = new Sandbox())
            {
                string path = box.Place(Fixture(number: "1234567-CPP-P-1-PQE"));
                mock.Handler = r => r.PathAndQuery.StartsWith("/Security/LogOn") ? MockStrand.Json("{}") : MockStrand.Json("{\"Success\":false,\"Message\":\"Mock rejection\",\"Data\":null}");
                var (result, _, _, _) = await Submit(box, path, InspectUserKind.Preinspector, "Preinspector", website: new StrandWebsiteUploader(NewSession()));
                string archive = Path.Combine(box.Archive, "1234567-CPP-P-1-PQE.ins");
                Check("A-013-28", result.Stage == SubmitStage.WebsiteRejected && result.Message == "Cannot submit inspection. Cannot submit to server. Mock rejection" &&
                                  File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(archive)) && Directory.GetFiles(box.Outbox).Length == 0 &&
                                  !Directory.Exists(Path.Combine(box.IF, "Jobs")),
                    "rejected: INSPECT's message; My List and Archive hold the same bytes; Outbox empty; nothing copied");
            }

            // A-013-29 (b) login page, (c) HTTP 500, (d) delay past the timeout
            foreach (var (label, handler) in new (string, Func<MockRequest, (int, string, byte[], int)>)[]
            {
                ("login page", r => r.PathAndQuery.StartsWith("/Security/LogOn") ? MockStrand.Json("{}") : (200, "text/html", Encoding.UTF8.GetBytes("<html>Log on</html>"), 0)),
                ("HTTP 500", r => r.PathAndQuery.StartsWith("/Security/LogOn") ? MockStrand.Json("{}") : (500, "text/plain", Array.Empty<byte>(), 0)),
            })
            {
                while (mock.Requests.TryDequeue(out _)) { }
                using var box = new Sandbox();
                string path = box.Place(Fixture(number: "1234567-CPP-P-1-PQE"));
                mock.Handler = handler;
                var session = NewSession();
                var (result, _, _, _) = await Submit(box, path, InspectUserKind.Preinspector, "Preinspector", website: new StrandWebsiteUploader(session));
                int posts = mock.Requests.Count(r => r.PathAndQuery == "/Inspect/Inspection/Submit");
                Check("A-013-29", result.Stage == SubmitStage.WebsiteNoAnswer && result.Message == SubmitService.WebsiteNoAnswerMessage &&
                                  File.Exists(path) && File.Exists(Path.Combine(box.Archive, "1234567-CPP-P-1-PQE.ins")) && posts == 1,
                    $"{label}: \"Strand's answer could not be read...\"; files stay; exactly one POST");
                if (label == "login page")
                {
                    while (mock.Requests.TryDequeue(out _)) { }
                    mock.Handler = r => r.PathAndQuery.StartsWith("/Security/LogOn") ? MockStrand.Json("{}") : MockStrand.Json(accepted);
                    await new StrandWebsiteUploader(session).UploadInspectionAsync("/Inspect/Inspection/Submit", "x.ins", new byte[] { 1 }, default);
                    Check("A-013-29", mock.Requests.First().PathAndQuery.StartsWith("/Security/LogOn"), "after a login page the next upload logs on again first");
                }
            }
        }
        // (a) connection refused: a closed port
        {
            using var box = new Sandbox();
            string path = box.Place(Fixture(number: "1234567-CPP-P-1-PQE"));
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            var dead = StrandWebsiteSession.ForLoopbackMock(new Uri($"http://127.0.0.1:{port}/"), () => ("u@example.com", "p"));
            var (result, _, _, _) = await Submit(box, path, InspectUserKind.Preinspector, "Preinspector", website: new StrandWebsiteUploader(dead));
            Check("A-013-29", result.Stage == SubmitStage.WebsiteNotSent && result.Message == SubmitService.WebsiteNotSentMessage && File.Exists(path),
                "connection refused: \"RED could not reach Strand...\"; the file stays");
        }
        // A-013-30 not signed in
        {
            using var box = new Sandbox();
            string path = box.Place(Fixture(number: "1234567-CPP-P-1-PQE"));
            string before = Sha(path);
            var none = StrandWebsiteSession.ForLoopbackMock(new Uri("http://127.0.0.1:9/"), () => (null, null));
            var (result, _, _, _) = await Submit(box, path, InspectUserKind.Preinspector, "Preinspector", website: new StrandWebsiteUploader(none));
            Check("A-013-30", result.Stage == SubmitStage.WebsiteSignInNeeded && result.Message == "Sign in to Strand first. As a Preinspector, Submit uploads the inspection to Strand." &&
                              Sha(path) == before && Directory.GetFiles(box.Archive).Length == 0, "no credentials: refused before anything changes");
        }
        // A-013-32 Outbox kinds make no HTTP call
        foreach (var (raw, kind) in new[] { ("NonDropBoxInspector", InspectUserKind.NonDropBox), ("PreInspector", InspectUserKind.Other), ("Field Tech", InspectUserKind.Other) })
        {
            using var box = new Sandbox();
            string path = box.Place(Fixture());
            var (result, _, _, _) = await Submit(box, path, kind, raw);
            var sent = Read(Path.Combine(box.Outbox, "1234567-CPP-1-PQE.ins"));
            string expected = kind == InspectUserKind.NonDropBox ? "Sent to Outbox. Press Sync in the Outbox to send it to Strand." : SubmitService.SentToOutboxMessage;
            Check("A-013-32", result.Succeeded && result.Message == expected && sent["IsPreinspection"]!.Value<bool>() == false &&
                              (kind != InspectUserKind.Other || SubmitService.OtherKindNotice(raw) == $"INSPECT user type '{raw}' is not one INSPECT offers. RED submits through the Outbox, as INSPECT does."),
                $"{raw}: Outbox path, no website, IsPreinspection false, \"{result.Message}\"");
        }

        // ---------- Sync (A-013-34 to 43) ----------
        await SyncChecks(user, password);

        Console.WriteLine();
        Console.WriteLine($"F-013 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0) return -1;
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    private static async Task SyncChecks(string user, string password)
    {
        using var mock = new MockStrand();
        StrandWebsiteSession NewSession() => StrandWebsiteSession.ForLoopbackMock(mock.Base, () => (user, password));
        string Empty = "[]";
        void Reset() { while (mock.Requests.TryDequeue(out _)) { } }

        // A-013-34 offline
        using (var box = new Sandbox())
        {
            File.WriteAllText(Path.Combine(box.Outbox, "1111111-CPP-1-PQE.ins"), Fixture(number: "1111111-CPP-1-PQE").ToString(Formatting.None));
            string before = string.Join(",", Directory.GetFiles(box.IF, "*", SearchOption.AllDirectories).Select(Sha));
            mock.RefuseAll = true;
            var report = await StrandSyncService.SyncAsync(new SyncOptions { InspectionsFolder = box.IF, Session = NewSession(), LocalRoot = box.Local, ReadInspectLastSyncDate = _ => null });
            mock.RefuseAll = false;
            Check("A-013-34", report.Stopped && report.Headline == $"Cannot connect to {mock.Base}. Please check your internet connection and try again." &&
                              string.Join(",", Directory.GetFiles(box.IF, "*", SearchOption.AllDirectories).Select(Sha)) == before,
                $"offline: INSPECT's message with the configured base; nothing changed ({report.Headline})");
        }

        // A-013-35 uploads the Outbox
        Reset();
        using (var box = new Sandbox())
        {
            var originals = new Dictionary<string, byte[]>();
            foreach (var n in new[] { "1111111", "2222222", "3333333" })
            {
                string name = $"{n}-CPP-1-PQE.ins";
                File.WriteAllText(Path.Combine(box.Outbox, name), Fixture(number: $"{n}-CPP-1-PQE", job: n).ToString(Formatting.None), new UTF8Encoding(false));
                originals[name] = File.ReadAllBytes(Path.Combine(box.Outbox, name));
            }
            mock.Handler = r =>
            {
                if (r.PathAndQuery.StartsWith("/Security/LogOn") || r.PathAndQuery == "/") return MockStrand.Json("{}");
                if (r.PathAndQuery == "/Inspect/Inspection/NormalSubmit")
                    return Encoding.UTF8.GetString(r.Body).Contains("2222222-CPP-1-PQE.ins\"") ? (200, "text/html", Encoding.UTF8.GetBytes("<html></html>"), 0)
                                                                                               : MockStrand.Json("{\"Success\":true}");
                if (r.PathAndQuery.StartsWith("/Inspect/Sync/GetFileList")) return MockStrand.Json(Empty);
                return (404, "text/plain", Array.Empty<byte>(), 0);
            };
            var report = await StrandSyncService.SyncAsync(new SyncOptions { InspectionsFolder = box.IF, Session = NewSession(), LocalRoot = box.Local, ReadInspectLastSyncDate = _ => null });
            var posts = mock.Requests.Where(r => r.PathAndQuery == "/Inspect/Inspection/NormalSubmit").Select(r => Encoding.UTF8.GetString(r.Body)).ToList();
            bool order = posts.Count == 3 && posts[0].Contains("1111111-CPP-1-PQE.ins\"") && posts[1].Contains("2222222-CPP-1-PQE.ins\"") && posts[2].Contains("3333333-CPP-1-PQE.ins\"");
            Check("A-013-35", order && File.ReadAllBytes(Path.Combine(box.Archive, "1111111-CPP-1-PQE.ins")).SequenceEqual(originals["1111111-CPP-1-PQE.ins"]) &&
                              File.ReadAllBytes(Path.Combine(box.Archive, "3333333-CPP-1-PQE.ins")).SequenceEqual(originals["3333333-CPP-1-PQE.ins"]) &&
                              !File.Exists(Path.Combine(box.Outbox, "1111111-CPP-1-PQE.ins")) && !File.Exists(Path.Combine(box.Outbox, "3333333-CPP-1-PQE.ins")) &&
                              File.ReadAllBytes(Path.Combine(box.Outbox, "2222222-CPP-1-PQE.ins")).SequenceEqual(originals["2222222-CPP-1-PQE.ins"]) &&
                              report.Headline == "Sync complete. 2 inspections submitted." && report.Problems.Contains("0 downloaded, 0 uploaded.") &&
                              report.Problems.Contains("2222222-CPP-1-PQE.ins: Strand did not answer; it stays in the Outbox."),
                $"three NormalSubmit in name order; 1 and 3 archived and removed; 2 stays ({report.Text.Replace(Environment.NewLine, " | ")})");
        }

        // A-013-36 stops on a rejection
        Reset();
        using (var box = new Sandbox())
        {
            foreach (var n in new[] { "1111111", "2222222", "3333333" })
                File.WriteAllText(Path.Combine(box.Outbox, $"{n}-CPP-1-PQE.ins"), Fixture(number: $"{n}-CPP-1-PQE", job: n).ToString(Formatting.None));
            mock.Handler = r => r.PathAndQuery.StartsWith("/Security/LogOn") || r.PathAndQuery == "/" ? MockStrand.Json("{}") :
                                r.PathAndQuery == "/Inspect/Inspection/NormalSubmit" ? MockStrand.Json("{\"Success\":false,\"Message\":\"Mock rejection\"}") : MockStrand.Json(Empty);
            var report = await StrandSyncService.SyncAsync(new SyncOptions { InspectionsFolder = box.IF, Session = NewSession(), LocalRoot = box.Local, ReadInspectLastSyncDate = _ => null });
            Check("A-013-36", report.Stopped && report.Headline == "Cannot submit to server. Mock rejection" &&
                              report.Problems.Contains("1111111-CPP-1-PQE.ins and the files after it were not sent. Sync stopped.") &&
                              mock.Requests.Count(r => r.PathAndQuery == "/Inspect/Inspection/NormalSubmit") == 1 &&
                              !mock.Requests.Any(r => r.PathAndQuery.StartsWith("/Inspect/Sync/")) && Directory.GetFiles(box.Outbox).Length == 3 &&
                              StrandSyncService.ReadRedLastSyncDate(box.Local, box.IF) == null,
                "one NormalSubmit, no folder sync, all three stay, LastSyncDate unchanged");
        }

        // A-013-37 / 38 / 40 / 41 / 43 folder sync
        Reset();
        using (var box = new Sandbox())
        {
            DateTime t0 = DateTime.Now.AddHours(-2);
            string config = Path.Combine(box.IF, "config.json");
            File.WriteAllText(config, JsonConvert.SerializeObject(new { LastSyncDate = t0 }));
            File.WriteAllText(config + ".bak", JsonConvert.SerializeObject(new { LastSyncDate = t0 }));
            string configHash = Sha(config), bakHash = Sha(config + ".bak");
            // RED's own stored value T0 (a previous sync).
            Directory.CreateDirectory(box.Local);
            File.WriteAllText(Path.Combine(box.Local, "sync-state.json"), JsonConvert.SerializeObject(new Dictionary<string, DateTime> { [Path.GetFullPath(box.IF).TrimEnd('\\').ToUpperInvariant()] = t0 }));
            string Local(string name, string content, DateTime written)
            {
                string p = Path.Combine(box.MyList, name); Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, content); File.SetLastWriteTime(p, written); return p;
            }
            string y = "{\"Y\":1}";
            Local("Y.ins", y, t0.AddMinutes(30));
            Local("Z.ins", "{\"Z\":1}", t0.AddMinutes(30));
            Local("W.ins", "{\"W\":1}", t0.AddMinutes(-30));
            Local(Path.Combine("Sub", "Q.ins"), "{\"Q\":1}", t0.AddMinutes(30));
            Local("K.ins", "{\"K\":\"" + new string('a', 990) + "\"}", t0.AddMinutes(30));
            Local("J.ins", "{\"J\":1}", t0.AddMinutes(-30));
            string kServer = "{\"K\":\"" + new string('b', 590) + "\"}";
            string Entry(string name, int length, DateTime modified, string path = "\\\\srv\\share\\My List") =>
                $"{{\"SvrPath\":{JsonConvert.ToString(path)},\"FileName\":\"{name}\",\"FolderName\":\"MyList\",\"FileLength\":{length},\"LastModDate\":\"{modified:yyyy-MM-ddTHH:mm:ss}\"}}";
            string myList = "[" + string.Join(",", Entry("X.ins", 10, t0.AddMinutes(40)), Entry("Y.ins", y.Length, t0.AddMinutes(40)),
                Entry("V.ins", 10, t0.AddMinutes(-40)), Entry("K.ins", kServer.Length, t0.AddMinutes(40)),
                Entry("EMPTY.ins", 5, t0.AddMinutes(40)), Entry("J.ins", 99, t0.AddMinutes(40))) + "]";
            mock.Handler = r =>
            {
                string q = r.PathAndQuery;
                if (q.StartsWith("/Security/LogOn") || q == "/") return MockStrand.Json("{}");
                if (q.StartsWith("/Inspect/Sync/GetFileList")) return MockStrand.Json(q.Contains("folderName=MyList") ? myList : "[]");
                if (q.StartsWith("/Inspect/Sync/Download"))
                {
                    string path = System.Web.HttpUtility.ParseQueryString(new Uri(mock.Base, q).Query)["path"] ?? "";
                    if (path.EndsWith("X.ins")) return MockStrand.Json("{\"X\":\"downloaded\"}");
                    if (path.EndsWith("K.ins")) return MockStrand.Json(kServer);
                    if (path.EndsWith("EMPTY.ins")) return (200, "application/json", Array.Empty<byte>(), 0);
                    if (path.EndsWith("J.ins")) return (200, "text/plain", Encoding.UTF8.GetBytes("not json"), 0);
                }
                if (q == "/Inspect/Sync/Upload") return MockStrand.Json("{\"Success\":true}");
                return (404, "text/plain", Array.Empty<byte>(), 0);
            };
            DateTime started = DateTime.Now;
            var report = await StrandSyncService.SyncAsync(new SyncOptions { InspectionsFolder = box.IF, Session = NewSession(), LocalRoot = box.Local });
            var requests = mock.Requests.ToList();
            var lists = requests.Where(r => r.PathAndQuery.StartsWith("/Inspect/Sync/GetFileList")).Select(r => System.Web.HttpUtility.ParseQueryString(new Uri(mock.Base, r.PathAndQuery).Query)).ToList();
            var downloads = requests.Where(r => r.PathAndQuery.StartsWith("/Inspect/Sync/Download")).Select(r => System.Web.HttpUtility.ParseQueryString(new Uri(mock.Base, r.PathAndQuery).Query)["path"]).ToList();
            var uploads = requests.Where(r => r.PathAndQuery == "/Inspect/Sync/Upload").Select(r => Encoding.UTF8.GetString(r.Body)).ToList();
            bool uploadNames = uploads.Any(u => u.Contains("filename=\"Z.ins\"")) && uploads.Any(u => u.Contains("filename=\"Q.ins\"")) &&
                               !uploads.Any(u => u.Contains("filename=\"W.ins\"") || u.Contains("filename=\"Y.ins\"")) &&
                               uploads.All(u => u.Contains("name=\"folderName\"\r\n\r\nMyList") && u.Contains("name=\"userName\"\r\n\r\n" + user));
            Check("A-013-37", lists.Select(l => l["folderName"]).SequenceEqual(new[] { "MyList", "Review", "Archive" }) && lists.All(l => l["userName"] == user) &&
                              downloads.Any(d => d!.EndsWith("X.ins")) && !downloads.Any(d => Path.GetFileName(d) is "Y.ins" or "V.ins") && uploadNames &&
                              File.ReadAllText(Path.Combine(box.MyList, "X.ins")) == "{\"X\":\"downloaded\"}",
                "GetFileList MyList, Review, Archive with the decoded userName; X downloaded; Z and Sub\\Q.ins uploaded; Y, V, W not transferred" +
                $" (lists {string.Join("/", lists.Select(l => l["folderName"] + ":" + (l["userName"] == user)))}; downloads {string.Join("/", downloads.Select(Path.GetFileName))};" +
                $" uploads {string.Join("/", uploads.Select(u => System.Text.RegularExpressions.Regex.Match(u, "filename=\"([^\"]+)\"").Groups[1].Value))}; uploadNames {uploadNames}; X {(File.Exists(Path.Combine(box.MyList, "X.ins")) ? File.ReadAllText(Path.Combine(box.MyList, "X.ins")) : "missing")})");
            var stored = StrandSyncService.ReadRedLastSyncDate(box.Local, box.IF);
            Check("A-013-37", stored.HasValue && Math.Abs((stored.Value - started).TotalSeconds) < 2, "RED's LastSyncDate = when the folder sync began");
            string backups = Path.Combine(box.Local, "SyncBackups", DateTime.Now.ToString("yyyy-MM-dd"), "MyList");
            Check("A-013-38", File.ReadAllText(Path.Combine(box.MyList, "K.ins")) == kServer && Directory.Exists(backups) &&
                              Directory.GetFiles(backups).Any(f => new FileInfo(f).Length > 900) && uploads.Any(u => u.Contains("filename=\"K.ins\"") && u.Contains(kServer)) &&
                              report.Problems.Contains("K.ins: Strand's copy replaced yours; yours is in RED's sync backups."),
                "K takes Strand's bytes exactly, the old copy is backed up, then K is uploaded; the result says so");
            Check("A-013-40", !File.Exists(Path.Combine(box.MyList, "EMPTY.ins")) && File.ReadAllText(Path.Combine(box.MyList, "J.ins")) == "{\"J\":1}" &&
                              report.Problems.Contains("EMPTY.ins: download failed.") && report.Problems.Contains("J.ins: download failed."),
                "an empty or non-JSON download never replaces or creates a file, and both are listed");
            Check("A-013-41", Sha(config) == configHash && Sha(config + ".bak") == bakHash, "config.json and .bak are byte-identical after the Sync");
            Check("A-013-43", downloads.Any(d => d == "\\\\srv\\share\\My List\\X.ins"), "the decoded path equals the original SvrPath\\FileName exactly");
        }

        // A-013-39 file list failure
        Reset();
        using (var box = new Sandbox())
        {
            mock.Handler = r => r.PathAndQuery.StartsWith("/Security/LogOn") || r.PathAndQuery == "/" ? MockStrand.Json("{}") :
                                r.PathAndQuery.Contains("folderName=Review") ? (200, "text/html", Encoding.UTF8.GetBytes("<html/>"), 0) : MockStrand.Json("[]");
            var report = await StrandSyncService.SyncAsync(new SyncOptions { InspectionsFolder = box.IF, Session = NewSession(), LocalRoot = box.Local, ReadInspectLastSyncDate = _ => null });
            Check("A-013-39", report.Stopped && report.Headline == "Could not get the Review file list from Strand. Sync stopped." &&
                              !mock.Requests.Any(r => r.PathAndQuery.Contains("folderName=Archive")) && StrandSyncService.ReadRedLastSyncDate(box.Local, box.IF) == null,
                "stops at Review; no Archive request; LastSyncDate unchanged");
        }

        // A-013-42 unsaved editor wins
        Reset();
        using (var box = new Sandbox())
        {
            string e = Path.Combine(box.MyList, "E.ins"); File.WriteAllText(e, "{\"E\":\"local\"}");
            mock.Handler = r => r.PathAndQuery.StartsWith("/Security/LogOn") || r.PathAndQuery == "/" ? MockStrand.Json("{}") :
                                r.PathAndQuery.Contains("folderName=MyList") ? MockStrand.Json("[{\"SvrPath\":\"s\",\"FileName\":\"E.ins\",\"FileLength\":99,\"LastModDate\":\"2026-10-18T10:00:00\"}]") :
                                r.PathAndQuery.StartsWith("/Inspect/Sync/Download") ? MockStrand.Json("{\"E\":\"server\"}") :
                                r.PathAndQuery == "/Inspect/Sync/Upload" ? MockStrand.Json("{\"Success\":true}") : MockStrand.Json("[]");
            var report = await StrandSyncService.SyncAsync(new SyncOptions { InspectionsFolder = box.IF, Session = NewSession(), LocalRoot = box.Local,
                ReadInspectLastSyncDate = _ => null, IsOpenWithUnsavedChanges = p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(e), StringComparison.OrdinalIgnoreCase) });
            Check("A-013-42", File.ReadAllText(e) == "{\"E\":\"local\"}" && report.Problems.Contains("E.ins: open with unsaved changes; Strand's copy was not downloaded."),
                "a file open with unsaved changes is not downloaded and is listed");
        }

        // V8 plan only: nothing sent or written
        Reset();
        using (var box = new Sandbox())
        {
            File.WriteAllText(Path.Combine(box.Outbox, "1111111-CPP-1-PQE.ins"), Fixture(number: "1111111-CPP-1-PQE").ToString(Formatting.None));
            File.WriteAllText(Path.Combine(box.MyList, "Z.ins"), "{\"Z\":1}");
            mock.Handler = r => r.PathAndQuery.StartsWith("/Security/LogOn") || r.PathAndQuery == "/" ? MockStrand.Json("{}") :
                                r.PathAndQuery.Contains("folderName=MyList") ? MockStrand.Json("[{\"SvrPath\":\"s\",\"FileName\":\"X.ins\",\"FileLength\":5,\"LastModDate\":\"2026-10-18T10:00:00\"}]") : MockStrand.Json("[]");
            string before = string.Join(",", Directory.GetFiles(box.IF, "*", SearchOption.AllDirectories).Select(Sha));
            var report = await StrandSyncService.SyncAsync(new SyncOptions { InspectionsFolder = box.IF, Session = NewSession(), LocalRoot = box.Local, PlanOnly = true, ReadInspectLastSyncDate = _ => null });
            Check("V8", report.PlannedDownloads.SequenceEqual(new[] { "MyList\\X.ins" }) && report.PlannedUploads.SequenceEqual(new[] { "MyList\\Z.ins" }) &&
                        mock.Requests.All(r => r.PathAndQuery == "/" || r.PathAndQuery.StartsWith("/Security/LogOn") || r.PathAndQuery.StartsWith("/Inspect/Sync/GetFileList")) &&
                        string.Join(",", Directory.GetFiles(box.IF, "*", SearchOption.AllDirectories).Select(Sha)) == before,
                "Sync plan: only the internet check, LogOn and three GetFileList calls; lists shown; nothing sent or written");
        }
    }
}
