import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CLIENT = (ROOT / "Services" / "GrokApiClient.cs").read_text()
PROJECT = (ROOT / "InspectionEditor.csproj").read_text()


class Gemini37RoutingStaticTests(unittest.TestCase):
    def test_all_primary_red_ai_routes_use_gemini_37_flash(self):
        self.assertIn('private const string FAST_MODEL = "gemini-3.7-flash";', CLIENT)
        self.assertIn('private const string CAREFUL_MODEL = "gemini-3.7-flash";', CLIENT)
        self.assertIn('private const string TRANSCRIBE_MODEL = "gemini-3.7-flash";', CLIENT)
        self.assertNotIn('gemini-3.1-flash-lite', CLIENT)
        self.assertNotIn('gemini-3.5-flash', CLIENT)

    def test_gemini_25_flash_remains_the_explicit_fallback(self):
        self.assertIn('private const string FAST_LEGACY_MODEL = "gemini-2.5-flash";', CLIENT)
        self.assertIn('private const string CAREFUL_LEGACY_MODEL = "gemini-2.5-flash";', CLIENT)

    def test_fast_and_transcribe_requests_hard_code_low_thinking(self):
        self.assertIn('private const string FAST_THINKING_LEVEL = "low";', CLIENT)
        self.assertRegex(
            CLIENT,
            r'MakeApiRequestWithTimeout\(\s*prompt,\s*imageDataUrl,\s*primaryModel,\s*timeout,\s*style\.Careful \? CAREFUL_THINKING_LEVEL : FAST_THINKING_LEVEL,',
        )
        self.assertRegex(
            CLIENT,
            r'MakeApiRequestWithTimeout\(\s*prompt,\s*imageDataUrl,\s*TRANSCRIBE_MODEL,\s*PRIMARY_TIMEOUT_SECONDS,\s*FAST_THINKING_LEVEL,',
        )

    def test_careful_requests_use_medium_thinking_and_larger_budget(self):
        self.assertIn('private const string CAREFUL_THINKING_LEVEL = "medium";', CLIENT)
        self.assertIn('private const int CAREFUL_MAX_OUTPUT_TOKENS = 1600;', CLIENT)
        self.assertRegex(
            CLIENT,
            r'MakeTextOnlyApiRequestWithTimeout\(\s*prompt,\s*CAREFUL_MODEL,\s*timeoutSeconds: 45,\s*thinkingLevel: CAREFUL_THINKING_LEVEL,\s*maxOutputTokens: CAREFUL_MAX_OUTPUT_TOKENS,',
        )

    def test_fallback_does_not_send_gemini_37_thinking_levels_to_25(self):
        fallback_calls = re.findall(
            r'Make(?:TextOnly)?ApiRequestWithTimeout\([^;]+?(?:FAST_LEGACY_MODEL|CAREFUL_LEGACY_MODEL)[^;]+?\);',
            CLIENT,
            flags=re.S,
        )
        self.assertGreaterEqual(len(fallback_calls), 3)
        self.assertTrue(all('thinkingLevel: null' in call for call in fallback_calls))

    def test_release_metadata_is_current(self):
        self.assertIn("<Version>2.1.6</Version>", PROJECT)
        self.assertIn("<ReleaseDate>2026-08-16</ReleaseDate>", PROJECT)


if __name__ == "__main__":
    unittest.main()
