using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    public sealed record HeaderMarker(string Key, string Text, bool Blocking);

    public sealed record StatusOption(int Id, string Name);

    public sealed record NextActionOption(int Id, string Text, string Label);

    // F-007 header sections (Top and End): INSPECT's field markers, the Footer's next-action table and the
    // InspectionNumber rule. The same header rules feed F-012's completion check (I1-I11, I13-I18).
    public static class HeaderRules
    {
        // E2: index = NextActionId, NextActionText exact.
        public static readonly IReadOnlyList<NextActionOption> NextActions = new[]
        {
            new NextActionOption(0, " ", "(none)"),
            new NextActionOption(1, "Failed items to be inspected at next inspection", "Failed items to be inspected at next inspection"),
            new NextActionOption(2, "Request a reinspection. (PO required)", "Request a reinspection. (PO required)"),
            new NextActionOption(3, "Request a reinspection.", "Request a reinspection.")
        };

        public const int FailedStatusId = 3;
        public const string NextActionRequired = "Next action is required.";
        public const string StatusCannotBeOrdered = "Status cannot be Ordered.";
        public const string AttemptTooLow = "Attempt must be 1 or greater.";
        public const string PoRequired = "Purchase Order or Requisition Number required.";
        public const string NotifyPair = "Notify Who and Notify When must both be filled or both be empty.";
        public const string FixInStrandHint = "Fix in Strand";

        // T3: what RED lets the inspector edit until Research decodes TopControl. Date is always display-only (T5).
        public static readonly IReadOnlyList<string> EditableTopKeys = new[]
        {
            "Lot", "Block", "PlanNumber", "StructureTypeId", "Contact", "ContactNumber", "IsReinspection", "Attempt",
            "PurchaseOrderRequired", "PurchaseOrderNumber", "RequisitionNumber", "WhoNotified", "WhenNotified", "Requests"
        };

        // StructureTypeId names, as INSPECT's reports print them (R-report-layouts L7: Shared::LookupStructure @IL_0001; INFERRED that TopControl's editor uses the same ids).
        public static readonly IReadOnlyList<(int Id, string Name)> Structures = new[]
        {
            (0, "Main House"), (1, "Pavilion"), (2, "Pool House"), (3, "Outdoor Kitchen"), (4, "Addition")
        };

        public static readonly IReadOnlyList<string> EditableEndKeys = new[] { "StatusId", "NextActionId", "NextActionText", "InternalNotes" };

        // Owner Q-21: fees are never shown or edited; the header path refuses these keys.
        public static readonly IReadOnlyList<string> FeeKeys = new[]
        {
            "InspectionFee_Secure", "InspectorOtherFee1_Secure", "InspectorOtherFee2_Secure", "OtherFeeDescription1", "OtherFeeDescription2"
        };

        public static bool IsFeeKey(string key) => FeeKeys.Contains(key, StringComparer.Ordinal);

        public static string? Text(JToken? token) =>
            token == null || token.Type == JTokenType.Null ? null : token.Type == JTokenType.String ? (string?)token : token.ToString();

        public static bool Blank(JToken? token) => string.IsNullOrWhiteSpace(Text(token));

        public static int Int(JToken? token, int fallback = 0) =>
            token != null && token.Type is JTokenType.Integer or JTokenType.Float ? (int)(double)token :
            int.TryParse(Text(token), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

        public static bool Bool(JToken? token) =>
            token != null && (token.Type == JTokenType.Boolean ? (bool)token : bool.TryParse(Text(token), out bool b) && b);

        // T1 markers, as INSPECT's TopControl/FooterControl texts (SUB §4) and the F-012 header rules.
        public static List<HeaderMarker> Markers(Func<string, JToken?> get)
        {
            var markers = new List<HeaderMarker>();
            void Required(string key, string text, bool blocking = true)
            {
                if (Blank(get(key))) markers.Add(new HeaderMarker(key, text, blocking));
            }

            // INSPECT reads DateInspected as DateTime?: a blank string is null there too.
            if (get("DateInspected") is not JToken date || date.Type == JTokenType.Null || Blank(date))
                markers.Add(new HeaderMarker("DateInspected", "Inspection Date required.", true)); // I1
            int status = Int(get("StatusId"));
            if (status is 0 or 1)
                markers.Add(new HeaderMarker("StatusId", StatusCannotBeOrdered, true)); // I2, I3
            Required("BuilderName", "Builder required.");       // I4
            Required("Project", "Project required.");           // I5
            Required("Address", "Address required.");           // I6
            Required("City", "City required.");                 // I7
            Required("State", "State required.");               // I8
            string job = Text(get("JobNumber")) ?? "";
            // I9, I14, I15; a null or shorter-than-7 JobNumber crashes INSPECT's Validate, RED reports it the same way.
            if (string.IsNullOrWhiteSpace(job) || job.Length < 7 || job.Length > 10 || job.Substring(0, 7) == "0000000")
                markers.Add(new HeaderMarker("JobNumber", "S2E Number required.", true));
            Required("PlanNumber", "Plan Number required.");    // I10
            Required("InspectorName", "Inspector required.");   // I11
            if (Int(get("Attempt")) <= 0)
                markers.Add(new HeaderMarker("Attempt", AttemptTooLow, true)); // I13
            if (Bool(get("PurchaseOrderRequired")) && Blank(get("PurchaseOrderNumber")) && Blank(get("RequisitionNumber")))
                markers.Add(new HeaderMarker("PurchaseOrderNumber", PoRequired, true)); // I16
            if (Blank(get("WhoNotified")) != Blank(get("WhenNotified")))
                markers.Add(new HeaderMarker(Blank(get("WhoNotified")) ? "WhoNotified" : "WhenNotified", NotifyPair, true)); // I17
            if (status == FailedStatusId && Int(get("NextActionId")) == 0)
                markers.Add(new HeaderMarker("NextActionId", NextActionRequired, true)); // I18
            // I20: INSPECT tests only Signature != null; an empty array passes (owner Q-36 default).
            if (get("Signature") is not JToken signature || signature.Type == JTokenType.Null)
                markers.Add(new HeaderMarker("Signature", "Signature required.", true));
            // Lot and Block: red markers that do not block (SUB §2.3 item 9).
            Required("Lot", "Lot required.", blocking: false);
            Required("Block", "Block required.", blocking: false);
            return markers;
        }

        public static List<StatusOption> StatusList(JToken? statusList)
        {
            var list = new List<StatusOption>();
            if (statusList is not JArray array)
                return list;
            foreach (var entry in array.OfType<JObject>())
            {
                var id = entry["Id"];
                if (id == null || id.Type is not (JTokenType.Integer or JTokenType.Float or JTokenType.String))
                    continue;
                list.Add(new StatusOption(Int(id, -1), Text(entry["Name"]) ?? ""));
            }
            return list;
        }

        // F-013 O5 / Inspection::BuildInspectionNumber: {job7}-{code}-{attempt}-{initials}, with "-P-" before the
        // attempt when IsPreinspection. Initials are Inspection::GetInitials of the file's InspectorName.
        public static string BuildInspectionNumber(string? jobNumber, string? code, int attempt, string? inspectorName,
            bool isPreinspection, string? fallbackInitials = null)
        {
            string job = (jobNumber ?? "").Trim();
            string job7 = job.Length > 7 ? job.Substring(0, 7) : job;
            string initials = InspectorProfileService.Initials(inspectorName);
            if (string.IsNullOrEmpty(initials))
                initials = fallbackInitials ?? "";
            string attemptText = attempt.ToString(CultureInfo.InvariantCulture);
            return isPreinspection
                ? $"{job7}-{code}-P-{attemptText}-{initials}"
                : $"{job7}-{code}-{attemptText}-{initials}";
        }

        // The initials part of an existing number (its last dash-separated part), used when the file has no InspectorName.
        public static string? InitialsOf(string? inspectionNumber)
        {
            if (string.IsNullOrWhiteSpace(inspectionNumber))
                return null;
            int dash = inspectionNumber.LastIndexOf('-');
            return dash >= 0 && dash < inspectionNumber.Length - 1 ? inspectionNumber.Substring(dash + 1) : null;
        }

        // F-002 B5: a date RED sets is local time with offset, yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz.
        public static string FormatDate(DateTime local) =>
            local.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);

        // Display of a stored date (verbatim on failure).
        public static string DisplayDate(JToken? token, string format = "M/d/yyyy h:mm tt")
        {
            string? text = Text(token);
            if (string.IsNullOrEmpty(text))
                return "";
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
                ? value.LocalDateTime.ToString(format, CultureInfo.GetCultureInfo("en-US"))
                : text;
        }

        public static bool TryParseLocalDate(string? text, out DateTime local)
        {
            local = default;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return DateTime.TryParse(text.Trim(), CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AssumeLocal, out local);
        }
    }
}
