import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PROJECT = (ROOT / "InspectionEditor.csproj").read_text(encoding="utf-8")
MAIN = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8")
GITIGNORE = (ROOT / ".gitignore").read_text(encoding="utf-8")


class AiReleasePackagingRegressionTests(unittest.TestCase):
    def test_release_build_requires_private_generated_key_provider(self):
        self.assertIn('Name="RequireEmbeddedAiKeyForRelease"', PROJECT)
        self.assertIn("'$(Configuration)' == 'Release'", PROJECT)
        self.assertIn("!Exists('$(MSBuildProjectDirectory)\\EmbeddedApiKeyProvider.Generated.cs')", PROJECT)
        self.assertIn("Release builds require the gitignored EmbeddedApiKeyProvider.Generated.cs", PROJECT)

    def test_generated_key_source_remains_gitignored(self):
        self.assertIn("EmbeddedApiKeyProvider.Generated.cs", GITIGNORE)

    def test_inline_ai_buttons_do_not_disappear_when_key_is_missing(self):
        self.assertIn("bool canUseAi = hasPhoto;", MAIN)
        self.assertNotIn("bool canUseAi = _grokClient != null && hasPhoto;", MAIN)

    def test_missing_key_has_explicit_user_diagnostic(self):
        self.assertGreaterEqual(MAIN.count("ShowAiUnavailableMessage();"), 2)
        self.assertIn("RED AI Tools Unavailable", MAIN)
        self.assertIn("Run the latest RED updater", MAIN)


if __name__ == "__main__":
    unittest.main()
