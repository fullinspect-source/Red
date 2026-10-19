using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace InspectionEditor.Services
{
    public enum ReviewVisibility { Auto, Always, Never }

    // F-018 W1: RED's "Show Review" setting (auto / always / never), in RED's app data.
    public sealed class ReviewSettings
    {
        public ReviewVisibility ShowReview { get; set; } = ReviewVisibility.Auto;

        private static string PathOf(string? root) => Path.Combine(root ?? AppIdentity.LocalAppDataPath, "review.json");

        public static ReviewSettings Load(string? root = null)
        {
            try { return File.Exists(PathOf(root)) ? JsonConvert.DeserializeObject<ReviewSettings>(File.ReadAllText(PathOf(root))) ?? new() : new(); }
            catch (Exception) { return new(); }
        }

        public void Save(string? root = null)
        {
            string path = PathOf(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(this, Formatting.Indented));
            File.Move(temp, path, overwrite: true);
        }
    }

    public sealed record ApproveRunResult(int Approved, int Total, string? FailedFile, string? FailedMessage)
    {
        public bool Succeeded { get; init; }
        public string Summary => ReviewAccess.ApprovedSummary(Approved, Total) +
            (FailedMessage == null ? "" : Environment.NewLine + Environment.NewLine + (FailedFile == null ? FailedMessage : $"{FailedFile}: {FailedMessage}"));
    }

    /// <summary>
    /// F-018 W1-W3, A3-A4. INSPECT's hard-coded reviewer list is not copied: the Review folder's content decides (the
    /// server fills it only for reviewer accounts), with RED's auto / always / never setting.
    /// </summary>
    public static class ReviewAccess
    {
        public const string ViewRefused = "You have insufficient permissions to view the Review folder.";
        public const string MoveRefused = "You have insufficient permissions.";
        public const string ApprovePermissionRefused = "You do not have permissions to Approve inspections.";

        public static string OpenInEditor(string number) => $"{number} is open. Approve it from its window, or close it first.";
        public static string ApprovedSummary(int approved, int total) => $"{approved} of {total} inspections were approved.";

        public static string ReviewFolder(string inspectionsFolder) => InspectFolders.PathOf(inspectionsFolder, InspectFolder.Review);

        // V1: the navigation count, by counting files without parsing them.
        public static int CountReviewFiles(string? inspectionsFolder)
        {
            if (string.IsNullOrWhiteSpace(inspectionsFolder)) return 0;
            try
            {
                string folder = ReviewFolder(inspectionsFolder);
                return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.ins", SearchOption.TopDirectoryOnly).Count() : 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return 0; }
        }

        // W1/W2: the one flag for the Review view, Move to Review and Approve.
        public static bool Allowed(string? inspectionsFolder, ReviewSettings? settings = null)
        {
            var visibility = (settings ?? ReviewSettings.Load()).ShowReview;
            return visibility == ReviewVisibility.Always ||
                   visibility == ReviewVisibility.Auto && CountReviewFiles(inspectionsFolder) > 0;
        }

        // W3: Approve only for a file whose parent folder is exactly {IF}\Review (not any path containing "Review").
        public static bool IsInReview(string? filePath, string? inspectionsFolder)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(inspectionsFolder)) return false;
            string? parent = Path.GetDirectoryName(Path.GetFullPath(filePath));
            return parent != null && string.Equals(parent.TrimEnd('\\'), Path.GetFullPath(ReviewFolder(inspectionsFolder)).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        public static bool CanApprove(string? filePath, string? inspectionsFolder, ReviewSettings? settings = null) =>
            IsInReview(filePath, inspectionsFolder) && Allowed(inspectionsFolder, settings);

        // A3: one Approve run per file at a time (editor and list share it).
        private static readonly HashSet<string> Running = new(StringComparer.OrdinalIgnoreCase);

        public static bool TryBegin(string path)
        {
            lock (Running) return Running.Add(Path.GetFullPath(path));
        }

        public static void End(string path)
        {
            lock (Running) Running.Remove(Path.GetFullPath(path));
        }

        /// <summary>
        /// A4: the Review list's Approve, file by file in list order; the first failure stops the run. A file open in a
        /// RED editor is refused (and stops the run).
        /// </summary>
        public static async Task<ApproveRunResult> ApproveFilesAsync(IReadOnlyList<string> paths, CompleteProfile profile, string? rawUserType,
            Func<string, bool> isOpenInEditor, ISubmitLog? log = null, string? localRoot = null, Action<int>? sleep = null, CancellationToken token = default)
        {
            int approved = 0;
            var notes = new List<string>();
            foreach (string path in paths)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                // W3: only a file whose parent folder is exactly {IF}\Review is ever approved.
                if (!IsInReview(path, profile.Folder))
                    return new ApproveRunResult(approved, paths.Count, name, ApprovePermissionRefused);
                if (isOpenInEditor(path))
                    return new ApproveRunResult(approved, paths.Count, null, OpenInEditor(name));
                if (!TryBegin(path))
                    return new ApproveRunResult(approved, paths.Count, name, "An approval of this inspection is already running.");
                try
                {
                    var saver = new SurgicalSaveService();
                    CprRules.Install(saver);
                    var model = saver.Load(path);
                    var submitLog = log ?? InspectFormatSubmitLog.Instance;
                    var ctx = new SubmitContext
                    {
                        Approve = true,
                        Model = model,
                        Saver = saver,
                        SourcePath = path,
                        Profile = profile,
                        RawUserType = rawUserType,
                        // A1 step 3: RED's Save command on the loaded file (V5c and V5d sweeps, V1), without the stamp.
                        SaveWithSweeps = () =>
                        {
                            ItemAutoFailRules.ApplySaveCommandSweep(model);
                            ItemAutoFailRules.ApplyAutoSaveSweep(model);
                            saver.Save(model);
                            foreach (string line in InspectFormatLog.SavingLines(saver.GetHeaderValue, model, path, written: true)) // F-019 L7
                                submitLog.Inspect(line);
                            return true;
                        },
                        LocalRoot = localRoot,
                        Log = submitLog,
                        Sleep = sleep
                    };
                    var result = await SubmitService.SubmitAsync(ctx, token);
                    if (!result.Succeeded)
                        return new ApproveRunResult(approved, paths.Count, name, result.Message);
                    approved++;
                    if (result.Stage == SubmitStage.SourceNotRemoved || profile.Kind == InspectUserKind.NonDropBox)
                        notes.Add($"{name}: {result.Message}");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is JsonException)
                {
                    return new ApproveRunResult(approved, paths.Count, name, "Cannot approve inspection. " + ex.Message);
                }
                finally { End(path); }
            }
            return new ApproveRunResult(approved, paths.Count, null, notes.Count == 0 ? null : string.Join(Environment.NewLine, notes)) { Succeeded = true };
        }
    }
}
