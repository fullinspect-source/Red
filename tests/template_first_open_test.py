"""Compile and execute production C# first-open/template methods with UI-only stubs.
All report files and user state are scratch fixtures; no live INS is changed.
"""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRATCH = Path.home() / '.hermes/cache/scratch'

def method(src, signature):
    start = src.index(signature)
    brace = src.index('{', start)
    depth = 1
    end = brace + 1
    while depth:
        if src[end] == '{': depth += 1
        elif src[end] == '}': depth -= 1
        end += 1
    return src[start:end]

class TemplateFirstOpenTests(unittest.TestCase):
    def test_load_stage_wiring(self):
        src = (ROOT / 'MainWindow.xaml.cs').read_text()
        load = method(src, 'private async void LoadInspectionFileAsync(')
        self.assertEqual(load.count('OfferSavedTemplateForBlankReport(isFirstTrip);'), 1)
        self.assertLess(load.index('_currentInspection = await'), load.index('OfferSavedTemplateForBlankReport(isFirstTrip);'))
        self.assertLess(load.index('OfferSavedTemplateForBlankReport(isFirstTrip);'), load.rindex('PopulateTreeView();'))
        self.assertIn('isFirstTrip = tripCount == 1;', load)
        offer = method(src, 'private void OfferSavedTemplateForBlankReport(')
        self.assertNotIn('File.', offer)
        self.assertNotIn('LastEditTime', offer)

    def test_production_csharp(self):
        src = (ROOT / 'MainWindow.xaml.cs').read_text()
        if 'private void OfferSavedTemplateForBlankReport(' in src:
            methods = method(src, 'private void OfferSavedTemplateForBlankReport(')
            methods += method(src, 'private bool InspectionHasTemplateRelevantData()')
            links = ['Models/InspectionModels.cs', 'Models/TemplateModels.cs', 'Services/TemplateService.cs']
        else:
            # Real pre-fix gate and detector, not a Python reimplementation.
            start = src.index('            bool reportAlreadyFilled = InspectionHasTemplateRelevantData();')
            end = src.index('            // Reset filter state', start)
            methods = 'private void OfferSavedTemplateForBlankReport(bool isFirstTrip) {\n' + src[start:end] + '\n}'
            methods += method(src, 'private bool InspectionHasTemplateRelevantData()')
            links = ['Models/InspectionModels.cs', 'Models/TemplateModels.cs', 'Services/TemplateService.cs']
        methods += method(src, 'private static bool IsProperNameItem(')
        if 'private static bool IsAdministrativeTemplateDefault(' in src:
            methods += method(src, 'private static bool IsAdministrativeTemplateDefault(')
        methods += method(src, 'private void PromptApplyTemplate(')
        methods += method(src, 'private void ApplyInspectionTemplate()')
        methods += method(src, 'private void ApplyTemplateButton_Click(')
        load_src = (ROOT / 'Services/SurgicalSaveService.cs').read_text()
        load_methods = method(load_src, 'public InspectionFile Load(') + method(load_src, 'private void MapPictureFieldsFromIns(')
        legacy_src = (ROOT / 'Services/UserDataService.cs').read_text()
        legacy_methods = method(legacy_src, 'public Dictionary<string, string> GetTemplate(') + method(legacy_src, 'public bool HasTemplate(')
        program = (ROOT / 'tests/template_first_open_harness.cs.txt').read_text().replace('/*PRODUCTION_METHODS*/', methods).replace('/*PRODUCTION_LOAD_METHODS*/', load_methods).replace('/*PRODUCTION_LEGACY_METHODS*/', legacy_methods)
        with tempfile.TemporaryDirectory(dir=SCRATCH) as tmp:
            d = Path(tmp)
            (d / 'Program.cs').write_text(program)
            includes = ''.join(f'<Compile Include="{ROOT / f}" Link="{Path(f).name}" />' for f in links)
            (d / 'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><RollForward>Major</RollForward><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><PackageReference Include="Newtonsoft.Json" Version="13.0.3" />' + includes + '</ItemGroup></Project>')
            r = subprocess.run(['dotnet', 'run', '--project', str(d / 'Probe.csproj')], capture_output=True, text=True)
            print(r.stdout.strip())
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

if __name__ == '__main__':
    unittest.main()
