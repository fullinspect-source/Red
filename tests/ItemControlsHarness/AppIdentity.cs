namespace InspectionEditor;
// Keep harness runs out of real RED application data (recovery copies, verified-save registry).
internal static class AppIdentity
{
    public static string LocalAppDataPath { get; } = Path.Combine(Path.GetTempPath(), "red-f004-appdata-" + Guid.NewGuid().ToString("N"));
}
