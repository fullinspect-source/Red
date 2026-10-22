namespace InspectionEditor;

internal static class AppIdentity
{
    public static string LocalAppDataPath { get; } =
        Path.Combine(Path.GetTempPath(), "red-checklist-visibility-" + Guid.NewGuid().ToString("N"));

    // Audit S-28: this run's app-data sandbox is deleted when the harness ends (Program's finally, and at process exit).
    static AppIdentity() => AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteSandbox();

    public static void DeleteSandbox()
    {
        // A virus scan can hold a just-written file for a moment, so retry briefly.
        for (int attempt = 0; attempt < 20 && Directory.Exists(LocalAppDataPath); attempt++)
        {
            try { Directory.Delete(LocalAppDataPath, recursive: true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Thread.Sleep(100); }
        }
    }
}
