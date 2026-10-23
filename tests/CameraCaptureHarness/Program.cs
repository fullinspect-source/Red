// F-027 harness, RED side: RED's camera settings file and camera-roll copy, and source checks of MainWindow and the capture
// window (ACCEPTANCE A-027-01, -12, -18, -20, C10). The camera engine itself is tested by tests\RedCameraCoreHarness.
// Exit 0 = all passed.
using System.IO;
using InspectionEditor.Services.Camera;
using RedCamera.Core;

return CameraHarness.Run(args);

static class CameraHarness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id} {what}");
        if (ok) _passed++; else _failed++;
    }

    private static readonly CameraInfo Front = new("cam-front", "Front Camera", CameraPanelKind.Front, 0);

    public static int Run(string[] args)
    {
        try
        {
            SettingsAndRollCopy();
            SourceChecks(args.Length > 0 ? args[0] : FindRepoRoot());
        }
        catch (Exception ex)
        {
            Check("harness", false, ex.ToString());
        }
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void SettingsAndRollCopy()
    {
        string folder = Path.Combine(Path.GetTempPath(), "red-f027-harness-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new CameraSettingsService(Path.Combine(folder, "camera_settings.json"));
            bool defaultRed = !settings.UsesWindowsCameraApp && settings.DeviceId == null && settings.Panel == null;
            settings.Remember(Front);
            settings.SetMode(windowsCameraApp: true);
            var reloaded = new CameraSettingsService(Path.Combine(folder, "camera_settings.json"));
            bool kept = reloaded.UsesWindowsCameraApp && reloaded.DeviceId == Front.Id && reloaded.Panel == CameraPanelKind.Front;
            reloaded.SetMode(windowsCameraApp: false);
            Check("C11-settings", defaultRed && kept && !reloaded.UsesWindowsCameraApp && reloaded.DeviceId == Front.Id,
                "camera mode defaults to RED camera; mode and remembered camera survive each other's saves (own file)");

            // F-027 L1 (owner 2026-10-22): landscape photos are on by default, also for a settings file written before the
            // option existed; switching them off survives a restart and the other saves.
            string olderFile = Path.Combine(folder, "older_camera_settings.json");
            File.WriteAllText(olderFile, "{\"Mode\":\"red\",\"DeviceId\":\"cam-front\",\"Panel\":\"Front\"}");
            bool onForOlder = new CameraSettingsService(olderFile).AlwaysLandscape;
            bool onByDefault = new CameraSettingsService(Path.Combine(folder, "fresh.json")).AlwaysLandscape;
            reloaded.SetAlwaysLandscape(false);
            reloaded.Remember(Front);
            reloaded.SetMode(windowsCameraApp: false);
            var again = new CameraSettingsService(Path.Combine(folder, "camera_settings.json"));
            Check("L1-settings", onForOlder && onByDefault && !again.AlwaysLandscape && again.DeviceId == Front.Id,
                "landscape photos are on by default (also for an older settings file); off survives a restart and the other saves");

            // This harness runs as the Red2Dev assembly, so the copy must never resolve to the real Pictures folder.
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            Environment.SetEnvironmentVariable(CameraRollCopy.DevFolderVariable, Path.Combine(folder, "roll"));
            string redirected = CameraRollCopy.TargetFolder();
            Environment.SetEnvironmentVariable(CameraRollCopy.DevFolderVariable, null);
            string fallback = CameraRollCopy.TargetFolder();
            var when = new DateTime(2026, 10, 20, 9, 5, 7, 123);
            string? written = CameraRollCopy.TrySave(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, when, redirected);
            Check("C9-dev", redirected == Path.Combine(folder, "roll", "red-camera") && !fallback.StartsWith(pictures, StringComparison.OrdinalIgnoreCase)
                && fallback.EndsWith("\\red-camera", StringComparison.OrdinalIgnoreCase)
                && written != null && Path.GetFileName(written) == "RED_20261020_090507_123.jpg"
                && Path.GetDirectoryName(written) == redirected,
                $"backup copies go to <camera roll>\\red-camera; a dev build uses RED_DEV_CAMERA_ROLL or its app data, never Pictures ({fallback})");

            // Owner Q-59: copies older than 2 x Archive retention are deleted; only RED_*.jpg directly in red-camera.
            string backups = Path.Combine(folder, "purge", "red-camera");
            Directory.CreateDirectory(Path.Combine(backups, "keep-subfolder"));
            var now = new DateTime(2026, 10, 20, 12, 0, 0);
            string Make(string name, double daysOld)
            {
                string path = Path.Combine(backups, name);
                File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
                File.SetLastWriteTime(path, now.AddDays(-daysOld));
                return path;
            }
            string old11 = Make("RED_20261009_120000_000.jpg", 11);   // retention 5 → kept for 10 days → deleted
            string young9 = Make("RED_20261011_120000_000.jpg", 9);   // kept
            string other = Make("holiday.jpg", 30);                    // not RED's → kept
            string nested = Path.Combine(backups, "keep-subfolder", "RED_20260901_120000_000.jpg");
            File.WriteAllBytes(nested, new byte[] { 1 });
            File.SetLastWriteTime(nested, now.AddDays(-60));          // in a subfolder → kept
            int none = CameraRollCopy.PurgeOld(now, 0, backups);       // retention 0 → nothing deleted
            bool allThere = File.Exists(old11) && File.Exists(young9);
            int deleted = CameraRollCopy.PurgeOld(now, 5, backups);
            Check("C9-purge", none == 0 && allThere && deleted == 1 && !File.Exists(old11) && File.Exists(young9)
                && File.Exists(other) && File.Exists(nested),
                "backup photos older than 2 x Archive retention (5 → 10 days) are deleted; younger ones, other files and subfolders stay; 0 days deletes nothing");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "InspectionEditor.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? string.Empty;
    }

    private static void SourceChecks(string root)
    {
        string mainPath = Path.Combine(root, "MainWindow.xaml.cs");
        if (!File.Exists(mainPath))
        {
            Check("source", false, "RED source not found (pass the PL folder as the first argument)");
            return;
        }
        string main = File.ReadAllText(mainPath);
        string Method(string signature)
        {
            int start = main.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            int end = main.IndexOf("\n        }", start, StringComparison.Ordinal);
            return end < 0 ? main[start..] : main[start..end];
        }

        string cameraClick = Method("private void CameraButton_Click(");
        Check("A-027-01", cameraClick.Contains("UsesWindowsCameraApp") && cameraClick.Contains("OpenWindowsCameraApp(")
            && Method("private void OpenWindowsCameraApp(").Contains("_cameraService.OpenOrFocusCamera()"),
            "Camera mode \"Windows Camera app\" keeps the CameraService path");

        string redPhoto = Method("private void OnRedCameraPhoto(");
        string captured = Method("private void OnPhotoCaptured(");
        Check("A-027-18", redPhoto.Contains("ReferenceEquals(") && redPhoto.Contains("_editorLoadedItem") && redPhoto.Contains("OnPhotoCaptured(")
            && redPhoto.IndexOf("ReferenceEquals(", StringComparison.Ordinal) < redPhoto.IndexOf("OnPhotoCaptured(", StringComparison.Ordinal),
            "a capture-screen photo is added only to the item bound at the press");
        Check("A-027-20", captured.Length > 0 && captured[..Math.Min(400, captured.Length)].Contains("AddPhotoToCurrentItem(photoData);")
            && captured.Contains("_photoFromCaptureScreen"),
            "capture-screen photos use OnPhotoCaptured → AddPhotoToCurrentItem (F-023 T1, read-only, ink, auto-Fail unchanged)");

        string xamlPath = Path.Combine(root, "CameraCaptureWindow.xaml");
        string xaml = File.Exists(xamlPath) ? File.ReadAllText(xamlPath) : string.Empty;
        int shutter = xaml.IndexOf("x:Name=\"ShutterButton\"", StringComparison.Ordinal);
        string shutterTag = shutter < 0 ? string.Empty : xaml[xaml.LastIndexOf('<', shutter)..xaml.IndexOf('>', shutter)];
        string code = File.Exists(xamlPath + ".cs") ? File.ReadAllText(xamlPath + ".cs") : string.Empty;
        Check("A-027-12b", shutter >= 0 && !shutterTag.Contains("Visibility") && !shutterTag.Contains("IsEnabled")
            && shutterTag.Contains("Panel.ZIndex=\"100\"")
            && !code.Contains("ShutterButton.Visibility") && !code.Contains("ShutterButton.IsEnabled"),
            "the shutter is never hidden or disabled and sits above every overlay");
        Check("C10-ui", code.Contains("CameraResetFlow") && code.Split("RunResetAsync(").Length == 3,
            "the reset flow has exactly one caller, the Reset camera button");
        int closing = code.IndexOf("private void OnClosing(", StringComparison.Ordinal);
        string closingBody = closing < 0 ? string.Empty : code[closing..code.IndexOf("\n        }", closing, StringComparison.Ordinal)];
        Check("C12-focus", code.Contains("Closing += OnClosing;") && closingBody.Contains("owner.Activate();")
            && closingBody.Contains("WindowState.Minimized"),
            "closing the camera brings the report back in front (owner tablet test 2026-10-20: it stayed behind the list)");

        // F-027 Z4/Z5: pinch, wheel and double-tap act on the preview surface only; the readout is a 44 px button; no popup.
        int surface = xaml.IndexOf("x:Name=\"PreviewSurface\"", StringComparison.Ordinal);
        string surfaceTag = surface < 0 ? string.Empty : xaml[xaml.LastIndexOf('<', surface)..xaml.IndexOf('>', surface)];
        int surfaceEnd = surface < 0 ? -1 : xaml.IndexOf("</Border>", surface, StringComparison.Ordinal);
        string surfaceBlock = surfaceEnd < 0 ? string.Empty : xaml[surface..surfaceEnd];
        int readout = xaml.IndexOf("x:Name=\"ZoomReadoutButton\"", StringComparison.Ordinal);
        string readoutTag = readout < 0 ? string.Empty : xaml[xaml.LastIndexOf('<', readout)..xaml.IndexOf('>', readout)];
        int zoomStart = code.IndexOf("// ------------------------------------------------------------------ zoom", StringComparison.Ordinal);
        int zoomEnd = code.IndexOf("// ------------------------------------------------------------------ shutter", StringComparison.Ordinal);
        string zoomCode = zoomStart < 0 || zoomEnd < zoomStart ? string.Empty : code[zoomStart..zoomEnd];
        Check("A-027-27", surfaceTag.Contains("IsManipulationEnabled=\"True\"") && surfaceTag.Contains("ManipulationDelta=")
            && surfaceTag.Contains("MouseWheel=") && surfaceTag.Contains("TouchUp=") && surfaceBlock.Contains("x:Name=\"PreviewImage\"")
            && !surfaceBlock.Contains("<Button") && shutter > surfaceEnd
            && readoutTag.Contains("MinHeight=\"44\"") && readoutTag.Contains("MinWidth=\"72\"") && readoutTag.Contains("Click=")
            && zoomCode.Split("_controller.ResetZoom()").Length == 4
            && zoomCode.Contains("_renderer.SetZoom(_controller.PreviewZoom)") && code.Contains("_controller.ZoomChanged += OnZoomChanged;")
            && !zoomCode.Contains("MessageBox") && !zoomCode.Contains("Popup") && !readoutTag.Contains("ToolTip"),
            "zoom: pinch, wheel and double-tap only on the preview surface (no button inside it, shutter outside); the readout "
            + "is a 44 px button that resets to 1x, as do double-click and double-tap; no popup");

        // F-027 owner update 2026-10-22: the shutter overlays the middle of the preview's right edge (>= 64 px), the switch
        // button sits above it, a tap focuses through CameraFocus with a mark that never takes input, and Volume Up, Enter and
        // Space go through the one-photo-per-press gate (plus the Volume Up app command).
        int area = xaml.IndexOf("x:Name=\"PreviewArea\"", StringComparison.Ordinal);
        int areaEnd = area < 0 ? -1 : xaml.IndexOf("<Grid Grid.Row=\"2\"", area, StringComparison.Ordinal);
        string areaBlock = areaEnd < 0 ? string.Empty : xaml[area..areaEnd];
        int switchAt = xaml.IndexOf("x:Name=\"SwitchCameraButton\"", StringComparison.Ordinal);
        string switchTag = switchAt < 0 ? string.Empty : xaml[xaml.LastIndexOf('<', switchAt)..xaml.IndexOf('>', switchAt)];
        int focusLayer = xaml.IndexOf("x:Name=\"FocusLayer\"", StringComparison.Ordinal);
        string focusTag = focusLayer < 0 ? string.Empty : xaml[xaml.LastIndexOf('<', focusLayer)..xaml.IndexOf('>', focusLayer)];
        double Number(string tag, string attribute)
        {
            int at = tag.IndexOf(attribute + "=\"", StringComparison.Ordinal);
            if (at < 0) return 0;
            at += attribute.Length + 2;
            return double.TryParse(tag[at..tag.IndexOf('"', at)], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : 0;
        }
        bool shutterRight = areaBlock.Contains("x:Name=\"ShutterButton\"") && shutterTag.Contains("HorizontalAlignment=\"Right\"")
            && shutterTag.Contains("VerticalAlignment=\"Center\"") && Number(shutterTag, "Width") >= 64 && Number(shutterTag, "Height") >= 64;
        // The switch button is centred on the shutter's column and lifted by its bottom margin (176 / 2 = 88) above the centre:
        // its bottom edge (centre - 88 + 28) stays above the shutter's top (centre - 44) with a 16 px gap.
        bool switchAbove = areaBlock.Contains("x:Name=\"SwitchCameraButton\"") && switchTag.Contains("Margin=\"0,0,32,176\"")
            && switchTag.Contains("VerticalAlignment=\"Center\"") && Number(switchTag, "Height") == 56 && shutterTag.Contains("Margin=\"0,0,16,0\"");
        string keysCode = code.Contains("private void Window_PreviewKeyDown(")
            ? code[code.IndexOf("private void Window_PreviewKeyDown(", StringComparison.Ordinal)..] : string.Empty;
        bool keys = code.Contains("Key.Space or Key.Enter or Key.VolumeUp") && keysCode.Contains("_shutterKeys.Press(e.IsRepeat, e.Timestamp)")
            && code.Contains("AppCommandVolumeUp = 10") && code.Contains("_shutterKeys.Pulse(");
        bool tapFocus = code.Contains("CameraFocus.TryMapTap(PreviewImage, PreviewSurface") && code.Contains("_controller.TapToFocus(x, y)")
            && focusTag.Contains("IsHitTestVisible=\"False\"") && code.Contains("FocusAt(point)")
            && code.Contains("TabletDeviceType.Touch") && !code.Contains("MessageBox.Show(this, \"Focus");
        int failure = xaml.IndexOf("x:Name=\"FailurePanel\"", StringComparison.Ordinal);
        string failureBlock = failure < 0 ? string.Empty : xaml[failure..xaml.IndexOf("</Border>", xaml.IndexOf("</ScrollViewer>", failure, StringComparison.Ordinal), StringComparison.Ordinal)];
        bool small = code.Contains("private void PlaceSwitchButton()") && code.Contains("new Thickness(0, 0, 120, 0)")
            && code.Contains("PreviewArea.SizeChanged += (_, _) =>") && code.Contains("                PlaceSwitchButton();")
            && failureBlock.Contains("<Style TargetType=\"Button\">") && failureBlock.Contains("TextWrapping=\"Wrap\"");
        Check("A-027-34", shutterRight && switchAbove && keys && tapFocus && small,
            "the shutter overlays the middle of the preview's right edge (88 px) with Switch camera above it; a tap or click focuses "
            + "through CameraFocus and shows a mark that takes no input; Volume Up, Enter and Space (and the Volume Up app command) "
            + "take one photo per press; on a short preview Switch moves beside the shutter, and failure buttons wrap their text");

        // Owner 2026-10-22: Close twice as tall; the landscape toggle at the top, from RED's camera settings, driving the
        // engine's AlwaysLandscape; a guide that takes no input.
        int close = xaml.IndexOf("x:Name=\"CloseCameraButton\"", StringComparison.Ordinal);
        string closeTag = close < 0 ? string.Empty : xaml[xaml.LastIndexOf('<', close)..xaml.IndexOf('>', close)];
        int toggle = xaml.IndexOf("x:Name=\"LandscapeToggle\"", StringComparison.Ordinal);
        string toggleTag = toggle < 0 ? string.Empty : xaml[xaml.LastIndexOf('<', toggle)..xaml.IndexOf('>', toggle)];
        int guide = xaml.IndexOf("x:Name=\"LandscapeGuide\"", StringComparison.Ordinal);
        string guideTag = guide < 0 ? string.Empty : xaml[xaml.LastIndexOf('<', guide)..xaml.IndexOf('>', guide)];
        bool topBar = toggle >= 0 && toggle < xaml.IndexOf("x:Name=\"PreviewArea\"", StringComparison.Ordinal);
        Check("A-027-37", Number(closeTag, "MinHeight") == 88 && Number(toggleTag, "MinHeight") == 88 && topBar
            && guideTag.Contains("IsHitTestVisible=\"False\"")
            && code.Contains("_controller.AlwaysLandscape = _cameraSettings.AlwaysLandscape")
            && code.Contains("_cameraSettings.SetAlwaysLandscape(on)") && code.Contains("CameraLandscape.KeptOnScreen(")
            && code.Contains("? \"Landscape only\" : \"Allow all orientations\"") && toggleTag.Contains("MaxWidth=\"150\"")
            && code.Contains("NextExifOrientation(), frame.Width") && code.Contains("int exif = NextExifOrientation();")
            && code.Contains("_devUpright ? 6 :") && code.Contains("case Key.F9:"),
            "Close is 88 px tall (twice 44); the toggle sits in the top bar (88 px, at most 150 px wide, its text wrapping) and "
            + "reads \"Landscape only\" (the default) or \"Allow all orientations\" (owner 2026-10-22); it starts from RED's "
            + "camera setting and switches the engine; the framing guide takes no input; the upright simulation is dev-only");

        // The engine stays free of RED code, so the RED Camera companion can reuse it unchanged.
        string coreFolder = Path.Combine(root, "RedCamera.Core");
        var coreSources = Directory.Exists(coreFolder)
            ? Directory.GetFiles(coreFolder, "*.cs", SearchOption.TopDirectoryOnly).Select(File.ReadAllText).ToList()
            : new List<string>();
        string coreProject = File.Exists(Path.Combine(coreFolder, "RedCamera.Core.csproj")) ? File.ReadAllText(Path.Combine(coreFolder, "RedCamera.Core.csproj")) : "";
        Check("LIB-boundary", coreSources.Count >= 8 && !coreProject.Contains("ProjectReference")
            && coreSources.All(s => !s.Contains("InspectionEditor") && !s.Contains("AppIdentity") && !s.Contains("DiagnosticLogService")),
            $"RedCamera.Core ({coreSources.Count} files) references no RED code or project");
    }
}
