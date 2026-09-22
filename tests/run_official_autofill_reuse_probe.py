#!/usr/bin/env python3
"""Run real manager/walk reuse callbacks and PDF services, with UI/shell-only seams.

Builds under a temporary directory, never regenerates or changes another probe.
No default PDF editor is launched. All INS/PDF writes stay in temporary fixtures.
"""
from pathlib import Path
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
HARNESS = ROOT / "tests/OrientationPdfHarness"

with tempfile.TemporaryDirectory(prefix="red-official-reuse-") as temporary:
    temp = Path(temporary)
    project = ET.parse(HARNESS / "OrientationPdfHarness.csproj")
    for item in project.findall(".//Compile"):
        item.set("Include", str((HARNESS / item.attrib["Include"]).resolve()))
    group = ET.SubElement(project.getroot(), "ItemGroup")
    for path in [HARNESS / "AutofillProbe.cs", ROOT / "tests/OfficialAutofillReuseProbe.cs"]:
        ET.SubElement(group, "Compile", Include=str(path))
    project.write(temp / "Reuse.csproj")

    # Reuse UI-only stubs, excluding their existing scenario and duplicate walk fields.
    stubs = (HARNESS / "AttachmentManagerProbe.cs").read_text().split("        public static void RunAttachmentManagerProbe(")[0]
    stubs = stubs.replace(", _finishingOrientationPdf;", ";")
    stubs = stubs.replace("        private PdfEditMonitor? _orientationPdf;\n", "")
    stubs = stubs.replace("        private void UpdateOrientationPdfControls() { }\n", "")
    stubs = stubs.replace("public static void Show(string text, string title, MessageBoxButton button, MessageBoxImage image) { }", "public static string Last = \"\"; public static void Show(string text, string title, MessageBoxButton button, MessageBoxImage image) { Last = text; }")
    stubs = stubs.replace("private bool TrySaveCurrentInspection() { SaveCount++; return !SaveFails; }", "private bool TrySaveCurrentInspection() { SaveCount++; if (SaveFails) return false; ProbeSaver.Save(_currentInspection!); return true; }")
    stubs += """
        private SurgicalSaveService ProbeSaver = null!;
        private int LaunchCount;
        private sealed class Control { public System.Windows.Visibility Visibility; public bool IsEnabled; public string Text = ""; public object? ToolTip; }
        private readonly Control OrientationPdfPanel = new(), OpenOrientationPdfButton = new(), AttachmentsButton = new(), OrientationPdfHint = new();
    }
}
namespace System.Windows { public enum Visibility { Visible, Collapsed } }
"""
    (temp / "Stubs.cs").write_text(stubs)
    for filename, expression in [("MainWindow.Attachments.cs", "monitor"), ("MainWindow.OrientationPdf.cs", "_orientationPdf")]:
        source = (ROOT / filename).read_text()
        original = f"using var process = Process.Start({expression}.StartInfo);"
        assert source.count(original) == 1, "Shell seam changed: " + filename
        source = source.replace(original, f"_ = {expression}.StartInfo; LaunchCount++;")
        source = source.replace('Path.Combine(AppIdentity.LocalAppDataPath, "PdfAttachments")', "ProbeRoot")
        (temp / filename).write_text(source)
    (temp / "Program.cs").write_text('''
int passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
string root = Path.Combine(Path.GetTempPath(), "red-official-reuse-fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try { InspectionEditor.MainWindow.RunOfficialReuseProbe(root, Check); Console.WriteLine($"{passed} official autofill reuse checks passed"); }
finally { Directory.Delete(root, true); }
''')
    subprocess.run(["dotnet", "run", "--project", str(temp / "Reuse.csproj")], cwd=ROOT, check=True)
