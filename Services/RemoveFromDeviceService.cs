using System;
using System.IO;
using System.Threading;

namespace InspectionEditor.Services
{
    /// <summary>
    /// F-019 D1-D5: Remove from device deletes only the selected .ins files (owner Q-25 default: job folders are left to
    /// the purge). INSPECT's questions, plus RED's every-device sentence. No log line (D5: INSPECT writes none).
    /// </summary>
    public static class RemoveFromDeviceService
    {
        public const string OneQuestion = "Are you sure you want to remove this service from your device?";
        public const string ManyQuestion = "Are you sure you want to remove these services from your device?";
        public const string EveryDevice = "This removes it from every device that shares this Dropbox folder.";

        public static string Question(int count) => (count == 1 ? OneQuestion : ManyQuestion) + Environment.NewLine + Environment.NewLine + EveryDevice;

        // Only a top-level .ins of My List, Outbox, Archive or Review in the Inspections folder can be removed.
        public static bool CanRemove(string path, string inspectionsFolder)
        {
            if (!path.EndsWith(".ins", StringComparison.OrdinalIgnoreCase)) return false;
            string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
            foreach (var folder in new[] { InspectFolder.MyList, InspectFolder.Outbox, InspectFolder.Archive, InspectFolder.Review })
                if (string.Equals(parent?.TrimEnd('\\'), Path.GetFullPath(InspectFolders.PathOf(inspectionsFolder, folder)).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // D2: delete with retries on sharing violations (10 x 10 ms, then 3 x 1 s). A file already gone counts as removed.
        public static bool Remove(string path, string inspectionsFolder, Action<int>? sleep = null)
        {
            if (!CanRemove(path, inspectionsFolder)) return false;
            sleep ??= Thread.Sleep;
            for (int attempt = 0; attempt < 13; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return true;
                    File.Delete(path);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    sleep(attempt < 10 ? 10 : 1000);
                }
            }
            return false;
        }
    }
}
