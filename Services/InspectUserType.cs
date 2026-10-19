using Newtonsoft.Json.Linq;
using System;
using System.Globalization;

namespace InspectionEditor.Services
{
    public enum InspectUserKind { Inspector, Preinspector, NonDropBox, Missing, Other }

    // One read of an INSPECT config file: Config is null when neither config.json nor its .bak could be read
    // (F-003 S4); BackupDate is set when the .bak was used; Path is the file that was read (or would have been).
    public sealed record InspectConfigRead(JObject? Config, string Path, DateTime? BackupDate);

    // INSPECT's FormConfig.UserType, classified as the program spec requires (F-003 O9/O10, owner Q-22).
    // INSPECT compares with case-sensitive String.op_Equality everywhere, so this uses exact ordinal matches.
    public static class InspectUserType
    {
        public const string ProfileIncompleteMessage = "Complete your inspector profile in RED Settings first.";
        public const string UnreadableMessage = "RED could not read INSPECT's settings. Enter them in RED Settings.";
        // RED has no user type picker (owner, 2026-10-18): the type is changed in INSPECT and imported.
        public const string ChangeHint = "To change it, change Type in INSPECT Settings, then press Import from INSPECT.";

        public static InspectUserKind Classify(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return InspectUserKind.Missing; // RED never assumes "Inspector" for a blank value
            return value switch
            {
                "Inspector" => InspectUserKind.Inspector,
                "Preinspector" => InspectUserKind.Preinspector,
                "NonDropBoxInspector" => InspectUserKind.NonDropBox,
                _ => InspectUserKind.Other // includes "PreInspector" and "preinspector"
            };
        }

        // UserType from one INSPECT config.json (O5): a file with no UserType key gets INSPECT's default "Inspector"
        // (Newtonsoft keeps the constructor default); a present but null or blank value stays blank (Missing);
        // no config at all is Missing. The key is matched like Newtonsoft does (exact first, then ignoring case).
        public static string? Resolve(JObject? config)
        {
            if (config == null)
                return null;
            var property = config.Property("UserType", StringComparison.OrdinalIgnoreCase);
            if (property == null)
                return "Inspector";
            return property.Value.Type == JTokenType.String ? property.Value.Value<string>() : null;
        }

        // New inspections are preinspections only for kind Preinspector (O6).
        public static bool IsPreinspection(InspectUserKind kind) => kind == InspectUserKind.Preinspector;

        // Only real Inspectors ask the website for the job's files (Inspect's AddFormToMyList; UT §5).
        public static bool RequestsJobFiles(InspectUserKind kind) => kind == InspectUserKind.Inspector;

        // The Settings line for each kind (O10).
        public static string KindLine(InspectUserKind kind) => kind switch
        {
            InspectUserKind.Inspector => "Submits through the Outbox; Dropbox delivers it.",
            InspectUserKind.Preinspector => "Submit uploads each inspection to Strand.",
            InspectUserKind.NonDropBox => "Submits to the Outbox; press Sync in the Outbox to send.",
            InspectUserKind.Other => "Not a type INSPECT offers; RED submits through the Outbox, as INSPECT does.",
            _ => "Not set. Set the Type in INSPECT Settings, then press Import from INSPECT."
        };

        // S4: shown when the values came from config.json.bak.
        public static string BackupMessage(DateTime backupDate) =>
            "INSPECT's settings file could not be read. Values were taken from its backup dated " +
            backupDate.ToString("M/d/yyyy", CultureInfo.InvariantCulture) + ".";
    }
}
