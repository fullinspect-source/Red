using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    public enum MoveOutcome { Moved, Cancelled, SourceGone, Failed }

    public sealed record MoveResult(MoveOutcome Outcome, string Message, string? Destination = null);

    // F-014 §5: Move = byte copy + verify + delete (no re-serialisation, no event), between {IF} folders. A same-named
    // destination is replaced only after the user agrees, and the replaced bytes go to RED's local backup first (M2).
    public static class FolderMoveService
    {
        public const string MovesBackupFolder = "MoveBackups";
        public const string AlreadyPickedUp = "Strand has already picked up this inspection. When it is processed it will appear in Archive, and you can move it to My List from there.";

        public static string ReplaceQuestion(string destFolder, string number) => $"{destFolder} already contains '{number}'. Replace it?";

        public static string Summary(int moved, int total) => $"{moved} of {total} inspections were moved.";

        public static MoveResult Move(string sourcePath, string inspectionsFolder, InspectFolder destination, Func<string, bool> confirmReplace,
            string? localRoot = null, Action<int>? sleep = null)
        {
            sleep ??= Thread.Sleep;
            if (!File.Exists(sourcePath))
            {
                bool fromOutbox = string.Equals(Path.GetFileName(Path.GetDirectoryName(sourcePath)), "Outbox", StringComparison.OrdinalIgnoreCase);
                return new MoveResult(MoveOutcome.SourceGone, fromOutbox ? AlreadyPickedUp : $"{Path.GetFileName(sourcePath)} is no longer there.");
            }
            byte[] bytes;
            try { bytes = InspectionFileReader.ReadBytes(sourcePath); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new MoveResult(MoveOutcome.Failed, $"{Path.GetFileName(sourcePath)} could not be read. {ex.Message}");
            }

            string number = Path.GetFileNameWithoutExtension(sourcePath);
            try
            {
                var json = InspectionFileReader.ParseDocument(bytes, out _);
                if (json["InspectionNumber"]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string?)json["InspectionNumber"]))
                    number = ((string)json["InspectionNumber"]!).Trim();
            }
            catch (Exception) { /* a damaged file still moves under its own name */ }
            if (number.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                number = Path.GetFileNameWithoutExtension(sourcePath);

            string destFolder;
            try
            {
                destFolder = destination == InspectFolder.Review
                    ? InspectFolders.EnsureReviewForMoveToReview(inspectionsFolder)
                    : InspectFolders.EnsureForWrite(inspectionsFolder, destination);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                return new MoveResult(MoveOutcome.Failed, $"The {destination} folder could not be prepared. {ex.Message}");
            }
            string target = Path.Combine(destFolder, number + ".ins");
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
                return new MoveResult(MoveOutcome.Failed, $"{number} is already in {destination}.");

            byte[]? replaced = null;
            if (File.Exists(target))
            {
                if (!confirmReplace(ReplaceQuestion(destination == InspectFolder.MyList ? "My List" : destination.ToString(), number)))
                    return new MoveResult(MoveOutcome.Cancelled, "");
                replaced = InspectionFileReader.ReadBytes(target);
                string backupFolder = Path.Combine(localRoot ?? AppIdentity.LocalAppDataPath, MovesBackupFolder);
                Directory.CreateDirectory(backupFolder);
                File.WriteAllBytes(Path.Combine(backupFolder, $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{destination}-{number}.ins"), replaced);
            }

            try
            {
                // Same atomic placement as Submit: staging on the same drive outside the Dropbox tree, then one rename.
                SubmitService.PlaceAtomically(target, bytes, Path.Combine(localRoot ?? AppIdentity.LocalAppDataPath, "move-staging"),
                    Path.GetDirectoryName(sourcePath)!, number + ".ins");
                SubmitService.ForgetPendingDelete(target, localRoot); // F-013 step 14: an unsubmitted copy is not the submitted one

            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new MoveResult(MoveOutcome.Failed, $"{number} could not be copied to {destination}. {ex.Message}");
            }

            // Delete the source; it must still be the bytes that were copied.
            for (int attempt = 0; attempt < 13; attempt++)
            {
                try
                {
                    if (!InspectionFileReader.ReadBytes(sourcePath).AsSpan().SequenceEqual(bytes))
                        return new MoveResult(MoveOutcome.Failed, $"{number} changed while it was moved; both copies were kept.", target);
                    File.Delete(sourcePath);
                    return new MoveResult(MoveOutcome.Moved, "", target);
                }
                catch (FileNotFoundException) { return new MoveResult(MoveOutcome.Moved, "", target); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { sleep(attempt < 10 ? 10 : 1000); }
            }
            return new MoveResult(MoveOutcome.Failed, $"{number} was copied to {destination}, but the original could not be removed.", target);
        }
    }

    // F-014 §6: what RED submitted, so the views can tell "waiting", "not picked up yet", "processed" and "not returned".
    public static class SubmittedTracker
    {
        public sealed class Entry
        {
            public string Number { get; set; } = "";
            public string InspectionsFolder { get; set; } = "";
            public DateTime SubmittedAt { get; set; }
            public string Kind { get; set; } = "";
        }

        public const string NotPickedUpYet = "Not picked up yet";
        public const string NotSentYet = "Not sent yet. Press Sync in the Outbox.";
        public const string NotReturnedYet = "Strand has not returned this inspection yet.";
        public const string Waiting = "Waiting for Strand";

        private static string PathOf(string? localRoot) => Path.Combine(localRoot ?? AppIdentity.LocalAppDataPath, "submitted.json");

        public static List<Entry> Load(string? localRoot = null)
        {
            try { return File.Exists(PathOf(localRoot)) ? JsonConvert.DeserializeObject<List<Entry>>(File.ReadAllText(PathOf(localRoot))) ?? new() : new(); }
            catch (Exception) { return new(); }
        }

        // S1 (S5: a Preinspector's accepted upload is its receipt; nothing is tracked).
        public static void Record(string number, string inspectionsFolder, InspectUserKind kind, DateTime when, string? localRoot = null)
        {
            if (kind == InspectUserKind.Preinspector)
                return;
            var entries = Load(localRoot).Where(e => !(e.Number == number && SameFolder(e.InspectionsFolder, inspectionsFolder))).ToList();
            entries.Add(new Entry { Number = number, InspectionsFolder = inspectionsFolder, SubmittedAt = when, Kind = kind.ToString() });
            if (entries.Count > 500) entries = entries.OrderByDescending(e => e.SubmittedAt).Take(500).ToList();
            string path = PathOf(localRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(entries, Formatting.Indented));
            File.Move(temp, path, overwrite: true);
        }

        // S2/S6: the Outbox row's note.
        public static string OutboxNote(string number, string inspectionsFolder, InspectUserKind kind, DateTime now, string? localRoot = null)
        {
            var entry = Load(localRoot).FirstOrDefault(e => e.Number == number && SameFolder(e.InspectionsFolder, inspectionsFolder));
            if (kind == InspectUserKind.NonDropBox)
                return NotSentYet;
            if (entry == null)
                return "";
            return now - entry.SubmittedAt > TimeSpan.FromMinutes(30) ? NotPickedUpYet : Waiting;
        }

        // S3: processed = Outbox copy gone and the Archive copy is the server's (LastDBDate set, last event 31
        // "Submitting inspection"). RED's own local Archive copy never counts.
        public static bool IsProcessed(string archivePath)
        {
            try
            {
                if (!File.Exists(archivePath)) return false;
                var json = InspectionFileReader.ParseDocument(InspectionFileReader.ReadBytes(archivePath), out _);
                if (HeaderRules.Blank(json["LastDBDate"])) return false;
                var last = (json["Events"] as JArray)?.OfType<JObject>().LastOrDefault();
                return last != null && HeaderRules.Int(last["EventTypeId"]) == 31 && (string?)last["AdditionalInfo"] == "Submitting inspection";
            }
            catch (Exception) { return false; }
        }

        // S4: submitted inspections whose Outbox copy is gone but no processed Archive copy came back within 60 minutes.
        public static List<string> NotReturned(string inspectionsFolder, DateTime now, string? localRoot = null)
        {
            var list = new List<string>();
            foreach (var entry in Load(localRoot).Where(e => SameFolder(e.InspectionsFolder, inspectionsFolder)))
            {
                if (now - entry.SubmittedAt < TimeSpan.FromMinutes(60)) continue;
                if (File.Exists(Path.Combine(inspectionsFolder, "Outbox", entry.Number + ".ins"))) continue;
                if (IsProcessed(Path.Combine(inspectionsFolder, "Archive", entry.Number + ".ins"))) continue;
                list.Add(entry.Number);
            }
            return list;
        }

        private static bool SameFolder(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }
}
