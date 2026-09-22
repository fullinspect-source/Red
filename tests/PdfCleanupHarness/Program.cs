using System.Diagnostics;
using System.Reflection;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json.Linq;
using PdfSharp.Pdf;

static byte[] Pdf(string label)
{
    using var document = new PdfDocument(); document.AddPage(); document.Info.Subject = label;
    using var bytes = new MemoryStream(); document.Save(bytes, false); return bytes.ToArray();
}
static PdfEditMonitor Monitor(string root)
{
    byte[] bytes = Pdf("original");
    var owner = new InspectionFile { Attachments = new() { PdfAttachmentService.Create("Plan.pdf", bytes) } };
    return new PdfEditMonitor(owner, 0, "Plan.pdf", bytes, Path.Combine(root, "synthetic.ins"), root);
}
if (args.FirstOrDefault() == "--monitor")
{
    var childMonitor = Monitor(args[1]);
    Console.WriteLine(childMonitor.WorkingPath); Console.Out.Flush();
    Console.ReadLine();
    GC.KeepAlive(childMonitor); // Deliberately abandon: emulate process exit, not successful cleanup.
    return;
}
if (args.FirstOrDefault() == "--replace")
{
    string replacement = args[1] + ".replacement";
    try
    {
        File.WriteAllBytes(replacement, Pdf("late atomic replacement"));
        File.Move(replacement, args[1], true);
        Console.WriteLine("REPLACED");
    }
    catch (IOException) { Console.WriteLine("BLOCKED"); }
    finally { if (File.Exists(replacement)) File.Delete(replacement); }
    return;
}
if (args.FirstOrDefault() == "--write")
{
    try
    {
        using var stream = new FileStream(args[1], FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Write(Pdf("late save")); stream.Flush(true);
        Console.WriteLine("WROTE");
    }
    catch (IOException) { Console.WriteLine("BLOCKED"); }
    return;
}

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name); passed++;
}
void Fails(Action action, string name)
{
    try { action(); } catch (IOException) { Check(true, name); return; }
    throw new Exception("Expected IOException: " + name);
}
Process Child(params string[] arguments)
{
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardInput = true,
        RedirectStandardError = true, UseShellExecute = false };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    return Process.Start(start)!;
}
string Line(Process process) => process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult()
    ?? throw new Exception("Child exited: " + process.StandardError.ReadToEnd());
