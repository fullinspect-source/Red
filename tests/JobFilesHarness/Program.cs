// F-016 Job Files harness (program/features/F-016-job-files-reference/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// Sandbox Inspections folder under %TEMP% with generated sample PDFs (job 1234567), a sandbox PDF working root, and a
// loopback mock Strand website for RequestFiles. Never touches Dropbox\Inspections or live Strand.
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Controls;
using System.Windows.Documents;
using InspectionEditor;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PdfSharp.Pdf;

if (args.Length >= 2 && args[0] == "--ui-pdf")
{
    // A sample one-page PDF for the isolated UI check's Jobs folder.
    using var document = new PdfDocument();
    document.AddPage();
    document.Info.Subject = "sample plan";
    document.Save(args[1]);
    Console.WriteLine("wrote sample PDF");
    return 0;
}
int result = 1;
var thread = new Thread(() => result = Harness.Run().GetAwaiter().GetResult());
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
return result;

sealed record MockRequest(string Method, string PathAndQuery);

sealed class QuietLog : ISubmitLog
{
    public void Inspect(string line) { }
    public void Red(string line) { }
}

// A tiny loopback HTTP server: LogOn sets cookies, other paths answer the scripted status.
sealed class MockStrand : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    public ConcurrentQueue<MockRequest> Requests { get; } = new();
    public int Status { get; set; } = 200;
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
            var request = new MockRequest(first[0], first.Length > 1 ? first[1] : "/");
            Requests.Enqueue(request);
            bool logOn = request.PathAndQuery.StartsWith("/Security/LogOn", StringComparison.OrdinalIgnoreCase);
            int status = logOn ? 200 : Status;
            string extra = logOn ? "Set-Cookie: ASP.NET_SessionId=mock1; path=/\r\nSet-Cookie: .ASPXAUTH=mock2; path=/\r\n" : "";
            string head = $"HTTP/1.1 {status} X\r\nContent-Type: text/plain\r\nContent-Length: 0\r\n{extra}Connection: close\r\n\r\n";
            try { await stream.WriteAsync(Encoding.ASCII.GetBytes(head)); } catch { }
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

    private static byte[] Pdf(string label)
    {
        using var document = new PdfDocument();
        document.AddPage();
        document.Info.Subject = label;
        using var bytes = new MemoryStream();
        document.Save(bytes, false);
        return bytes.ToArray();
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static JObject Report() => new()
    {
        ["InspectVersion"] = null, ["WorkOrderId"] = "00000000-0000-0000-0000-000000000016", ["ServiceId"] = 1, ["FormId"] = 20,
        ["InspectionName"] = "Example Inspection", ["InspectionNumber"] = "1234567-CPP-1-PQE", ["InspectionCode"] = "CPP",
        ["Attempt"] = 1, ["StatusId"] = 1, ["JobNumber"] = "1234567", ["Address"] = "100 Example St",
        ["InspectionFee_Secure"] = "PLACEHOLDERCIPHERTEXTA0==",
        ["Sections"] = new JArray(new JObject
        {
            ["SectionId"] = 2, ["Name"] = "Footings", ["ControlName"] = "SectionStandardControl", ["Number"] = "2",
            ["Items"] = new JArray(new JObject { ["ItemId"] = 201, ["Name"] = "Forms", ["Number"] = "2.1", ["ControlName"] = "PassFailNaNi", ["Value"] = "Pass", ["Comments"] = "" })
        }),
        ["Events"] = new JArray(), ["Attachments"] = new JArray()
    };

    private static JObject Read(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    // The editor's attachment transaction (PdfAttachmentService.Transact), reached by reflection.
    private static bool Transact(InspectionFile owner, JArray replacement, Func<bool> save)
    {
        var expected = (JArray)typeof(PdfAttachmentService).GetMethod("Snapshot", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { owner })!;
        return (bool)typeof(PdfAttachmentService).GetMethod("Transact", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new PdfAttachmentService(owner), new object[] { expected, replacement, save })!;
    }

    private static JArray Snapshot(InspectionFile owner) => owner.Attachments == null ? new JArray() : JArray.FromObject(owner.Attachments);

    public static async Task<int> Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f016-" + Guid.NewGuid().ToString("N"));
        string inspections = Path.Combine(root, "Inspections");
        string engineering = Path.Combine(inspections, "Jobs", "1234567", "Engineering");
        string inspectionPdfs = Path.Combine(inspections, "Jobs", "1234567", "Inspections");
        string workingRoot = Path.Combine(root, "AppData", "PdfAttachments");
        Directory.CreateDirectory(engineering);
        Directory.CreateDirectory(inspectionPdfs);
        Directory.CreateDirectory(Path.Combine(inspections, "MyList"));
        try
        {
            string plan = Path.Combine(engineering, "1234567.pdf");
            File.WriteAllBytes(plan, Pdf("plan"));
            File.WriteAllBytes(Path.Combine(engineering, "1234567R2 Plans.pdf"), Pdf("plans r2"));
            File.WriteAllBytes(Path.Combine(inspectionPdfs, "1234567-CPP-1-PQE.pdf"), Pdf("prior report"));
            File.SetLastWriteTime(plan, DateTime.Now.AddDays(-2));
            string planHash = Hash(plan);

            // ---------- A-016-01 Listing and versions ----------
            var files = JobFilesService.List(inspections, "1234567", null);
            Check("A-016-01", files.Select(f => (f.Filename, f.FileType, f.Version)).SequenceEqual(new[]
                {
                    ("1234567.pdf", "Engineering", "0"), ("1234567R2 Plans.pdf", "Engineering", "2"), ("1234567-CPP-1-PQE.pdf", "Inspection", "0")
                }), "three files: Engineering/0, Engineering/2, Inspection/0");
            Check("J1", JobFilesService.List(inspections, "7654321", null).Count == 0 &&
                        !Directory.Exists(Path.Combine(inspections, "Jobs", "7654321")), "a missing job folder lists as empty and is not created");
            Check("J1", JobFilesService.JobFolder(inspections, "1234567R3") == Path.Combine(inspections, "Jobs", "1234567"),
                "the folder uses the first 7 characters of JobNumber");

            string insPath = Path.Combine(inspections, "MyList", "1234567-CPP-1-PQE.ins");
            File.WriteAllText(insPath, Report().ToString(Formatting.None), new UTF8Encoding(false));
            var saver = new SurgicalSaveService();
            var model = saver.Load(insPath);
            bool Save() { saver.Save(model); return true; }
            var jobFile = files[0];

            // ---------- A-016-02 Opening creates a reference attachment ----------
            {
                var decision = JobFilesService.Plan(model.Attachments, jobFile.Filename, jobFile.Modified);
                var replacement = Snapshot(model);
                replacement.Add(JobFilesService.ReferenceAttachment(jobFile.Filename, PdfAttachmentService.ReadPdf(jobFile.FullPath), DateTime.Now));
                bool saved = decision.Action == JobFileOpenAction.CreateReference && Transact(model, replacement, Save);
                var written = (JArray)Read(insPath)["Attachments"]!;
                var a = written.Count == 1 ? (JObject)written[0] : new JObject();
                Check("A-016-02", saved && written.Count == 1 && (string?)a["Filename"] == "1234567.pdf" && (bool?)a["ReferenceOnly"] == true &&
                                  (bool?)a["CanExcludePages"] == true && a["AnnotatedPages"] is JArray { Count: 0 } && a["IncludedPages"] is JArray { Count: 0 } &&
                                  a["EditPath"]?.Type == JTokenType.Null && a["InspectVersion"]?.Type == JTokenType.Null && (string?)a["FileType"] == "pdf" &&
                                  Convert.FromBase64String((string)a["FileData"]!).AsSpan().SequenceEqual(File.ReadAllBytes(plan)) &&
                                  a["LastWriteTime"]?.Type == JTokenType.String && a["ServerPath"] == null && a["ReducedFileData"] == null,
                    "one attachment with the O2 shape (full PDF FileData, EditPath and InspectVersion null), written to the INS");
                Check("A-016-02", Hash(plan) == planHash, "the job file's SHA-256 is unchanged");
            }

            // ---------- A-016-03 Not attached until marked ----------
            Check("A-016-03", !JobFilesService.List(inspections, "1234567", model.Attachments).First(f => f.Filename == "1234567.pdf").Attached,
                "the 1234567.pdf row shows no Attached marker while the attachment is reference-only");

            // ---------- A-016-06 No duplicate attachment ----------
            {
                var decision = JobFilesService.Plan(model.Attachments, "1234567.pdf", File.GetLastWriteTime(plan));
                Check("A-016-06", decision.Action == JobFileOpenAction.UseExisting && decision.Index == 0 && model.Attachments!.Count == 1,
                    "opening it again reuses the one attachment (no working copy needed to find it)");
            }

            // ---------- R1 Marking up through the PDF monitor ----------
            {
                var monitor = new PdfAttachmentService(model).Open(0, insPath, workingRoot);
                Check("O5", !monitor.WorkingPath.StartsWith(inspections, StringComparison.OrdinalIgnoreCase) && File.Exists(monitor.WorkingPath),
                    "the working copy lives outside Inspections");
                File.WriteAllBytes(monitor.WorkingPath, Pdf("marked up"));
                var t0 = DateTimeOffset.UtcNow;
                monitor.Poll(t0, Save);
                bool captured = monitor.Poll(t0 + PdfEditMonitor.Debounce + TimeSpan.FromMilliseconds(50), Save);
                var a = (JObject)((JArray)Read(insPath)["Attachments"]!)[0];
                Check("R1", captured && (bool?)a["ReferenceOnly"] == false && (string?)a["Filename"] == "1234567.pdf" &&
                            JobFilesService.AttachmentTime(a) > File.GetLastWriteTime(plan),
                    "a saved markup sets ReferenceOnly false and moves LastWriteTime");
                Check("R3", JobFilesService.List(inspections, "1234567", model.Attachments).First(f => f.Filename == "1234567.pdf").Attached,
                    "after marking, the row shows the Attached marker");
                monitor.Cleanup();
                Check("C3", Hash(plan) == planHash, "the job file is still unchanged");
            }

            // ---------- A-016-04 Marked-up prompt; No starts fresh ----------
            {
                var decision = JobFilesService.Plan(model.Attachments, "1234567.pdf", File.GetLastWriteTime(plan));
                Check("A-016-04", decision.Action == JobFileOpenAction.AskMarkedUp &&
                                  JobFilesService.MarkedUpQuestion("1234567.pdf") ==
                                  "It appears you have already marked up '1234567.pdf'.\r\nDo you want to attach the marked up file? or click No to start fresh.",
                    "job file older than the markup: RED asks INSPECT's O3 question");
                var current = (JObject)Snapshot(model)[0];
                var replacement = Snapshot(model);
                replacement[0] = JobFilesService.Refreshed(current, PdfAttachmentService.ReadPdf(plan), DateTime.Now);
                Transact(model, replacement, Save);
                var a = (JObject)((JArray)Read(insPath)["Attachments"]!)[0];
                Check("A-016-04", (bool?)a["ReferenceOnly"] == true && a["AnnotatedPages"] is JArray { Count: 0 } &&
                                  (string?)a["Id"] == (string?)current["Id"] &&
                                  Convert.FromBase64String((string)a["FileData"]!).AsSpan().SequenceEqual(File.ReadAllBytes(plan)),
                    "No replaces the copy with the job file, ReferenceOnly true, same attachment Id");
            }

            // ---------- A-016-05 Newer Dropbox file prompt ----------
            {
                var marked = (JObject)Snapshot(model)[0];
                marked["ReferenceOnly"] = false;
                marked["LastWriteTime"] = HeaderRules.FormatDate(DateTime.Now.AddHours(-1));
                var list = new List<object> { marked };
                var decision = JobFilesService.Plan(list, "1234567.pdf", DateTime.Now);
                Check("A-016-05", decision.Action == JobFileOpenAction.AskDropboxNewer &&
                                  JobFilesService.DropboxNewerQuestion("1234567.pdf") ==
                                  "It appears you have already marked up '1234567.pdf' but the Dropbox file is newer.\r\nDo you want to attach the marked up file? or click No to use the newer Dropbox file.",
                    "job file newer than the markup: RED asks INSPECT's O4 question");
                var reference = (JObject)Snapshot(model)[0];
                reference["LastWriteTime"] = HeaderRules.FormatDate(DateTime.Now.AddHours(-1));
                Check("O1", JobFilesService.Plan(new List<object> { reference }, "1234567.pdf", DateTime.Now).Action == JobFileOpenAction.RefreshReference,
                    "an unmarked reference copy older than the job file is refreshed without a question");
            }

            // ---------- A-016-08 Update list ----------
            {
                var before = JobFilesService.List(inspections, "1234567", model.Attachments);
                Check("A-016-08", !JobFilesService.ListChanged(before, JobFilesService.List(inspections, "1234567", model.Attachments)) &&
                                  JobFilesService.NoUpdatesMessage == "No updates to the job files list at this current time. Please add a file manually in File Explorer or by the 'Refresh' button.",
                    "no change: INSPECT's no-updates message");
                File.WriteAllBytes(Path.Combine(engineering, "1234567R3EC.pdf"), Pdf("new"));
                var after = JobFilesService.List(inspections, "1234567", model.Attachments);
                Check("A-016-08", JobFilesService.ListChanged(before, after) && after.Any(f => f.Filename == "1234567R3EC.pdf" && f.Version == "3") &&
                                  JobFilesService.ListUpdatedMessage == "Job files list was updated.",
                    "a new PDF: \"Job files list was updated.\" and the file is listed");
            }

            // ---------- Docs viewer with the host ----------
            {
                var host = new JobFilesHost { JobNumber = "1234567", InspectionsFolder = inspections, Attachments = () => model.Attachments };
                var window = new DocsViewerWindow(insPath, host);
                var panel = (StackPanel)typeof(DocsViewerWindow).GetField("DocsListPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var note = (TextBlock)typeof(DocsViewerWindow).GetField("RevisionNoteText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                string text = string.Join("|", panel.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>())
                    .Select(b => b.Content is TextBlock tb ? string.Concat(tb.Inlines.OfType<Run>().Select(r => r.Text)) : b.Content?.ToString()));
                Check("J3", note.Text == "Showing revision R3", "the viewer shows the newest revision and says so");
                Check("R3", !text.Contains("Attached"), "a reference-only attachment shows no Attached marker in the viewer");
                window.Close();
            }

            // ---------- A-016-07 Request files (mocked API) ----------
            {
                using var mock = new MockStrand();
                var session = StrandWebsiteSession.ForLoopbackMock(mock.Base, () => ("mockuser", "mock-" + Guid.NewGuid().ToString("N")));
                mock.Status = 200;
                string ok = await JobFilesService.RequestFilesAsync(session, "1234567R2", CancellationToken.None);
                var put = mock.Requests.LastOrDefault();
                Check("A-016-07", ok == "Job files were requested from server. Any received files will be copied to your 'Jobs' folder for this job" &&
                                  put?.Method == "PUT" && put.PathAndQuery == "/Inspect/Jobs/RequestFiles?jobNumber=1234567",
                    "HTTP 200: PUT /Inspect/Jobs/RequestFiles?jobNumber=1234567 and INSPECT's success message");
                mock.Status = 500;
                string failed = await JobFilesService.RequestFilesAsync(session, "1234567", CancellationToken.None);
                Check("A-016-07", failed == "Unable to connect to server to retrieve job files. Please contact IT Support if this persists",
                    "HTTP 500: INSPECT's failure message");
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                var offline = StrandWebsiteSession.ForLoopbackMock(new Uri($"http://127.0.0.1:{port}/"), () => ("mockuser", "x"));
                string notConnected = await JobFilesService.RequestFilesAsync(offline, "1234567", CancellationToken.None);
                Check("F1", notConnected == "Unable to request this job's files.  It appears you are not connected to the Internet.",
                    "nothing listening: INSPECT's not-connected message (two spaces)");
                var noLogin = StrandWebsiteSession.ForLoopbackMock(mock.Base, () => (null, null));
                Check("F1", await JobFilesService.RequestFilesAsync(noLogin, "1234567", CancellationToken.None) == JobFilesService.SignInFirstMessage,
                    "no stored login: RED asks to sign in first and sends nothing");
            }

            // ---------- A-016-09 PreInspector blocks Job Files ----------
            Check("A-016-09", JobFilesService.IsBlockedFor("PreInspector") &&
                              JobFilesService.PreInspectorBlockedMessage == "This functionality is not available in PreInspector mode.",
                "UserType \"PreInspector\" is blocked with INSPECT's message");
            Check("A-016-09", !JobFilesService.IsBlockedFor("Preinspector") && !JobFilesService.IsBlockedFor("Inspector") &&
                              !JobFilesService.IsBlockedFor("NonDropBoxInspector") && !JobFilesService.IsBlockedFor(null),
                "\"Preinspector\", \"Inspector\" and \"NonDropBoxInspector\" open Job Files normally");

            // ---------- A-016-10 The editor's plan/slab and prior buttons resolve to job files (owner Q-56, 2026-10-20) ----------
            {
                var slab = JobFilesService.JobFileFor(Path.Combine(engineering, "1234567R2 Plans.pdf"), inspections, null);
                var prior = JobFilesService.JobFileFor(Path.Combine(inspectionPdfs, "1234567-CPP-1-PQE.pdf"), inspections, null);
                string outside = Path.Combine(root, "elsewhere.pdf");
                File.WriteAllBytes(outside, Pdf("outside"));
                Check("A-016-10", slab is { Filename: "1234567R2 Plans.pdf", FileType: "Engineering", Version: "2" } &&
                                  prior is { Filename: "1234567-CPP-1-PQE.pdf", FileType: "Inspection" } &&
                                  JobFilesService.JobFileFor(outside, inspections, null) == null &&
                                  JobFilesService.JobFileFor(Path.Combine(engineering, "missing.pdf"), inspections, null) == null &&
                                  JobFilesService.JobFileFor(Path.Combine(root, "Inspections", "Jobs", "..", "elsewhere.pdf"), inspections, null) == null,
                    "a PDF under {IF}\\Jobs becomes a job file (type and version as Job Files lists them); outside Jobs, missing or a '..' path: none (read-only copy)");
            }

            // ---------- A-016-11 Edit page 3 of 5 through a button: one annotated page, only it prints, original untouched ----------
            {
                string fivePager = Path.Combine(engineering, "1234567 Foundation Plan.pdf");
                using (var document = new PdfDocument())
                {
                    for (int i = 0; i < 5; i++) document.AddPage();
                    document.Info.Subject = "five page plan";
                    document.Save(fivePager);
                }
                string fiveHash = Hash(fivePager);
                string ins2 = Path.Combine(inspections, "MyList", "1234567-CPP-2-PQE.ins");
                var doc2 = Report();
                doc2["InspectionNumber"] = "1234567-CPP-2-PQE";
                File.WriteAllText(ins2, doc2.ToString(Formatting.None), new UTF8Encoding(false));
                var saver2 = new SurgicalSaveService();
                var model2 = saver2.Load(ins2);
                bool Save2() { saver2.Save(model2); return true; }
                // What OpenSharedJobPdf → OpenJobFile does for a first open (CreateReference), then a second press (UseExisting).
                var file = JobFilesService.JobFileFor(fivePager, inspections, model2.Attachments)!;
                var first = JobFilesService.Plan(model2.Attachments, file.Filename, file.Modified);
                var replacement = Snapshot(model2);
                replacement.Add(JobFilesService.ReferenceAttachment(file.Filename, PdfAttachmentService.ReadPdf(file.FullPath), DateTime.Now));
                bool attached = first.Action == JobFileOpenAction.CreateReference && Transact(model2, replacement, Save2);
                var again = JobFilesService.Plan(model2.Attachments, file.Filename, File.GetLastWriteTime(fivePager));
                var monitor = new PdfAttachmentService(model2).Open(0, ins2, workingRoot);
                Check("A-016-11", attached && again.Action == JobFileOpenAction.UseExisting && model2.Attachments!.Count == 1 &&
                                  !monitor.WorkingPath.StartsWith(inspections, StringComparison.OrdinalIgnoreCase),
                    "first press: one reference-only attachment and a working copy outside Inspections; second press reuses it (no second attachment)");

                // The PDF app saves a markup on page 3 (index 2) of the working copy.
                using (var working = PdfSharp.Pdf.IO.PdfReader.Open(monitor.WorkingPath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
                {
                    var note = new PdfSharp.Pdf.Annotations.PdfTextAnnotation { Title = "Inspector", Contents = "check this beam" };
                    note.Rectangle = new PdfRectangle(new PdfSharp.Drawing.XRect(72, 72, 20, 20));
                    working.Pages[2].Annotations.Add(note);
                    working.Save(monitor.WorkingPath);
                }
                var t0 = DateTimeOffset.UtcNow;
                monitor.Poll(t0, Save2);
                bool captured = monitor.Poll(t0 + PdfEditMonitor.Debounce + TimeSpan.FromMilliseconds(50), Save2);
                var a = (JObject)((JArray)Read(ins2)["Attachments"]!)[0];
                int storedPages;
                using (var stored = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(Convert.FromBase64String((string)a["FileData"]!)), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                    storedPages = stored.PageCount;
                Check("A-016-11", captured && (bool?)a["ReferenceOnly"] == false && a["AnnotatedPages"] is JArray { Count: 1 } ap && (int)ap[0] == 2 &&
                                  (bool?)a["CanExcludePages"] == true && storedPages == 5 && a["ReducedFileData"] == null,
                    "after the markup: a real attachment, AnnotatedPages [2], the full 5-page PDF kept in FileData (INSPECT's format, no reduced copy)");

                var rendered = InspectionEditor.Services.Reports.ReportService.Render(Read(ins2), new InspectionEditor.Services.Reports.ReportSettings(),
                    new InspectionEditor.Services.Reports.ReportOptions(false, true, true), DateTime.Now);
                int previewPages;
                using (var preview = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(rendered.Pdf), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                    previewPages = preview.PageCount;
                Check("A-016-11", rendered.AttachmentsAdded == 1 && previewPages == rendered.ReportPages + 1,
                    $"the report preview adds exactly 1 page for the attachment ({rendered.ReportPages} report page(s) + 1)");
                monitor.Cleanup();
                Check("A-016-11", Hash(fivePager) == fiveHash, "the shared 5-page plan is byte-for-byte unchanged");

                // ---------- A-016-12 An unedited copy is dropped at Submit ----------
                string ins3 = Path.Combine(inspections, "MyList", "1234567-CPP-3-PQE.ins");
                var doc3 = Report();
                doc3["InspectionNumber"] = "1234567-CPP-3-PQE";
                File.WriteAllText(ins3, doc3.ToString(Formatting.None), new UTF8Encoding(false));
                var saver3 = new SurgicalSaveService();
                var model3 = saver3.Load(ins3);
                bool Save3() { saver3.Save(model3); return true; }
                var unedited = Snapshot(model3);
                unedited.Add(JobFilesService.ReferenceAttachment(file.Filename, PdfAttachmentService.ReadPdf(fivePager), DateTime.Now));
                bool referenced = Transact(model3, unedited, Save3);
                var ctx = new SubmitContext
                {
                    Model = model3, Saver = saver3, SourcePath = ins3, RawUserType = "Inspector",
                    Profile = new CompleteProfile(inspections, InspectUserKind.Inspector, "Pat Q Example", "inspector@example.com", "000-000-0000", "TX"),
                    SaveWithSweeps = () => { saver3.Save(model3); return true; }, Validate = () => new CompletionResult(),
                    LocalRoot = Path.Combine(root, "AppData"), Log = new QuietLog(), Sleep = _ => { }
                };
                var submitted = await SubmitService.SubmitAsync(ctx);
                string outboxCopy = Path.Combine(inspections, "Outbox", "1234567-CPP-3-PQE.ins");
                Check("A-016-12", referenced && submitted.Stage == SubmitStage.Done && File.Exists(outboxCopy) &&
                                  ((JArray?)Read(outboxCopy)["Attachments"] ?? new JArray()).Count == 0 && Hash(fivePager) == fiveHash,
                    $"a reference-only (unedited) copy is dropped at Submit: the Outbox file has no attachment ({submitted.Stage})");
            }

            // ---------- A-016-13 The buttons always open the PDF; a newer Dropbox file is still asked about (review 2026-10-20) ----------
            {
                string source = File.ReadAllText(Path.Combine("..", "..", "MainWindow.JobFiles.cs"));
                int shared = source.IndexOf("private void OpenSharedJobPdf", StringComparison.Ordinal);
                int open = source.IndexOf("private bool OpenJobFile", StringComparison.Ordinal);
                string sharedBody = shared < 0 || open < shared ? "" : source.Substring(shared, open - shared);
                Check("A-016-13", sharedBody.Contains("try { opened = OpenJobFile(") && sharedBody.Contains("catch (IOException") &&
                                  sharedBody.Contains("if (!opened)") && sharedBody.Contains("DocsViewerWindow.OpenReadOnlyCopy(fullPath);") &&
                                  source.Contains("if (plan.Action == JobFileOpenAction.AskMarkedUp && FindPdfMonitor(index)?.HasLaunched == true) break;") &&
                                  !source.Contains("default:\r\n                {\r\n                    index = plan.Index!.Value;\r\n                    if (FindPdfMonitor(index)?.HasLaunched == true) break;"),
                    "a PDF that cannot be attached (unsafe name, unreadable, over 100 MiB, embedding declined, save failed) still opens as a read-only copy; only O3 is skipped for an open copy, O4 (newer Dropbox file) is always asked (source check)");
            }

            Check("C3", Hash(plan) == planHash, "after every step the shared job file is byte-for-byte unchanged");
        }
        catch (Exception ex)
        {
            Check("harness", false, "unexpected exception: " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"F-016 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
