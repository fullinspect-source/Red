using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RedCamera.Core
{
    /// <summary>
    /// Writing stills to disk (F-027 C9) and the folders INSPECT watches for new photos (F-023 I2/T3). The rules follow
    /// INSPECT's IL (research/R-red-camera-companion.md §3-§5; INSPECT2026 2026.1012.2 `PathManager.GetCameraRollPath`,
    /// Inspect2022 2022.7.27.1 `PictureWatcher.GetPicturesPath`).
    /// </summary>
    public static class CameraFiles
    {
        /// <summary>
        /// Writes <paramref name="bytes"/> to <paramref name="path"/> so the file appears complete, in one step, as a
        /// <em>Created</em> event: the bytes go to a temporary file outside the destination folder on the same volume,
        /// which is then moved in. Inspect2022 reacts to Created only (no Renamed), so a temporary name inside the folder
        /// renamed to .jpg would be missed. The staging folder is <paramref name="stagingFolder"/> when it is on the same
        /// volume, else the destination's parent folder (not watched by either INSPECT). A destination at a drive root
        /// falls back to a temporary name inside it. Creates the destination folder only if <paramref name="createFolder"/>.
        /// Never replaces an existing file. Throws on IO errors.
        /// </summary>
        public static void SaveAtomic(byte[] bytes, string path, string? stagingFolder = null, bool createFolder = true)
        {
            string fullPath = Path.GetFullPath(path);
            string folder = Path.GetDirectoryName(fullPath)!;
            if (createFolder) Directory.CreateDirectory(folder);
            else if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Folder not found: {folder}");

            string staging = ChooseStaging(folder, stagingFolder);
            Directory.CreateDirectory(staging);
            string temp = Path.Combine(staging, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, fullPath, overwrite: false);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private static string ChooseStaging(string folder, string? stagingFolder)
        {
            static bool SameVolume(string a, string b) =>
                string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(stagingFolder) && SameVolume(stagingFolder, folder) && !IsInside(stagingFolder, folder))
                return stagingFolder;
            string? parent = Path.GetDirectoryName(folder.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(parent) ? folder : parent;
        }

        private static bool IsInside(string candidate, string folder)
        {
            string a = Path.GetFullPath(candidate).TrimEnd('\\', '/') + "\\";
            string b = Path.GetFullPath(folder).TrimEnd('\\', '/') + "\\";
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Saves a still as <c>{prefix}yyyyMMdd_HHmmss_fff.jpg</c> in <paramref name="folder"/> through
        /// <see cref="SaveAtomic"/>, never overwriting (a clash gets <c>_2</c>, <c>_3</c>, ...). Returns the path.
        /// Throws on IO errors.</summary>
        public static string SaveCopy(byte[] jpeg, string folder, DateTime now, string prefix = "RED_",
            string? stagingFolder = null, bool createFolder = true)
        {
            string name = $"{prefix}{now:yyyyMMdd_HHmmss_fff}";
            for (int i = 1; ; i++)
            {
                string path = Path.Combine(folder, (i == 1 ? name : $"{name}_{i}") + ".jpg");
                if (File.Exists(path)) continue;
                try
                {
                    SaveAtomic(jpeg, path, stagingFolder, createFolder);
                    return path;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // another writer took the name between the check and the move
                }
            }
        }

        /// <summary>
        /// INSPECT2026's PathManager.GetCameraRollPath (OBSERVED from IL):
        /// - candidate roots in order: the Pictures known folder, %OneDrive%\Pictures, %OneDriveConsumer%\Pictures,
        ///   %OneDriveCommercial%\Pictures, %USERPROFILE%\Pictures (unset or invalid variables skipped; duplicates removed
        ///   case-insensitively after trimming trailing separators, the first spelling kept);
        /// - roots are walked in that order and missing ones skipped; the first existing root is the fallback;
        /// - in each root, the "Camera Roll*" subfolders (top level) ordered by last-write time, newest first: the FIRST
        ///   ROOT with any match wins (not the newest across roots);
        /// - if searching a root fails, the fallback is returned at once; with no match anywhere, the fallback; with no
        ///   existing root, null.
        /// </summary>
        public static string? ResolveCameraRoll(Func<string, string?>? environment = null, string? picturesFolder = null)
        {
            string? fallback = null;
            foreach (string root in CandidateRoots(environment, picturesFolder))
            {
                if (!Directory.Exists(root)) continue;
                fallback ??= root;
                try
                {
                    string? match = Directory.GetDirectories(root, "Camera Roll*")
                        .OrderByDescending(Directory.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (match != null) return match;
                }
                catch (Exception)
                {
                    return fallback; // INSPECT: "could not be searched" → the fallback root
                }
            }
            return fallback;
        }

        /// <summary>INSPECT2026's candidate roots, in order (see <see cref="ResolveCameraRoll"/>).</summary>
        public static IReadOnlyList<string> CandidateRoots(Func<string, string?>? environment = null, string? picturesFolder = null)
        {
            environment ??= Environment.GetEnvironmentVariable;
            var roots = new List<string>();
            void Add(string? root)
            {
                if (string.IsNullOrWhiteSpace(root)) return;
                try { _ = Path.GetFullPath(root); } catch (Exception) { return; }
                string key = root.TrimEnd('\\', '/');
                if (!roots.Any(r => string.Equals(r.TrimEnd('\\', '/'), key, StringComparison.OrdinalIgnoreCase)))
                    roots.Add(root);
            }
            Add(picturesFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            foreach (string variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial", "USERPROFILE" })
            {
                string? value = environment(variable);
                if (string.IsNullOrWhiteSpace(value)) continue;
                string combined;
                try { combined = Path.Combine(value, "Pictures"); } catch (Exception) { continue; }
                Add(combined);
            }
            return roots;
        }

        /// <summary>
        /// Inspect2022's PictureWatcher.GetPicturesPath (OBSERVED from IL by the companion research): Pictures\Camera Roll
        /// when it exists, else Pictures when it exists, else null. Its watcher is not recursive and takes "*.jpg" Created
        /// events only.
        /// </summary>
        public static string? ResolveInspect2022CameraRoll(string? picturesFolder = null)
        {
            string pictures = picturesFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrWhiteSpace(pictures)) return null;
            string roll = Path.Combine(pictures, "Camera Roll");
            if (Directory.Exists(roll)) return roll;
            return Directory.Exists(pictures) ? pictures : null;
        }

        /// <summary>
        /// The folder INSPECT2026 watches right now (<see cref="ResolveCameraRoll"/>), to save stills into so INSPECT
        /// picks them up. Null when nothing resolves. INSPECT never creates a folder, and a new folder would be watched only
        /// after the next item selection (and never by Inspect2022 inside Pictures), so neither does this.
        /// </summary>
        public static string? CameraRollFolderForSaving(Func<string, string?>? environment = null, string? picturesFolder = null) =>
            ResolveCameraRoll(environment, picturesFolder);
    }
}
