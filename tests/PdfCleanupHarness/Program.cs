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
static PdfEditMonitor Monitor(string root, string filename = "Plan.pdf")
{
    byte[] bytes = Pdf("original");
    var owner = new InspectionFile { Attachments = new() { PdfAttachmentService.Create(filename, bytes) } };
    return new PdfEditMonitor(owner, 0, filename, bytes, Path.Combine(root, "synthetic.ins"), root);
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
    File.WriteAllBytes(replacement, Pdf("late atomic replacement"));
    try
    {
        File.Move(replacement, args[1], true);
        Console.WriteLine("REPLACED");
    }
    // Windows reports a rename over the leased file as a sharing violation or as access denied; both mean blocked.
    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Console.WriteLine("BLOCKED"); }
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
// A left-over session: the marker plus its working copy, named after the attachment ("Orientation.pdf" before names).
string Owned(string? identity = null, string? name = null, string pdf = "Plan.pdf")
{
    string session = Path.Combine(root, identity ?? new string('A', 64), name ?? Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(session);
    using (PdfWorkingSession.CreateLease(session)) { }
    File.WriteAllBytes(Path.Combine(session, pdf), Pdf("old"));
    Age(session); return session;
}
// Symbolic links need Developer Mode or the symlink privilege on Windows. Without it a directory link is a junction and a
// file link is a file with a third-party reparse tag: still reparse points, which is what every guard refuses.
var directoryLinks = new List<string>();
void DirectoryLink(string link, string target)
{
    directoryLinks.Add(link);
    try { Directory.CreateSymbolicLink(link, target); return; }
    catch (Exception ex) when (OperatingSystem.IsWindows() && (ex is IOException || ex is UnauthorizedAccessException)) { }
    var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
    using (var mklink = Process.Start(start)!) mklink.WaitForExit();
    if (!Directory.Exists(link) || (File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0) throw new Exception("No junction: " + link);
    Console.WriteLine("NOTE no symlink privilege; junction used for " + link);
}
void FileLink(string link, string target)
{
    try { File.CreateSymbolicLink(link, target); return; }
    catch (Exception ex) when (OperatingSystem.IsWindows() && (ex is IOException || ex is UnauthorizedAccessException)) { }
    File.WriteAllBytes(link, Array.Empty<byte>());
    Reparse.Tag(link);
    Console.WriteLine("NOTE no symlink privilege; third-party reparse tag used for " + link);
}
try
{
    var monitor = Monitor(root);
    string directory = Path.GetDirectoryName(monitor.WorkingPath)!;
    string identityHex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(Path.Combine(root, "synthetic.ins")).ToUpperInvariant())));
    Check(Path.GetDirectoryName(directory) == Path.Combine(Path.GetFullPath(root), identityHex) &&
          Guid.TryParseExact(Path.GetFileName(directory), "N", out _) && Path.GetFileName(monitor.WorkingPath) == "Plan.pdf" &&
          monitor.StartInfo.FileName == Path.Combine(directory, "Plan.pdf"),
        "working copy is {root}\\{identity}\\{session}\\{Filename} (Plan.pdf), and that is the path the PDF app opens");
    int room = PdfEditMonitor.MaxWorkingPath - directory.Length - 1 - ".pdf".Length; // characters left for a stem
    var capped = Monitor(root, new string('L', room - 1) + " " + new string('M', 240 - room - 4) + ".pdf");
    string cappedSession = Path.GetDirectoryName(capped.WorkingPath)!;
    Check(capped.Filename.Length == 240 && capped.WorkingPath.Length <= PdfEditMonitor.MaxWorkingPath &&
          Path.GetFileName(capped.WorkingPath) == new string('L', room - 1) + ".pdf" && File.Exists(capped.WorkingPath),
        $"a 240-character name is cut to fit MAX_PATH ({capped.WorkingPath.Length} chars), keeping its start and .pdf, never a trailing space");
    capped.Cleanup();
    Check(!Directory.Exists(cappedSession), "the cut working copy is leased and retired like any other");
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

    string eligible = Owned(pdf: "1234567R2 Slab Eng.pdf");
    string legacy = Owned(pdf: "Orientation.pdf");
    Check(PdfWorkingSession.Scavenge(root, Path.Combine(sandbox, "different", "PdfAttachments"), now) == 0 && Directory.Exists(eligible) &&
          Directory.Exists(legacy), "mismatched root cannot scavenge");
    PdfWorkingSession.Scavenge(root, now);
    Check(Directory.Exists(eligible) && Directory.Exists(legacy), "production entry point rejects a custom root");
    string fresh = Owned(); File.SetLastWriteTimeUtc(Path.Combine(fresh, "Plan.pdf"), now.UtcDateTime);
    string unmarked = Owned(); File.Delete(Path.Combine(unmarked, PdfWorkingSession.LockName));
    string badMarker = Owned(); File.WriteAllText(Path.Combine(badMarker, PdfWorkingSession.LockName), "not RED"); Age(badMarker);
    string badIdentity = Owned(new string('G', 64));
    string shortIdentity = Owned(new string('A', 63));
    string badSession = Owned(name: new string('Z', 32));
    string shortSession = Owned(name: new string('a', 31));
    string unknown = Owned(); File.WriteAllText(Path.Combine(unknown, "other.txt"), "keep"); Age(unknown);
    string nested = Owned(); Directory.CreateDirectory(Path.Combine(nested, "nested")); Age(nested);
    string boundary = Owned(); File.SetLastWriteTimeUtc(Path.Combine(boundary, "Plan.pdf"), now.Subtract(PdfWorkingSession.Retention).UtcDateTime);
    string twoPdfs = Owned(pdf: "A.pdf"); File.WriteAllBytes(Path.Combine(twoPdfs, "B.pdf"), Pdf("second")); Age(twoPdfs);
    string notPdf = Owned(pdf: "Plan.pdf.bak");
    string unsafeName = Owned(pdf: " Plan.pdf"); // a leading space fails SafeFilename
    string source = Path.Combine(sandbox, "Documents"); Directory.CreateDirectory(source);
    string sourceFile = Path.Combine(source, "source.pdf"); File.WriteAllBytes(sourceFile, changed);
    string identityLink = Path.Combine(root, new string('B', 64)); DirectoryLink(identityLink, source);
    string sessionLink = Path.Combine(root, new string('A', 64), new string('C', 32)); DirectoryLink(sessionLink, source);
    string fileLink = Owned(); File.Delete(Path.Combine(fileLink, "Plan.pdf"));
    FileLink(Path.Combine(fileLink, "Plan.pdf"), sourceFile);
    string legacyLink = Owned(pdf: "Orientation.pdf"); File.Delete(Path.Combine(legacyLink, "Orientation.pdf"));
    FileLink(Path.Combine(legacyLink, "Orientation.pdf"), sourceFile);
    string markerLink = Owned(); File.Delete(Path.Combine(markerLink, PdfWorkingSession.LockName));
    FileLink(Path.Combine(markerLink, PdfWorkingSession.LockName), sourceFile);
    Fails(() => { using var lease = PdfWorkingSession.OpenRetirementLease(Path.Combine(fileLink, "Plan.pdf")); },
        "retirement lease itself refuses a final reparse point, not only scavenger filtering");
    int scavenged = PdfWorkingSession.Scavenge(root, root, now);
    Check(scavenged == 2 && !Directory.Exists(eligible) && !Directory.Exists(legacy),
        $"only old unlocked owned sessions with one safe-named PDF are scavenged, an attachment-named one and a legacy Orientation.pdf one (count={scavenged})");
    foreach (var item in new[] { fresh, unmarked, badMarker, badIdentity, shortIdentity, badSession, shortSession, unknown, nested, boundary,
                                 twoPdfs, notPdf, unsafeName, identityLink, sessionLink, fileLink, legacyLink, markerLink })
        Check(Directory.Exists(item), "conservative scavenging preserves " + Path.GetFileName(item));
    Check(File.Exists(Path.Combine(twoPdfs, "A.pdf")) && File.Exists(Path.Combine(twoPdfs, "B.pdf")) &&
          File.Exists(Path.Combine(notPdf, "Plan.pdf.bak")) && File.Exists(Path.Combine(unsafeName, " Plan.pdf")),
        "a second PDF, a non-PDF or an unsafe name keeps every file");
    Check(File.ReadAllBytes(sourceFile).SequenceEqual(changed) && Directory.Exists(root), "scavenging preserves source bytes and root");
    string rootLink = Path.Combine(sandbox, "linked", "PdfAttachments"); Directory.CreateDirectory(Path.GetDirectoryName(rootLink)!);
    DirectoryLink(rootLink, root);
    Check(PdfWorkingSession.Scavenge(rootLink, rootLink, now) == 0, "symlink root rejected without traversal");
    string ancestor = Path.Combine(sandbox, "ancestor"); DirectoryLink(ancestor, Path.Combine(sandbox, "RED"));
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
finally
{
    // Remove each link itself first: a recursive delete reports unmounting a junction as access denied.
    foreach (string link in directoryLinks) if (Directory.Exists(link)) Directory.Delete(link);
    Directory.Delete(sandbox, true);
}

static class Reparse
{
    // FSCTL_SET_REPARSE_POINT with a REPARSE_GUID_DATA_BUFFER: a non-Microsoft tag needs only write access, no privilege.
    internal static void Tag(string path)
    {
        using var handle = CreateFile(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero); // GENERIC_WRITE, OPEN_REPARSE_POINT
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception();
        var buffer = new byte[24 + 4];
        BitConverter.GetBytes(0x00001234u).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)4).CopyTo(buffer, 4);
        Guid.NewGuid().ToByteArray().CopyTo(buffer, 8);
        if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception();
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, byte[] input, int inputLength,
        IntPtr output, int outputLength, out int returned, IntPtr overlapped);
}
