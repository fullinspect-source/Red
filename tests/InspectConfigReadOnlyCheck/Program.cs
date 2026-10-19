using System.Diagnostics;
using System.Security.Cryptography;
using InspectionEditor.Services;

// F-003 V2: the read-only proof against INSPECT's real config files on this PC. It records the SHA-256, size and
// LastWriteTime of each file, runs RED's import and live reads against the real files, and checks that every file, and
// the entries next to it, are unchanged afterwards. It never writes. It prints labels and results only: no path, value,
// password or signature.
int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

bool inspectRunning = Process.GetProcesses()
    .Any(p => p.Id != Environment.ProcessId && p.ProcessName.StartsWith("Inspect", StringComparison.OrdinalIgnoreCase) &&
              !p.ProcessName.Equals("InspectConfigReadOnlyCheck", StringComparison.OrdinalIgnoreCase));
Console.WriteLine(inspectRunning
    ? "Note: INSPECT is running. It rewrites its config at start and after Settings, so a change could be its own."
    : "INSPECT is not running.");

string userConfig = StrandCredentialService.UserConfigPath;
var user = StrandCredentialService.ReadInspectUserConfig(userConfig);
string configured = user == null ? "" : StrandCredentialService.GetString(user, "InspectorFolder");
string? inspections = StrandCredentialService.SelectInspectorFolder(configured, StrandCredentialService.DefaultInspectorFolders());
Check(inspections != null && Directory.Exists(inspections), "found INSPECT's Inspections folder");
if (inspections == null)
    return 1;

var files = new (string Label, string Path)[]
{
    ("UserConfig config.json", userConfig),
    ("UserConfig config.json.bak", userConfig + ".bak"),
    ("FormConfig config.json", Path.Combine(inspections, "config.json")),
    ("FormConfig config.json.bak", Path.Combine(inspections, "config.json.bak")),
};
var folders = new (string Label, string Path)[]
{
    ("UserConfig folder", Path.GetDirectoryName(userConfig)!),
    ("Inspections folder (top level)", inspections),
};

var before = files.ToDictionary(f => f.Label, f => Fingerprint(f.Path));
var entriesBefore = folders.ToDictionary(f => f.Label, f => Entries(f.Path));
foreach (var (label, _) in files)
    Console.WriteLine($"      {label}: {(before[label] == null ? "absent" : "present")}");

// RED's reads, three times each: the full import (as Import from INSPECT and the first-run import read it), the
// import for RED's selected folder, the live Archive retention, and what ORDERS reads.
bool imported = false;
int retention = 0;
for (int run = 0; run < 3; run++)
{
    imported |= InspectorProfileService.ReadFromInspect().Profile != null;
    InspectorProfileService.ReadFromInspect(inspections);
    retention = InspectLiveSettings.ReadArchiveRetention(inspections);
    StrandCredentialService.ReadInspectSetting("URL");
    StrandCredentialService.ImportFromInspect();
}
Check(imported, "RED's import read INSPECT's settings");
Console.WriteLine($"      {InspectLiveSettings.ArchiveRetentionLine(retention, DateTime.Today)}");

foreach (var (label, path) in files)
{
    var after = Fingerprint(path);
    Check(Equals(before[label], after), $"{label}: {(after == null ? "still absent" : "SHA-256, size and LastWriteTime unchanged")}");
}
foreach (var (label, path) in folders)
    Check(entriesBefore[label].SetEquals(Entries(path)), $"{label}: no entry added or removed");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

static Fingerprint? Fingerprint(string path)
{
    if (!File.Exists(path))
        return null;
    // Shared read/write, as RED and INSPECT open these files.
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return new Fingerprint(Convert.ToHexString(SHA256.HashData(stream)), stream.Length, File.GetLastWriteTimeUtc(path));
}

static HashSet<string> Entries(string folder) =>
    Directory.Exists(folder)
        ? new HashSet<string>(Directory.EnumerateFileSystemEntries(folder), StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>();

record Fingerprint(string Sha256, long Length, DateTime LastWriteUtc);
