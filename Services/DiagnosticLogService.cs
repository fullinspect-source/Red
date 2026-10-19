using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace InspectionEditor.Services
{
    // RED-only log (F-019 L8): daily files red-{yyyy-MM-dd}.log in RED's own Logs folder, never in INSPECT's log folders.
    // Files more than 30 days old are deleted at launch; only red-*.log files are ever touched (the INSPECT-format
    // pending queue lives beside them and is not pruned).
    internal static class DiagnosticLogService
    {
        public const int RetentionDays = 30;
        private static readonly object WriteLock = new();

        internal static string LogFolder => Path.Combine(AppIdentity.LocalAppDataPath, "Logs");

        internal static string LogPathFor(DateTime date) =>
            Path.Combine(LogFolder, "red-" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

        public static void Log(string context, Exception exception) =>
            Append($"{context}{Environment.NewLine}{exception}{Environment.NewLine}");

        // A one-line note for support (no exception), e.g. the settings RED started with.
        public static void Info(string message) => Append(message);

        private static void Append(string text)
        {
            try
            {
                lock (WriteLock)
                {
                    DateTime now = DateTime.Now;
                    Directory.CreateDirectory(LogFolder);
                    File.AppendAllText(LogPathFor(now), $"{now:yyyy-MM-dd HH:mm:ss.fff} | {text}{Environment.NewLine}");
                }
            }
            catch
            {
                // Diagnostics must never become another failure path.
            }
        }

        // L8: at launch, delete RED-only day files whose last-write date is more than 30 days before today.
        public static int PruneOld(DateTime now, string? folder = null)
        {
            int deleted = 0;
            try
            {
                folder ??= LogFolder;
                if (!Directory.Exists(folder)) return 0;
                DateTime cutoff = now.Date.AddDays(-RetentionDays);
                foreach (var file in new DirectoryInfo(folder).GetFiles("red-*.log", SearchOption.TopDirectoryOnly))
                {
                    if (!Regex.IsMatch(file.Name, @"\Ared-\d{4}-\d{2}-\d{2}\.log\z") || file.LastWriteTime.Date >= cutoff) continue;
                    try { file.Delete(); deleted++; } catch (Exception) { }
                }
            }
            catch (Exception) { }
            return deleted;
        }
    }
}
