using System;
using System.Collections.Generic;
using System.IO;

namespace InspectionEditor.Services
{
    /// <summary>What one cleanup step removed: counts and bytes only (the logs never name a file).</summary>
    public sealed class CleanupTally
    {
        public int Files { get; set; }
        public int Folders { get; set; }
        public long Bytes { get; set; }
        public int Skipped { get; set; }

        public void Add(CleanupTally other)
        {
            Files += other.Files;
            Folders += other.Folders;
            Bytes += other.Bytes;
            Skipped += other.Skipped;
        }

        public bool Any => Files > 0 || Folders > 0 || Skipped > 0;

        public override string ToString() =>
            $"{Files} file(s){(Folders > 0 ? $" in {Folders} folder(s)" : "")}, {Bytes / 1048576.0:0.0} MB" +
            (Skipped > 0 ? $", {Skipped} skipped (open, locked or a link)" : "");
    }

    /// <summary>
    /// Storage audit (R-storage-debris-audit, common rules): every cleanup deletion goes through here. Best effort: a
    /// link (reparse point), a file another program has open or locked, or a read-only file the caller did not mark as
    /// its own is skipped and counted, never forced; nothing throws into the caller. Callers pick the exact folder and
    /// name pattern; this class only deletes what it is handed.
    /// </summary>
    public static class SafeDelete
    {
        /// <summary>Deletes one file when nobody else has it open. <paramref name="clearReadOnly"/> only for files RED
        /// itself marked read-only (the throwaway job-file copies).</summary>
        public static bool File(FileInfo file, CleanupTally tally, bool clearReadOnly = false)
        {
            try
            {
                file.Refresh();
                if (!file.Exists) return false;
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) { tally.Skipped++; return false; }
                if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                {
                    if (!clearReadOnly) { tally.Skipped++; return false; }
                    file.Attributes &= ~FileAttributes.ReadOnly;
                }
                if (!NobodyHasItOpen(file.FullName)) { tally.Skipped++; return false; }
                long length = file.Length;
                file.Delete();
                tally.Files++;
                tally.Bytes += length;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                tally.Skipped++;
                return false;
            }
        }

        /// <summary>
        /// Deletes a whole folder tree, but only when no part of it is a link and nobody has any file in it open;
        /// otherwise the folder is left whole and counted as skipped.
        /// </summary>
        public static bool Tree(DirectoryInfo folder, CleanupTally tally, bool clearReadOnly = false)
        {
            try
            {
                folder.Refresh();
                if (!folder.Exists) return false;
                if ((folder.Attributes & FileAttributes.ReparsePoint) != 0) { tally.Skipped++; return false; }
                var files = new List<FileInfo>();
                var folders = new List<DirectoryInfo>();
                if (!Collect(folder, files, folders)) { tally.Skipped++; return false; }
                foreach (var file in files)
                {
                    if ((file.Attributes & FileAttributes.ReadOnly) != 0 && !clearReadOnly) { tally.Skipped++; return false; }
                    if (!NobodyHasItOpen(file.FullName)) { tally.Skipped++; return false; }
                }
                var removed = new CleanupTally();
                foreach (var file in files)
                {
                    if (!File(file, removed, clearReadOnly) && file.Exists)
                    {
                        tally.Add(removed);
                        tally.Skipped++;
                        return false;
                    }
                }
                // Deepest first; the folder itself last.
                folders.Reverse();
                foreach (var sub in folders) sub.Delete(false);
                folder.Delete(false);
                tally.Add(removed);
                tally.Folders++;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                tally.Skipped++;
                return false;
            }
        }

        /// <summary>Removes the folder only when it is empty (no files, no subfolders) and not a link.</summary>
        public static void EmptyFolder(DirectoryInfo folder)
        {
            try
            {
                folder.Refresh();
                if (!folder.Exists || (folder.Attributes & FileAttributes.ReparsePoint) != 0) return;
                using var entries = folder.EnumerateFileSystemInfos().GetEnumerator();
                if (!entries.MoveNext()) folder.Delete(false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { }
        }

        // Lists the tree without ever following a link; false when one is found (the tree is then left alone).
        private static bool Collect(DirectoryInfo folder, List<FileInfo> files, List<DirectoryInfo> folders)
        {
            foreach (var entry in folder.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (entry is DirectoryInfo sub)
                {
                    folders.Add(sub);
                    if (!Collect(sub, files, folders)) return false;
                }
                else if (entry is FileInfo file)
                    files.Add(file);
            }
            return true;
        }

        // An exclusive open fails while any other program (a PDF app, a mail app, Dropbox mid-upload) has the file open.
        // On Windows it asks only for delete access, which reads no data: an online-only Dropbox or OneDrive file is
        // never downloaded just to be deleted.
        private static bool NobodyHasItOpen(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                string full = Path.GetFullPath(path);
                if (full.Length >= 260 && !full.StartsWith(@"\\?\", StringComparison.Ordinal))
                    full = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
                using var handle = CreateFileW(full, Delete, 0, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
                return !handle.IsInvalid;
            }
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private const uint Delete = 0x00010000, OpenExisting = 3, OpenReparsePoint = 0x00200000;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);
    }
}
