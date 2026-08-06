using System;
using System.IO;

namespace InspectionEditor.Services
{
    internal static class DiagnosticLogService
    {
        private static readonly object WriteLock = new();
        private static readonly string LogPath = Path.Combine(
            AppIdentity.LocalAppDataPath,
            "red_errors.log");

        public static void Log(string context, Exception exception)
        {
            try
            {
                lock (WriteLock)
                {
                    Directory.CreateDirectory(AppIdentity.LocalAppDataPath);
                    File.AppendAllText(
                        LogPath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
                }
            }
            catch
            {
                // Diagnostics must never become another failure path.
            }
        }
    }
}