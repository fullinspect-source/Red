using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace InspectionEditor.Services
{
    /// <summary>
    /// F-018 W1-W3. INSPECT's hard-coded reviewer list is not copied: the Review folder's content decides (the server
    /// fills it only for reviewer accounts); owner 2026-10-20: there is no Show Review setting. No Approve.
    /// Review is the owner's holding folder for their own automation; RED never submits, moves or rewrites a Review file on
    /// its own (only the user's own save of an open Review file writes it, F-002 E1).
    /// </summary>
    public static class ReviewAccess
    {
        public const string ViewRefused = "You have insufficient permissions to view the Review folder.";
        public const string MoveRefused = "You have insufficient permissions.";

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

        // W1/W2: the one flag for the Review view (and moving files to Review). Owner 2026-10-20: not a setting; Review
        // shows when the Review folder has reports (Strand fills it only for reviewer accounts), never for other inspectors.
        public static bool Allowed(string? inspectionsFolder) => CountReviewFiles(inspectionsFolder) > 0;

        // W3: a file whose parent folder is exactly {IF}\Review (not any path containing "Review").
        public static bool IsInReview(string? filePath, string? inspectionsFolder)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(inspectionsFolder)) return false;
            string? parent = Path.GetDirectoryName(Path.GetFullPath(filePath));
            return parent != null && string.Equals(parent.TrimEnd('\\'), Path.GetFullPath(ReviewFolder(inspectionsFolder)).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
