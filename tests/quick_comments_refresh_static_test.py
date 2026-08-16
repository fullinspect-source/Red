#!/usr/bin/env python3
"""Regression checks for current, non-shadowed Quick Comments refreshes."""

from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]


class QuickCommentsRefreshTests(unittest.TestCase):
    def test_download_target_is_writable_appdata(self):
        source = (ROOT / "Services" / "DataUpdateService.cs").read_text()
        self.assertIn(
            'QuickCommentsPath  = Path.Combine(AppIdentity.LocalAppDataPath, "quick_comments.json")',
            source,
        )
        self.assertIn("preserveNewerGeneratedData: true", source)
        self.assertIn('string temporaryPath = localPath + ".download"', source)
        self.assertIn("ToUnixTimeMilliseconds()", source)

    def test_downloaded_copy_precedes_bundled_copy_and_cache_detects_change(self):
        source = (ROOT / "Services" / "QuickCommentsService.cs").read_text()
        appdata = source.index('Path.Combine(AppIdentity.LocalAppDataPath, "quick_comments.json")')
        executable = source.index('Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)')
        self.assertLess(appdata, executable)
        self.assertIn("_loadedWriteTimeUtc", source)
        self.assertIn("writeTimeUtc == _loadedWriteTimeUtc", source)

    def test_remote_payload_is_validated_and_older_data_cannot_overwrite_newer(self):
        source = (ROOT / "Services" / "DataUpdateService.cs").read_text()
        self.assertIn("IsValidQuickCommentsPayload", source)
        self.assertIn("remoteGenerated.Value < localGenerated.Value", source)
        self.assertIn('Regex.IsMatch(content, "\\\"items\\\"', source)


if __name__ == "__main__":
    unittest.main()