import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
MAIN = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8")


class RevisionFilenameRegressionTests(unittest.TestCase):
    def test_compact_strand_revision_pattern_is_checked(self):
        self.assertIn(r'@"\d{5,}R(\d+)(?=[A-Za-z])"', MAIN)

    def test_compact_pattern_precedes_generic_revision_fallback(self):
        compact = MAIN.index(r'@"\d{5,}R(\d+)(?=[A-Za-z])"')
        generic = MAIN.index(r'@"(?:^|[^A-Za-z])R(?:EV(?:ISION)?)?\s*[-_. ]?(\d+)\b"')
        self.assertLess(compact, generic)

    def test_compact_examples_resolve_expected_revision(self):
        # Python equivalent of the C# compact filename expression. These are the
        # production filename shapes that previously displayed R0.
        pattern = re.compile(r"\d{5,}R(\d+)(?=[A-Za-z])", re.IGNORECASE)
        examples = {
            "2528605R3EC": 3,
            "2528605R2FFP": 2,
            "3336 Daily Harvest Drive (2528605R1EL)": 1,
        }
        for filename, expected in examples.items():
            with self.subTest(filename=filename):
                match = pattern.search(filename)
                if match is None:
                    self.fail(f"Compact revision pattern did not match {filename}")
                self.assertEqual(expected, int(match.group(1)))


if __name__ == "__main__":
    unittest.main()
