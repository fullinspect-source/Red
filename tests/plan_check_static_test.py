import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]
MAIN = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8")
XAML = (ROOT / "MainWindow.xaml").read_text(encoding="utf-8")
SERVICE = (ROOT / "Services" / "PlanCheckService.cs").read_text(encoding="utf-8")
SAVE = (ROOT / "Services" / "SurgicalSaveService.cs").read_text(encoding="utf-8")
PROJECT = (ROOT / "InspectionEditor.csproj").read_text(encoding="utf-8")


class PlanCheckStaticTests(unittest.TestCase):
    def test_cpp_only_entry_point_and_embedded_pdf_gate(self):
        self.assertIn('x:Name="PlanCheckButton"', XAML)
        self.assertIn('code.Equals("CPP"', MAIN)
        self.assertIn("GetEmbeddedPdfAttachments", MAIN)
        self.assertIn('obj.Value<string>("FileData")', SERVICE)

    def test_five_checks_require_inspector_completion(self):
        for check_id in ("steel", "beam", "slab", "hold-down", "cables"):
            self.assertIn(f'new("{check_id}"', SERVICE)
        window = (ROOT / "PlanCheckWindow.xaml.cs").read_text(encoding="utf-8")
        self.assertIn("_findings.Count == 5", window)
        self.assertIn("PlanCheckState.Incomplete", window)

    def test_optional_outputs_and_surgical_persistence(self):
        self.assertIn("CreateDeficiencyCrop", SERVICE)
        self.assertIn("CreateAnnotatedPdf", SERVICE)
        self.assertIn("PatchAddedAttachments", SAVE)
        self.assertIn("for (int i = originalAttachments.Count; i < inspection.Attachments.Count; i++)", SAVE)
        self.assertIn("PatchPlanCheckMetadata", SAVE)

    def test_review_safety_guards(self):
        window = (ROOT / "PlanCheckWindow.xaml.cs").read_text(encoding="utf-8")
        self.assertIn("_attachmentLoadGeneration", window)
        self.assertIn("generation != _attachmentLoadGeneration", window)
        self.assertIn("PlanCheckState.NotApplicable", window)
        self.assertIn("SourceAttachmentFilename = _attachment.Filename", window)
        marker = window.split("private void Marker_MouseLeftButtonDown", 1)[1].split("private void CycleFinding", 1)[0]
        self.assertNotIn("CycleFinding(finding)", marker)
        self.assertIn('TryGetValue("RedPlanCheck"', SAVE)
        self.assertNotIn('new[] { "beam", "grade beam", "width", "depth" }', SERVICE)

    def test_release_dependency_and_version(self):
        self.assertIn('PackageReference Include="PDFsharp" Version="6.2.0"', PROJECT)
        self.assertIn("<Version>2.1.23</Version>", PROJECT)
        self.assertIn("<ReleaseDate>2026-09-01</ReleaseDate>", PROJECT)


if __name__ == "__main__":
    unittest.main()