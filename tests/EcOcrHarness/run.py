#!/usr/bin/env python3
"""Execute the actual production pure helpers with real OCR evidence, on any OS.
Usage: python3 tests/EcOcrHarness/run.py /path/to/evidence
Evidence: ocr.txt plus streams/*.txt from actual PDF/Tesseract reproduction.
Generated C# and build products live in a temporary directory, not the WPF tree.
"""
import pathlib, subprocess, sys, tempfile
repo = pathlib.Path(__file__).resolve().parents[2]
source = (repo / "Services/EnergyComplianceService.cs").read_text()
a = source.index("        internal static bool IsPdfSoftMaskCandidate")
b = source.index("        private static Bitmap CreatePdfImageInkBitmap", a)
c = source.index("        internal static string BestEcPageText")
d = source.index("        private static bool LooksLikeUsefulEcOcrText", c)
helpers = source[a:b] + source[c:d]
evidence = pathlib.Path(sys.argv[1]).resolve()
program = r'''using System; using System.IO; using System.Linq; using System.Collections.Generic; using System.Text.RegularExpressions;
class Program {
HELPERS
static void Main(string[] args) {
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
Check(!IsPdfSoftMaskCandidate(5,100), "ordinary white scan retains normal preprocessing");
Check(IsPdfSoftMaskCandidate(66,100), "exact PDF dark stream profile permits mask recovery");
Check(!IsPdfSoftMaskCandidate(0,0), "empty sample safe");
string equipment = "Equipment Cooling Flow Rate Cfm 720\nHeating Duct Water";
Check(BestEcPageText(equipment, new[]{equipment, equipment}) == equipment, "alternate color and mask do not duplicate equipment rows");
Check(BestEcPageText(equipment, new[]{"", "garbage"}) == equipment, "failed recovery preserves readable page");
string root=args[0];
string rendered=File.ReadAllText(Path.Combine(root,"ocr.txt"));
Check(EcOcrQuality(rendered)>200,"real rendered report readable");
string old=File.ReadAllText(Path.Combine(root,"streams/p1-5-exact-old-ink.txt"));
Check(old.Contains("House Tightness") && old.Contains("Ventilation") && !old.Contains("Conditioned Floor Area"), "exact old stream reproduces false global success with missing core label");
string normal=File.ReadAllText(Path.Combine(root,"streams/p1-5-normal.txt"));
foreach(int deg in new[]{90,180,270}) {
string rotated=File.ReadAllText(Path.Combine(root,$"streams/p1-5-normal-r{deg}.txt"));
Console.WriteLine($"rotation {deg}: {EcOcrQuality(rotated)}; upright: {EcOcrQuality(normal)}");
Check(EcOcrQuality(normal)>EcOcrQuality(rotated),$"actual upright OCR outranks rotation {deg}"); }
string dark=File.ReadAllText(Path.Combine(root,"streams/p2-9-normal.txt"));
string ink=File.ReadAllText(Path.Combine(root,"streams/p2-9-old-ink.txt"));
Check(EcOcrQuality(ink)>EcOcrQuality(dark),"actual soft-mask recovery improves text");
Check(rendered.Contains("9,248") && rendered.Contains("1,156"),"real rendered evidence contains volume and floor area");
} }
'''.replace("HELPERS",helpers)
with tempfile.TemporaryDirectory(prefix="red-ec-ocr-") as tmp:
    p=pathlib.Path(tmp)
    (p/"Harness.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
    (p/"Program.cs").write_text(program)
    subprocess.run(["dotnet","run","--project",str(p/"Harness.csproj"),"--",str(evidence)],check=True)
