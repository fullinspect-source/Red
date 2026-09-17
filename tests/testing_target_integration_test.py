"""WPF integration guards; executable target semantics live in the C# harness."""
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
MAIN = (ROOT / 'MainWindow.xaml.cs').read_text()
TARGETS = (ROOT / 'Services/TestingTargetsService.cs').read_text()
EC = (ROOT / 'Services/EnergyComplianceService.cs').read_text()

class TestingTargetIntegrationTests(unittest.TestCase):
    def test_background_completion_checks_generation_and_object(self):
        self.assertIn('capturedEcRequestId != _ecExtractionRequestId', MAIN)
        self.assertIn('!ReferenceEquals(capturedInspection, _currentInspection)', MAIN)

    def test_initial_targets_exist_before_ocr_task(self):
        start = MAIN.index('var capturedInspection = _currentInspection;')
        end = MAIN.index('Task.Run(() =>', start)
        self.assertIn('TestingTargetsService.Refresh(_currentEcInfo, _currentInspection)', MAIN[start:end])

    def test_fallback_is_not_a_new_ec_success(self):
        model = EC[EC.index('public bool IsLoaded =>'):EC.index('// ── Derived targets')]
        self.assertNotIn('TestingTargets', model)

    def test_structured_target_service_never_reads_other_reports(self):
        self.assertNotIn('File.Read', TARGETS)
        self.assertNotIn('Directory.', TARGETS)
        self.assertNotIn('SetItemValue', TARGETS)

    def test_measurement_target_badges_are_display_only(self):
        semantic = (ROOT / 'Services/EnergySemanticMappingService.cs').read_text()
        self.assertIn('Actual values are NEVER writable from targets', semantic)
        self.assertIn('apply = false', semantic)
        self.assertIn('CanApply: resolved.CanApply', MAIN)
        self.assertIn('fresh == null || !fresh.CanApply', MAIN)

if __name__ == '__main__':
    unittest.main()
