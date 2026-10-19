using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace InspectionEditor.Services
{
    public sealed class JobPurgeResult
    {
        public bool Declined { get; set; }
        public bool Disabled { get; set; }
        public int Present { get; set; }
        public int Orphaned { get; set; }
        public int Taken { get; set; }
        public int Removed { get; set; }
        public int Failed { get; set; }
        public List<string> Candidates { get; } = new();
    }

    /// <summary>
    /// F-019 §2 (INSPECT 2026 JobFoldersDeleter): once per launch, in the background after the lists load. A folder
    /// Jobs\J is orphaned unless a live .ins name starts with J. Live = My List, Outbox, Review and (RED default, owner
    /// Q-26) Archive. At most 20 per run, links refused at any depth, every line in INSPECT's exact text.
    /// </summary>
    public static class JobFolderPurgeService
    {
        public const int MaxFoldersPerRun = 20;
        private static readonly InspectFolder[] LiveFolders = { InspectFolder.MyList, InspectFolder.Outbox, InspectFolder.Review, InspectFolder.Archive };

        // dryRun (V3): lists the candidates only; deletes nothing and writes no line.
        public static JobPurgeResult Run(string inspectionsFolder, InspectLogWriter log, bool dryRun = false, HousekeepingSettings? settings = null)
        {
            var result = new JobPurgeResult();
            if (!dryRun && !(settings ?? HousekeepingSettings.Load()).PurgeJobFolders)
            {
                result.Disabled = true;
                return result;
            }
            void Info(string text) { if (!dryRun) log.Info(text); }
            void Warn(string text, Exception? ex = null) { if (!dryRun) log.Warn(text, ex); }

            Info("Purging Job Files");
            var folders = LiveFolders.Select(f => InspectFolders.PathOf(inspectionsFolder, f)).ToList();
            if (!TryLiveNames(folders, out var live, out string? unreadable, out Exception? readError))
            {
                result.Declined = true;
                Warn($"Purge declined: {unreadable} could not be read, so the live inspections cannot be established", readError);
                return result;
            }
            if (live.Count == 0)
            {
                result.Declined = true;
                Warn($"Purge declined: no inspections found in {string.Join(", ", folders)}, so no job folder can be shown to be orphaned");
                return result;
            }

            string jobs = InspectFolders.PathOf(inspectionsFolder, InspectFolder.Jobs);
            List<DirectoryInfo> present;
            try
            {
                present = Directory.Exists(jobs)
                    ? new DirectoryInfo(jobs).GetDirectories().OrderBy(d => d.Name, StringComparer.Ordinal).ToList()
                    : new List<DirectoryInfo>();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                result.Declined = true;
                Warn($"Purge declined: could not read {jobs}", ex);
                return result;
            }
            result.Present = present.Count;
            var orphans = present.Where(d => !IsLive(d.Name, live)).ToList(); // P3
            result.Orphaned = orphans.Count;
            if (orphans.Count == 0)
            {
                Info($"Purge: {present.Count} job folder(s) present, none orphaned");
                return result;
            }
            var taken = orphans.Take(MaxFoldersPerRun).ToList(); // P4
            result.Taken = taken.Count;
            result.Candidates.AddRange(taken.Select(d => d.Name));
            if (dryRun)
                return result;
            Info($"Purge: {orphans.Count} of {present.Count} job folder(s) orphaned; removing {taken.Count} this run, {orphans.Count - taken.Count} left for the next launch");

            foreach (var folder in taken)
            {
                string job = folder.Name;
                // P5: re-check just before deleting.
                try
                {
                    if (!TryLiveNames(folders, out var now, out _, out var recheckError))
                        throw recheckError ?? new IOException("The live folders could not be read.");
                    if (IsLive(job, now))
                    {
                        Info($"Job folder {job} became live during the purge and was left alone");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    Warn($"Job folder {job} left in place: whether it is still orphaned could not be re-checked", ex);
                    continue;
                }
                if (!Directory.Exists(folder.FullName))
                {
                    result.Removed++;
                    Info($"Job folder {job} is already gone");
                    continue;
                }
                // P6: links refused at any depth; every subfolder must be listable before anything is deleted.
                FileAttributes attributes;
                try { attributes = File.GetAttributes(folder.FullName); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    result.Failed++;
                    Warn($"Job folder {job} left in place: {folder.FullName} could not be listed", ex);
                    continue;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    result.Failed++;
                    Warn($"Job folder {job} left in place: it is a link rather than a real folder");
                    continue;
                }
                var files = new List<string>();
                var directories = new List<string>();
                string? problem = Inventory(folder.FullName, files, directories, out string? link);
                if (link != null)
                {
                    result.Failed++;
                    Warn($"Job folder {job} left in place: {link} is a link to somewhere else, not a folder");
                    continue;
                }
                if (problem != null)
                {
                    result.Failed++;
                    Warn($"Job folder {job} left in place: {problem} could not be listed");
                    continue;
                }
                int removed = 0, failed = 0;
                foreach (string file in files)
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { failed++; }
                }
                foreach (string directory in directories.OrderByDescending(d => d.Length).Append(folder.FullName))
                {
                    try { Directory.Delete(directory, recursive: false); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
                if (Directory.Exists(folder.FullName))
                {
                    result.Failed++;
                    Warn($"Job folder {job} is still present at {folder.FullName}: {removed} file(s) removed, {failed} failed");
                }
                else
                {
                    result.Removed++;
                    Info($"Deleted job folder {job}: {removed} file(s) removed, {failed} failed");
                }
            }
            Info($"Purge: removed {result.Removed} job folder(s), {result.Failed} failed, {orphans.Count - taken.Count + result.Failed} left for the next launch"); // P7
            return result;
        }

        private static bool IsLive(string job, IReadOnlyCollection<string> live) =>
            live.Any(name => name.StartsWith(job, StringComparison.OrdinalIgnoreCase));

        // P1/P2: a missing live folder is empty; one that exists but cannot be listed declines the run.
        private static bool TryLiveNames(IEnumerable<string> folders, out List<string> names, out string? unreadable, out Exception? error)
        {
            names = new List<string>();
            unreadable = null;
            error = null;
            foreach (string folder in folders)
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    names.AddRange(Directory.GetFiles(folder, "*.ins", SearchOption.TopDirectoryOnly).Select(Path.GetFileName)!);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    unreadable = folder;
                    error = ex;
                    return false;
                }
            }
            return true;
        }

        // Lists every file and subfolder without following links. Returns the folder that could not be listed, if any.
        private static string? Inventory(string root, List<string> files, List<string> directories, out string? link)
        {
            link = null;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                try
                {
                    foreach (string entry in Directory.GetFileSystemEntries(current))
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            link = entry;
                            return null;
                        }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            directories.Add(entry);
                            pending.Push(entry);
                        }
                        else
                        {
                            files.Add(entry);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    return current;
                }
            }
            return null;
        }
    }
}
