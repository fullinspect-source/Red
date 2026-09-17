import pathlib, unittest
ROOT = pathlib.Path(__file__).resolve().parents[1]
class EcMismatchUiTests(unittest.TestCase):
    def test_steady_pink_is_shared(self):
        s = (ROOT/'MainWindow.xaml.cs').read_text()
        self.assertIn('Color.FromRgb(255, 77, 196)', s)
        colors = s[s.index('private void ApplyEcBannerColors'):s.index('private void FlashBannerRed')]
        self.assertIn('? EcMismatchBrush', colors)
        self.assertNotIn('FlashBannerRed(EcDataHeaderBorder', colors)
    def test_typing_recolors_without_sources_or_checklist_rebuild(self):
        s = (ROOT/'MainWindow.xaml.cs').read_text()
        capture = s[s.index('private void CaptureValueEdit'):s.index('private void MirrorInlineEdit')]
        self.assertIn('RefreshEcMismatchVisuals(item);', capture)
        self.assertIn('RefreshEcDataPanel(refreshSources: false)', capture)
        recolor = s[s.index('private void RefreshEcMismatchVisuals'):s.index('private bool HasEcDesignMismatch')]
        self.assertNotIn('PopulateInlineChecklist', recolor)
        self.assertNotIn('LoadItemToEditor', recolor)
        panel = s[s.index('private void RefreshEcDataPanel'):s.index('private void ApplyEcBannerColors')]
        self.assertIn('if (refreshSources && info != null) TestingTargetsService.Refresh', panel)
        self.assertIn('if (refreshSources && info != null && (currentCode', panel)
    def test_compare_is_resolved_and_does_not_write_answer(self):
        s = (ROOT/'Services/EnergySemanticMappingService.cs').read_text()
        compare = s[s.index('public static bool IsDesignMismatch'):s.index('private static string N(')]
        self.assertIn('resolved?.CanApply != true', compare)
        self.assertNotIn('item.Value =', compare)
        self.assertNotIn('StatusId =', compare)
if __name__ == '__main__': unittest.main()
