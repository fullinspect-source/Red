from pathlib import Path
import re
root=Path(__file__).resolve().parents[2]
s=(root/'Services/EnergyComplianceService.cs').read_text()
# Compile exact production model and relevant methods without native PDF/OCR dependencies.
info=s[s.index('    public class EnergyComplianceInfo'):s.index('    public static class EnergyComplianceService')]
names=['NormalizeCode','GetValueForField','GetLabelForField','ApplySingleItem','ApplyToInspection','SetItemValue','BestLookupMatch']
methods=[]
for name in names:
 for m in re.finditer(r'^        (?:public|private|internal) static [^\n]+\b'+name+r'\(',s,re.M):
  start=m.start(); brace=s.index('{',m.end()); depth=1; pos=brace+1
  while depth:
   if s[pos]=='{': depth+=1
   elif s[pos]=='}': depth-=1
   pos+=1
  if s[pos:pos+1]==';':pos+=1
  methods.append(s[start:pos])
out='#nullable enable\nusing InspectionEditor.Models; using System; using System.Linq; using System.Collections.Generic; using System.Text.RegularExpressions;\nnamespace InspectionEditor.Services {\n'+info+'public static class EnergyComplianceService {\n'+'\n'.join(methods)+'\n} public static class ExtractionMappingService { public static string NormalizeFieldKey(string? s) => Regex.Replace((s??"").ToUpperInvariant(), "[^A-Z0-9]", ""); } }'
(Path(__file__).parent/'Production.Generated.cs').write_text(out)
