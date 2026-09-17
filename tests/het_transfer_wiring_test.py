from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
CODE=(ROOT/'MainWindow.xaml.cs').read_text()
class HetTransferWiring(unittest.TestCase):
 def test_explicit_click_rechecks_and_confirms(self):
  s=CODE[CODE.index('private UIElement? CreateInlineHetMeasuredChip'):CODE.index('private UIElement? CreateInlineDesignAssistChip')]
  self.assertEqual(s.count('HetMeasuredTransferService.Resolve('),3)
  self.assertIn('if (fresh != proposal)',s)
  self.assertIn('confirmed != fresh',s)
  self.assertIn('MessageBoxResult.No',s)
  self.assertIn('EditorEditService.Owns(_currentInspection, item)',s)
  self.assertIn('SetInlineItemValue(item, fresh.Value!)',s)
  self.assertIn('Calculate: {proposal.Value}%',s)
  self.assertIn('Copy HET: {proposal.Value}',s)
  self.assertIn('proposal.Detail.Contains("WARNING")',s)
 def test_refreshes_calculation_without_active_row_rebuild(self):
  start=CODE.index('private void CaptureValueEdit');s=CODE[start:CODE.index('private ',start+15)]
  self.assertIn('item.Number is "1.5" or "1.6" or "1.7"',s)
  self.assertIn('RefreshInlineItemRow(calculatedRow)',s)
  self.assertNotIn('RefreshInlineItemRow(item)',s)
  self.assertIn('i.Number == "1.8"',s)
 def test_buttons_are_wired_into_inline_header(self):
  self.assertIn('var hetChip = CreateInlineHetMeasuredChip(item);',CODE)
  self.assertIn('if (hetChip != null) chips.Children.Add(hetChip);',CODE)
if __name__=='__main__':unittest.main()
