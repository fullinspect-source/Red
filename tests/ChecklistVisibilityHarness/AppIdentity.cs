namespace InspectionEditor;

internal static class AppIdentity
{
    public static string LocalAppDataPath { get; } =
        Path.Combine(Path.GetTempPath(), "red-checklist-visibility-" + Guid.NewGuid().ToString("N"));
}