void Exit(Process process)
{
    if (!process.WaitForExit(20000)) { process.Kill(true); throw new Exception("Child timeout"); }
    if (process.ExitCode != 0) throw new Exception(process.StandardError.ReadToEnd());
}
string sandbox = Path.Combine(Directory.GetCurrentDirectory(), "work", "pdf-cleanup-" + Guid.NewGuid().ToString("N"));
string root = Path.Combine(sandbox, "RED", "PdfAttachments");
Directory.CreateDirectory(root);
var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
void Age(string session)
{
    foreach (string file in Directory.GetFiles(session)) File.SetLastWriteTimeUtc(file, now.AddDays(-31).UtcDateTime);
    Directory.SetLastWriteTimeUtc(session, now.AddDays(-31).UtcDateTime);
}
string Owned(string? identity = null, string? name = null)
{
    string session = Path.Combine(root, identity ?? new string('A', 64), name ?? Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(session);
    using (PdfWorkingSession.CreateLease(session)) { }
    File.WriteAllBytes(Path.Combine(session, "Orientation.pdf"), Pdf("old"));
    Age(session); return session;
}
try
{
    var monitor = Monitor(root);
    string directory = Path.GetDirectoryName(monitor.WorkingPath)!;
    byte[] original = File.ReadAllBytes(monitor.WorkingPath);
    Check(monitor.CanLeave(out _), "preliminary CanLeave permits unchanged disk");
    byte[] changed = original.ToArray(); changed[^1] = changed[^1] == (byte)'\n' ? (byte)'\r' : (byte)'\n';
    File.WriteAllBytes(monitor.WorkingPath, changed);
    Check(changed.Length == original.Length, "late pre-lease change has same length and requires byte comparison");
    Fails(monitor.Cleanup, "cleanup rechecks changes after preliminary CanLeave");
    Check(File.ReadAllBytes(monitor.WorkingPath).SequenceEqual(changed) && monitor.HasPendingChanges,
        "failed cleanup retains edited bytes and pending status");
    monitor.Poll(now, () => true);
    Check(monitor.Poll(now.Add(PdfEditMonitor.Debounce), () => true), "failed cleanup remains capturable by live monitor");
    using (var locked = new FileStream(monitor.WorkingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Fails(monitor.Cleanup, "cleanup refuses existing writer lease");
    monitor.Cleanup(() =>
    {
        using var writer = Child("--write", monitor.WorkingPath);
        Check(Line(writer) == "BLOCKED", "actual child-process late save blocked at final-comparison boundary");
        Exit(writer);
        Check(File.Exists(monitor.WorkingPath), "working path exists until retirement while lease remains held");
        if (OperatingSystem.IsWindows())
        {
            using var replacer = Child("--replace", monitor.WorkingPath);
            Check(Line(replacer) == "BLOCKED", "Windows child atomic replacement blocked through retirement");
            Exit(replacer);
        }
        else Console.WriteLine("SKIP Windows mandatory atomic-replacement sharing: host is not Windows");
    });
    Check(!File.Exists(monitor.WorkingPath) && !Directory.Exists(directory), "cleanup retires PDF and own empty session");
    Check(Directory.Exists(root) && Directory.Exists(Path.GetDirectoryName(directory)!), "cleanup never removes root or identity");
    monitor.Cleanup(); Check(monitor.CanLeave(out _), "cleanup idempotent after successful retirement");
    var addOwner = new InspectionFile();
    var pending = new PdfEditMonitor(addOwner, null, "Pending.pdf", original,
        Path.Combine(root, "pending.ins"), root, pendingAdd: true);
    Fails(pending.Cleanup, "unchanged pending add cannot be discarded by cleanup");
    Check(pending.RetrySave(() => true), "pending add remains capturable after refused cleanup");
    pending.Cleanup();

    var deleteOwner = new InspectionFile { Attachments = new() { PdfAttachmentService.Create("Delete.pdf", original) } };
    var deleteMonitor = new PdfEditMonitor(deleteOwner, 0, "Delete.pdf", original,
        Path.Combine(root, "delete.ins"), root);
    var deleteService = new PdfAttachmentService(deleteOwner);
    bool TryDelete(bool saveResult) => deleteService.Delete(new[] { 0 }, () =>
    {
        using var writer = Child("--write", deleteMonitor.WorkingPath);
        Check(Line(writer) == "BLOCKED", "transactional delete blocks child save under same retirement lease");
        Exit(writer);
        return saveResult;
    }, new[] { deleteMonitor });
    Check(!TryDelete(false) && deleteOwner.Attachments!.Count == 1 && deleteMonitor.CanLeave(out _),
        "failed transactional delete releases working lease and preserves live session");
    Check(TryDelete(true) && deleteOwner.Attachments!.Count == 0 && !File.Exists(deleteMonitor.WorkingPath),
        "successful transactional delete retires working file before releasing its lease");

    var retained = Monitor(root);
    Fails(() => retained.Cleanup(() => throw new IOException("injected retirement failure")), "retirement failure propagated");
    File.WriteAllBytes(retained.WorkingPath, changed);
    retained.Poll(now, () => true);
    Check(retained.Poll(now.Add(PdfEditMonitor.Debounce), () => true), "retirement failure leaves session capturable");
    string extra = Path.Combine(Path.GetDirectoryName(retained.WorkingPath)!, "editor-backup.pdf");
    File.WriteAllBytes(extra, changed); retained.Cleanup();
    Check(File.ReadAllBytes(extra).SequenceEqual(changed), "cleanup preserves unexpected editor files without recursive deletion");

    string eligible = Owned();
    Check(PdfWorkingSession.Scavenge(root, Path.Combine(sandbox, "different", "PdfAttachments"), now) == 0 && Directory.Exists(eligible),
        "mismatched root cannot scavenge");
    PdfWorkingSession.Scavenge(root, now);
    Check(Directory.Exists(eligible), "production entry point rejects a custom root");
    string fresh = Owned(); File.SetLastWriteTimeUtc(Path.Combine(fresh, "Orientation.pdf"), now.UtcDateTime);
    string unmarked = Owned(); File.Delete(Path.Combine(unmarked, PdfWorkingSession.LockName));
    string badMarker = Owned(); File.WriteAllText(Path.Combine(badMarker, PdfWorkingSession.LockName), "not RED"); Age(badMarker);
    string badIdentity = Owned(new string('G', 64));
    string shortIdentity = Owned(new string('A', 63));
    string badSession = Owned(name: new string('Z', 32));
    string shortSession = Owned(name: new string('a', 31));
    string unknown = Owned(); File.WriteAllText(Path.Combine(unknown, "other.txt"), "keep"); Age(unknown);
    string nested = Owned(); Directory.CreateDirectory(Path.Combine(nested, "nested")); Age(nested);
    string boundary = Owned(); File.SetLastWriteTimeUtc(Path.Combine(boundary, "Orientation.pdf"), now.Subtract(PdfWorkingSession.Retention).UtcDateTime);
    string source = Path.Combine(sandbox, "Documents"); Directory.CreateDirectory(source);
    string sourceFile = Path.Combine(source, "source.pdf"); File.WriteAllBytes(sourceFile, changed);
    string identityLink = Path.Combine(root, new string('B', 64)); Directory.CreateSymbolicLink(identityLink, source);
    string sessionLink = Path.Combine(root, new string('A', 64), new string('C', 32)); Directory.CreateSymbolicLink(sessionLink, source);
    string fileLink = Owned(); File.Delete(Path.Combine(fileLink, "Orientation.pdf"));
    File.CreateSymbolicLink(Path.Combine(fileLink, "Orientation.pdf"), sourceFile);
    string markerLink = Owned(); File.Delete(Path.Combine(markerLink, PdfWorkingSession.LockName));
    File.CreateSymbolicLink(Path.Combine(markerLink, PdfWorkingSession.LockName), sourceFile);
    Fails(() => { using var lease = PdfWorkingSession.OpenRetirementLease(Path.Combine(fileLink, "Orientation.pdf")); },
        "retirement lease itself refuses final symlink, not only scavenger filtering");
    int scavenged = PdfWorkingSession.Scavenge(root, root, now);
    Check(scavenged == 1 && !Directory.Exists(eligible), $"only old unlocked exact owned session is scavenged (count={scavenged}, eligible remains={Directory.Exists(eligible)})");
    foreach (var item in new[] { fresh, unmarked, badMarker, badIdentity, shortIdentity, badSession, shortSession, unknown, nested, boundary, identityLink, sessionLink, fileLink, markerLink })
        Check(Directory.Exists(item), "conservative scavenging preserves " + Path.GetFileName(item));
    Check(File.ReadAllBytes(sourceFile).SequenceEqual(changed) && Directory.Exists(root), "scavenging preserves source bytes and root");
    string rootLink = Path.Combine(sandbox, "linked", "PdfAttachments"); Directory.CreateDirectory(Path.GetDirectoryName(rootLink)!);
    Directory.CreateSymbolicLink(rootLink, root);
    Check(PdfWorkingSession.Scavenge(rootLink, rootLink, now) == 0, "symlink root rejected without traversal");
    string ancestor = Path.Combine(sandbox, "ancestor"); Directory.CreateSymbolicLink(ancestor, Path.Combine(sandbox, "RED"));
    string ancestorRoot = Path.Combine(ancestor, "PdfAttachments");
    Check(PdfWorkingSession.Scavenge(ancestorRoot, ancestorRoot, now) == 0, "symlink ancestor rejected without traversal");

    using (var child = Child("--monitor", root))
    {
        try
        {
            string childPdf = Line(child); string childSession = Path.GetDirectoryName(childPdf)!;
            // Inject a future clock, not writes to the exclusively held marker's timestamps.
            // This also runs under mandatory Windows sharing without weakening the live lock.
            var childNow = DateTimeOffset.UtcNow.Add(PdfWorkingSession.Retention).AddDays(1);
            Check(PdfWorkingSession.Scavenge(root, root, childNow) == 0 && File.Exists(childPdf), "actual child-process monitor lifetime lock prevents stale-session scavenging");
            child.StandardInput.WriteLine("exit"); child.StandardInput.Flush(); Exit(child);
            Check(PdfWorkingSession.Scavenge(root, root, childNow) == 1 && !Directory.Exists(childSession), "abandoned child-process session becomes scavengable after process exit");
        }
        finally { if (!child.HasExited) { child.Kill(true); child.WaitForExit(); } }
    }
    Console.WriteLine($"{passed} PDF cleanup/scavenging checks passed");
}
finally { Directory.Delete(sandbox, true); }
