"""Prevent shipping/installing RED without its photo processor."""
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]

class PhotoDependencyPackagingTests(unittest.TestCase):
    def test_publish_rejects_missing_photo_dependency(self):
        text = (ROOT / 'InspectionEditor.csproj').read_text()
        self.assertIn('VerifyPhotoDependencyPublish', text)
        self.assertIn("$(PublishDir)SixLabors.ImageSharp.dll", text)
        self.assertIn('AfterTargets="Publish"', text)

    def test_release_script_preserves_publish_failure(self):
        text = (ROOT / 'scripts/publish-release.sh').read_text()
        publish = next(line for line in text.splitlines() if line.startswith('dotnet publish '))
        self.assertNotIn('|', publish)
        self.assertIn('set -e', text)

    def test_external_updater_checks_dependency_before_copy(self):
        text = (ROOT / 'scripts/update_red.bat').read_text()
        check = 'if not exist "%EXTRACT_DIR%\\SixLabors.ImageSharp.dll" goto :verify_failed'
        self.assertIn(check, text)
        self.assertLess(text.index(check), text.index('xcopy /E /Y /Q'))

    def test_internal_updater_checks_dependency_before_start(self):
        text = (ROOT / 'Services/AppUpdateService.cs').read_text()
        check = 'Path.Combine(extractDir, "SixLabors.ImageSharp.dll")'
        self.assertIn(check, text)
        self.assertLess(text.index(check), text.index('Process.Start(new ProcessStartInfo'))

if __name__ == '__main__':
    unittest.main()
