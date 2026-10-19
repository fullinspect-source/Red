using System;
using System.IO;
using System.Linq;

namespace InspectionEditor.Services
{
    /// <summary>
    /// F-019 L5 (Program::CopyLogFiles) and L6 (Program::CleanupDropboxLogFiles): the INSPECT-format day files are copied
    /// to {IF}\Logs at launch and at each editor open; {IF}\Logs is pruned at launch only. N = DropboxLogRetention, read
    /// live from {IF}\config.json (.bak, then INSPECT's default 10), with INSPECT's arithmetic and no guard.
    /// </summary>
    public static class InspectLogCopyService
    {
        public const string CopyingLine = "Copying Inspect log to Dropbox";
        public const string CopiedLine = "   Log successfully copied to Dropbox";
        public const string CopyFailedLine = "   Could not copy log to Dropbox";

        public static void CopyToDropbox(string inspectionsFolder, InspectLogWriter writer, DateTime now, int? days = null)
        {
            try
            {
                string logs = InspectFolders.EnsureLogsForLogCopy(inspectionsFolder); // step 1, as INSPECT InitialTasks
                if ((File.GetAttributes(logs) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"{logs} is a link to another folder; RED copies nothing through links.");
                writer.FlushPending();                                                   // L3: before every copy
                writer.Info(CopyingLine);                                                // step 2
                int n = days ?? InspectLiveSettings.ReadDropboxLogRetention(inspectionsFolder); // step 3
                DateTime from = now.AddDays(-n).Date;
                var sources = Directory.Exists(writer.Folder)
                    ? new DirectoryInfo(writer.Folder).GetFiles("*.log", SearchOption.TopDirectoryOnly).Where(f => f.LastWriteTime >= from).OrderBy(f => f.Name, StringComparer.Ordinal).ToList()
                    : new System.Collections.Generic.List<FileInfo>();
                foreach (var source in sources) // steps 4-5
                {
                    string target = Path.Combine(logs, source.Name);
                    var existing = new FileInfo(target);
                    if (existing.Exists && (existing.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException($"{target} is a link; RED copies nothing through links.");
                    if (!existing.Exists || source.LastWriteTime > existing.LastWriteTime)
                        File.Copy(source.FullName, target, overwrite: true); // keeps the source's last-write time
                }
                writer.Info(CopiedLine);                                                 // step 6
            }
            catch (Exception ex)
            {
                writer.Warn(CopyFailedLine, ex);
            }
        }

        // L6: every {IF}\Logs\*.log whose last-write date is on or before (now - N days).Date, whichever device wrote it.
        public static int PruneDropbox(string inspectionsFolder, InspectLogWriter writer, DateTime now, int? days = null)
        {
            int deleted = 0;
            try
            {
                string logs = InspectFolders.PathOf(inspectionsFolder, InspectFolder.Logs);
                if (!Directory.Exists(logs))
                    return 0;
                if ((File.GetAttributes(logs) & FileAttributes.ReparsePoint) != 0)
                    return 0; // RED safety: never delete through a link
                int n = days ?? InspectLiveSettings.ReadDropboxLogRetention(inspectionsFolder);
                DateTime cutoff = now.AddDays(-n).Date;
                foreach (var file in new DirectoryInfo(logs).GetFiles("*.log", SearchOption.TopDirectoryOnly))
                {
                    if (file.LastWriteTime.Date > cutoff) continue;
                    try { file.Delete(); deleted++; }
                    catch (Exception) { } // per-file errors are ignored without a line
                }
            }
            catch (Exception ex)
            {
                writer.Error("Program", ex);
            }
            return deleted;
        }
    }
}
