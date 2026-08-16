#!/usr/bin/env python3
"""Static regression checks for RED camera crash hardening."""

from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
CAMERA = (ROOT / "Services" / "CameraService.cs").read_text(encoding="utf-8")
APP = (ROOT / "App.xaml.cs").read_text(encoding="utf-8")
LOGGER = (ROOT / "Services" / "DiagnosticLogService.cs").read_text(encoding="utf-8")
PROJECT = (ROOT / "InspectionEditor.csproj").read_text(encoding="utf-8")


def method_body(source: str, signature: str) -> str:
    start = source.index(signature)
    brace = source.index("{", start)
    depth = 0
    for index in range(brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[brace + 1:index]
    raise AssertionError(f"Unclosed method: {signature}")


class CameraCrashSafetyTests(unittest.TestCase):
    def test_watcher_callbacks_use_full_exception_boundary(self):
        self.assertIn('await ProcessNewPhotoSafely("created", e.FullPath);', CAMERA)
        self.assertIn('await ProcessNewPhotoSafely("renamed", e.FullPath);', CAMERA)
        safe_body = method_body(CAMERA, "private async Task ProcessNewPhotoSafely")
        self.assertIn("try", safe_body)
        self.assertIn("catch (Exception ex)", safe_body)
        self.assertIn("DiagnosticLogService.Log", safe_body)

    def test_successful_capture_leaves_camera_open_and_visible(self):
        process_body = method_body(CAMERA, "private async Task ProcessNewPhoto(string filePath)")
        success = process_body.split("if (photoData != null)", 1)[1].split("else", 1)[0]
        self.assertNotIn("MinimizeCamera", process_body)
        self.assertNotIn("BringRedToFront", success)
        self.assertIn("PhotoCaptured?.Invoke(photoData)", success)

    def test_durable_diagnostic_log_is_best_effort_and_thread_safe(self):
        self.assertIn("AppIdentity.LocalAppDataPath", LOGGER)
        self.assertIn('"red_errors.log"', LOGGER)
        self.assertIn("lock (WriteLock)", LOGGER)
        self.assertRegex(LOGGER, r"catch\s*\{\s*// Diagnostics must never become another failure path\.")

    def test_global_exception_sources_use_durable_logger(self):
        self.assertIn("AppDomain.CurrentDomain.UnhandledException", APP)
        self.assertIn('DiagnosticLogService.Log("AppDomain unhandled exception"', APP)
        self.assertIn('DiagnosticLogService.Log("Unobserved task exception"', APP)
        self.assertIn('DiagnosticLogService.Log("WPF dispatcher exception"', APP)

    def test_release_metadata(self):
        self.assertIn("<Version>2.1.4</Version>", PROJECT)
        self.assertIn("<ReleaseDate>2026-08-16</ReleaseDate>", PROJECT)


if __name__ == "__main__":
    unittest.main()
