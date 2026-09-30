"""Regression for inspection-report labels in RED's Docs viewer."""
from pathlib import Path
import re
import unittest

SOURCE = Path(__file__).resolve().parents[1] / 'DocsViewerWindow.xaml.cs'


def display_name(code, trip):
    text = SOURCE.read_text(encoding='utf-8')
    mapping = re.search(r'InspectionCodeNames\s*=\s*new Dictionary<[^>]+>\([^)]*\)\s*\{(.*?)\n\s*\};', text, re.S)
    if not mapping:
        raise AssertionError('Docs viewer inspection mapping not found')
    labels = dict(re.findall(r'\{"([A-Z]+)",\s*"([^"]+)"\}', mapping.group(1)))
    return f'{labels.get(code, code)} {trip}'


class DocsViewerCohLabelTest(unittest.TestCase):
    def test_coh_report_is_framing_not_bmep(self):
        self.assertEqual(display_name('COH', 1), 'Flashing Sheathing Framing (COH) 1')
        self.assertEqual(display_name('COH', 2), 'Flashing Sheathing Framing (COH) 2')

    def test_bmep_and_other_framing_labels_unchanged(self):
        self.assertEqual(display_name('ME', 1), 'BMEP Rough 1')
        self.assertEqual(display_name('MP', 1), 'BMEP Rough 1')
        self.assertEqual(display_name('FSF', 1), 'Flash Sheath Frame 1')


if __name__ == '__main__':
    unittest.main()
