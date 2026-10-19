using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace InspectionEditor.Services
{
    // F-019 §3 L1-L3 (owner Q-27): INSPECT-format log lines, written by RED when RED performs the action INSPECT would log.
    // Format `yyyy-MM-dd HH:mm:ss.ffff | {Level} | {message}` CRLF, UTF-8 without BOM, one file per local date, appended
    // with NLog's own sharing mode and never kept open. While INSPECT 2026 holds its day file, entries wait in a pending
    // queue in RED's own folder and go out at the next write, before each log copy and at exit. Shared by F-015 and F-019.
    public sealed class InspectLogWriter
    {
        private static readonly UTF8Encoding Utf8 = new(false);
        private readonly object _lock = new();
        private readonly Func<DateTime> _clock;
        private readonly Random _random = new();

        public string Folder { get; }
        public string QueueFolder { get; }

        // A dev build (Red2Dev) writes its INSPECT-format lines into its own app data, never into INSPECT's log folder.
        public static string DefaultFolder => AppIdentity.IsDevBuild
            ? Path.Combine(AppIdentity.LocalAppDataPath, "Logs", "inspect-format")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Strand", "Inspect");
        public static string DefaultQueueFolder => Path.Combine(AppIdentity.LocalAppDataPath, "Logs", "inspect-pending");

        private static InspectLogWriter? _default;
        public static InspectLogWriter Default => _default ??= new InspectLogWriter(DefaultFolder, DefaultQueueFolder);

        public InspectLogWriter(string folder, string queueFolder, Func<DateTime>? clock = null)
        {
            Folder = folder;
            QueueFolder = queueFolder;
            _clock = clock ?? (() => DateTime.Now);
        }

        public void Info(string message) => Write("Info", message, null);
        public void Warn(string message, Exception? exception = null) => Write("Warn", message, exception);
        public void Error(string message, Exception? exception = null) => Write("Error", message, exception);

        public static string Format(DateTime timestamp, string level, string message, Exception? exception)
        {
            var text = new StringBuilder();
            text.Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.ffff", CultureInfo.InvariantCulture));
            text.Append(" | ").Append(level).Append(" | ").Append(message);
            if (exception != null)
                text.Append("\r\n").Append(exception.ToString());
            text.Append("\r\n");
            return text.ToString();
        }

        private void Write(string level, string message, Exception? exception)
        {
            DateTime now = _clock();
            string line = Format(now, level, message, exception);
            lock (_lock)
            {
                FlushPendingLocked();
                // L3 order: while older entries for that date still wait, the new one waits behind them (and no retries
                // stall the caller while INSPECT 2026 holds its file).
                if (File.Exists(QueueFile(now.Date)) || !TryAppend(now.Date, line))
                    Enqueue(now.Date, line);
            }
        }

        // L3: before every copy (L5) and at RED exit.
        public void FlushPending()
        {
            lock (_lock) FlushPendingLocked();
        }

        public string FileFor(DateTime date) => Path.Combine(Folder, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

        private bool TryAppend(DateTime date, string text)
        {
            byte[] bytes = Utf8.GetBytes(text);
            for (int attempt = 0; attempt <= 10; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(Folder); // as NLog does
                    using var stream = new FileStream(FileFor(date), FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                    return true;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(_random.Next(1, 51)); // INSPECT 2022 opens per write; a short retry gets in between
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return false;
                }
            }
            return false;
        }

        private string QueueFile(DateTime date) => Path.Combine(QueueFolder, "pending-" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

        private void Enqueue(DateTime date, string text)
        {
            try
            {
                Directory.CreateDirectory(QueueFolder);
                File.AppendAllText(QueueFile(date), text, Utf8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                DiagnosticLogService.Log("An INSPECT-format log entry could not be queued", ex);
            }
        }

        private void FlushPendingLocked()
        {
            if (!Directory.Exists(QueueFolder))
                return;
            foreach (var pending in Directory.GetFiles(QueueFolder, "pending-*.log").OrderBy(p => p, StringComparer.Ordinal))
            {
                string name = Path.GetFileNameWithoutExtension(pending).Substring("pending-".Length);
                if (!DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    continue;
                string text;
                try { text = File.ReadAllText(pending, Utf8); }
                catch (IOException) { continue; }
                if (text.Length == 0 || TryAppendOnce(date, text))
                {
                    try { File.Delete(pending); } catch (IOException) { }
                }
            }
        }

        private bool TryAppendOnce(DateTime date, string text)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                using var stream = new FileStream(FileFor(date), FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
                byte[] bytes = Utf8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return false; }
        }
    }
}
