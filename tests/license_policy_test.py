#!/usr/bin/env python3
"""Compile actual production policy/signature methods without Windows/tamper side effects."""
from pathlib import Path
import os
import platform
import subprocess
import sys
import tempfile

root = Path(__file__).resolve().parents[1]
source = (root / 'Services/LicenseService.cs').read_text()

def extract(marker):
    start = source.index(marker)
    opening = source.index('{', start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]

assert 'if (expDate.Date > MaximumAuthorizedExpiration(license.IssuedTo, DateTime.Today))' in source
assert 'daysRemaining > 90' not in source
assert 'StringComparison.OrdinalIgnoreCase' in source
assert 'if (daysRemaining < 0)' in source
program = '''using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
class LicenseInfo {
 public string MachineId { get; set; } = "";
 public string ExpirationDate { get; set; } = "";
 public string IssuedTo { get; set; } = "";
 public string Signature { get; set; } = "";
}
static class Production {
 private const string PublicKey = "REDLicensePublicKey2026";
''' + extract('public static DateTime MaximumAuthorizedExpiration') + '\n' + extract('private static bool VerifySignature') + '''
 public static bool Verify(LicenseInfo value) => VerifySignature(value);
}
class Program {
 static int checks;
 static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
 static void Main(string[] files) {
  var now = new DateTime(2026,10,23);
  foreach (var name in new[] {"Ronnie Shorthose", "Sam Melton", "Trent Fuller", "Trent Fuller (Main Tablet)", "Trent Fuller (Test Laptop)"}) {
   var maximum = Production.MaximumAuthorizedExpiration(name, now);
   Check(maximum == new DateTime(2028,10,23), "two-year exact date");
   Check(new DateTime(2028,10,24) > maximum, "over-two-years refused");
   Check(new DateTime(2027,1,21) <= maximum, "prior ninety-day license remains accepted");
  }
  foreach (var name in new[] {"Other Inspector", "Trent Fuller Jr", "", "Sam Another"}) {
   var maximum = Production.MaximumAuthorizedExpiration(name, now);
   Check(maximum == new DateTime(2027,4,23), "six-month exact date");
   Check(new DateTime(2027,4,24) > maximum, "over-six-months refused");
  }
  Check(Production.MaximumAuthorizedExpiration("Other Inspector", new DateTime(2026,8,31)) == new DateTime(2027,2,28), "month-end clamping");
  Check(Production.MaximumAuthorizedExpiration("Sam Melton", new DateTime(2024,2,29)) == new DateTime(2026,2,28), "leap-year clamping");
  foreach (var file in files) {
   var license = JsonSerializer.Deserialize<LicenseInfo>(File.ReadAllText(file))!;
   Check(Production.Verify(license), "actual issued-file signature");
   Check(DateTime.Parse(license.ExpirationDate) <= Production.MaximumAuthorizedExpiration(license.IssuedTo,now), "actual issued-file policy acceptance");
   license.ExpirationDate = "2030-10-23";
   Check(!Production.Verify(license), "edited date signature refused");
  }
  Console.WriteLine($"PASS {checks} production-method checks; {files.Length} actual issued files; no Windows/network/tamper paths executed.");
 }
}
'''
scratch = Path(os.environ.get('TMPDIR') or (Path.home() / '.hermes/cache/scratch'))
scratch.mkdir(parents=True, exist_ok=True)
rid = ('osx-arm64' if platform.machine() == 'arm64' else 'osx-x64') if sys.platform == 'darwin' else ('win-x64' if os.name == 'nt' else 'linux-x64')
with tempfile.TemporaryDirectory(prefix='red-license-policy-', dir=scratch) as tmp:
    work = Path(tmp)
    (work/'Policy.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
    (work/'Program.cs').write_text(program)
    subprocess.run(['dotnet','run','--project',str(work/'Policy.csproj'),'-c','Release','-r',rid,'--self-contained','true','--',*sys.argv[1:]],check=True)
