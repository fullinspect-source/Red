using System;
using System.IO;

namespace InspectionEditor.Services
{
    // The folders under the Inspections folder ({IF}, F-003 O7). Each name is the folder's name on disk.
    public enum InspectFolder { MyList, Outbox, Archive, Review, Logs, Jobs, Documents, Data, Temp }

    // F-003 O8: who may create which folder. INSPECT creates them all at start and after Settings
    // (PathManager::CreateInspectFolderStructure), so on a device where INSPECT never ran they may be missing.
    // RED creates MyList, Outbox and Archive only right before writing into one, Review only for a reviewer's Move to
    // Review (F-018) and Logs only for the F-019 L5 log copy. Temp, Documents, Data and Jobs belong to INSPECT or the
    // server: RED reads them and never creates them. RED never creates the Inspections folder itself.
    public static class InspectFolders
    {
        public static string PathOf(string inspectionsFolder, InspectFolder folder) => Path.Combine(inspectionsFolder, folder.ToString());

        // Call right before writing a file into MyList, Outbox or Archive; returns the folder's path.
        public static string EnsureForWrite(string inspectionsFolder, InspectFolder folder)
        {
            if (folder is not (InspectFolder.MyList or InspectFolder.Outbox or InspectFolder.Archive))
                throw new InvalidOperationException($"RED does not create the {folder} folder before a write (F-003 O8).");
            return Create(inspectionsFolder, folder);
        }

        // F-018: only a reviewer's Move to Review.
        public static string EnsureReviewForMoveToReview(string inspectionsFolder) => Create(inspectionsFolder, InspectFolder.Review);

        // F-019 L5: only the log copy to Dropbox, as INSPECT's InitialTasks.
        public static string EnsureLogsForLogCopy(string inspectionsFolder) => Create(inspectionsFolder, InspectFolder.Logs);

        private static string Create(string inspectionsFolder, InspectFolder folder)
        {
            if (string.IsNullOrWhiteSpace(inspectionsFolder) || !Directory.Exists(inspectionsFolder))
                throw new DirectoryNotFoundException($"The Inspections folder {inspectionsFolder} does not exist.");
            string path = PathOf(inspectionsFolder, folder);
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
