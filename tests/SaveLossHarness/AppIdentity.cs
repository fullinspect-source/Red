namespace InspectionEditor;
// Keep fault tests out of real RED application data.
internal static class AppIdentity
{
    public static string LocalAppDataPath { get; } = Path.Combine(Path.GetTempPath(), "red-save-loss-recovery-" + Guid.NewGuid().ToString("N"));
}
