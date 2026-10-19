using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    /// <summary>Result of an F-002 Mode B projection. Warnings name JSON paths only, never values.</summary>
    public sealed class InspectProjection
    {
        public InspectProjection(string json, IReadOnlyList<string> warnings)
        {
            Json = json;
            Warnings = warnings;
        }

        public string Json { get; }
        public IReadOnlyList<string> Warnings { get; }
        public byte[] ToUtf8Bytes() => new UTF8Encoding(false).GetBytes(Json);
    }

    /// <summary>
    /// F-002 Mode B: the bytes INSPECT2026 itself would write for an inspection (SPEC §4 B1-B7). Used by Submit and
    /// Approve; never for the ordinary save. Key lists and model defaults follow SAE.Inspect.Models.* and
    /// SAE.Inspect.Shared.{Picture,Attachment} in Inspect2026.dll 2026.1012.2.0 (field types read with REA).
    /// </summary>
    public static class InspectProjectionWriter
    {
        private enum Kind { String, Int, Bool, NullableBool, NullableInt, Guid, NullableGuid, Date, NullableDate, Bytes,
            StringList, IntList, ObjectList, DisplayLabel }

        private sealed record Field(string Name, Kind Kind, JToken? Default = null, Field[]? Children = null);

        private const string EmptyGuid = "00000000-0000-0000-0000-000000000000";
        // Newtonsoft's text for DateTime.MinValue (Kind Unspecified): what INSPECT writes for an absent non-nullable date.
        private const string MinDate = "0001-01-01T00:00:00";

        private static Field S(string name) => new(name, Kind.String);
        private static Field I(string name) => new(name, Kind.Int);
        private static Field B(string name) => new(name, Kind.Bool);

        private static readonly Field[] StatusFields = { I("Id"), S("Name") };

        private static readonly Field[] PictureFields = { S("Path"), S("Caption"), S("Description"), new("Image", Kind.Bytes) };

        private static readonly Field[] ItemFields =
        {
            I("ItemId"), S("Name"), S("Number"), new("ControlName", Kind.String, "ItemStandardControl"),
            S("ControlFormatString"), S("ItemGroupId"), S("ItemGroupControlName"), I("PictureSizeId"), B("IsCopied"),
            B("Hide"), new("MaxIncidents", Kind.NullableInt), I("SortOrder"), new("Required", Kind.Bool, true),
            B("AlwaysPrint"), S("OnlyPrint"), S("Template"), S("ButtonText"), B("HideCommentsButton"),
            B("HidePicturesButton"), B("HideAddButton"), new("CommentList", Kind.StringList, new JArray()),
            new("ValueList", Kind.StringList), B("IsPictureRequired"),
            new("Pictures", Kind.ObjectList, new JArray(), PictureFields), new("DisplayLabel", Kind.DisplayLabel),
            S("Value"), I("ResultSortOrder"), S("Comments"), new("Incidents", Kind.NullableInt)
        };

        private static readonly Field[] SectionFields =
        {
            I("SectionId"), S("Name"), S("ControlName"), S("Number"),
            new("Items", Kind.ObjectList, new JArray(), ItemFields), I("SortOrder")
        };

        private static readonly Field[] EventFields =
        {
            new("Date", Kind.Date), I("EventTypeId"), S("Username"), S("UserDisplayName"), S("AdditionalInfo")
        };

        private static readonly Field[] AttachmentFields =
        {
            S("InspectVersion"), new("Id", Kind.Guid), S("EditPath"), new("FileData", Kind.Bytes), S("FileType"),
            S("Filename"), new("AnnotatedPages", Kind.IntList, new JArray()), new("IncludedPages", Kind.IntList, new JArray()),
            B("ReferenceOnly"), new("LastWriteTime", Kind.Date), B("CanExcludePages")
        };

        private static readonly Field[] InspectionFields =
        {
            S("InspectVersion"), new("WorkOrderId", Kind.Guid), I("ServiceId"), I("FormId"), B("IsTransfer"),
            B("IsPreinspection"), B("NoCharge"), B("IsApproval"), S("InspectionName"), S("InspectionNumber"),
            S("InspectionCode"), I("Attempt"), I("StatusId"), new("StatusList", Kind.ObjectList, new JArray(), StatusFields),
            I("Version"), new("LastDBDate", Kind.NullableDate), S("ErrorMessage"), S("PlanNumber"), S("BuilderName"),
            new("BuilderAccountId", Kind.NullableGuid), S("Contact"), S("ContactNumber"), S("WhoNotified"),
            new("WhenNotified", Kind.NullableDate), S("JobNumber"), S("Project"), S("Address"), S("Lot"), S("Block"),
            S("City"), S("State"), S("InspectorUsername"), S("InspectorName"), S("InspectorPhone"),
            S("SubmittorUserName"), new("DateInspected", Kind.NullableDate), S("TimePeriodInspected"),
            I("StructureTypeId"), I("NextActionId"), S("NextActionText"), new("CreatedDate", Kind.Date),
            new("LastModifiedDate", Kind.Date), S("LastModifiedUsername"), new("IsReinspection", Kind.NullableBool),
            S("NoChargeReason"), new("PurchaseOrderRequired", Kind.NullableBool), S("PurchaseOrderNumber"),
            S("RequisitionNumber"), S("SuperintendentName"), S("Subcontractor"), B("ManuallyEnteredAddress"),
            S("Requests"), new("Signature", Kind.Bytes), S("DocumentTypeId"),
            // Q-21: fee ciphertexts and descriptions pass through untouched (strings are copied verbatim).
            S("InspectionFee_Secure"), S("InspectorOtherFee1_Secure"), S("InspectorOtherFee2_Secure"),
            S("OtherFeeDescription1"), S("OtherFeeDescription2"), S("InternalNotes"),
            new("Sections", Kind.ObjectList, new JArray(), SectionFields),
            new("Events", Kind.ObjectList, new JArray(), EventFields),
            new("Attachments", Kind.ObjectList, new JArray(), AttachmentFields)
        };

        public static IReadOnlyList<string> TopLevelKeys { get; } = InspectionFields.Select(f => f.Name).ToArray();
        public static IReadOnlyList<string> SectionKeys { get; } = SectionFields.Select(f => f.Name).ToArray();
        public static IReadOnlyList<string> ItemKeys { get; } = ItemFields.Select(f => f.Name).ToArray();
        public static IReadOnlyList<string> PictureKeys { get; } = PictureFields.Select(f => f.Name).ToArray();
        public static IReadOnlyList<string> EventKeys { get; } = EventFields.Select(f => f.Name).ToArray();
        public static IReadOnlyList<string> AttachmentKeys { get; } = AttachmentFields.Select(f => f.Name).ToArray();

        /// <summary>Projects an .ins document (any dialect) to INSPECT-local format. The input is not modified.</summary>
        public static InspectProjection Project(JObject document)
        {
            var warnings = new List<string>();
            JObject projected = ProjectObject(document, InspectionFields, "$", warnings);
            // JObject.ToString uses a default JsonTextWriter: the same escaping and number text as JsonConvert.SerializeObject.
            return new InspectProjection(projected.ToString(Formatting.None), warnings);
        }

        /// <summary>B5: a date RED sets, written as INSPECT writes a local DateTime (offset kept, fraction trimmed).</summary>
        public static string FormatInspectDate(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc) value = value.ToLocalTime();
            else if (value.Kind == DateTimeKind.Unspecified) value = DateTime.SpecifyKind(value, DateTimeKind.Local);
            return value.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFFzzz", CultureInfo.InvariantCulture);
        }

        /// <summary>B4: INSPECT's computed label, "{Number} {Name}" plus " *" when Required.</summary>
        public static string DisplayLabel(string? number, string? name, bool required) =>
            number + " " + name + (required ? " *" : "");

        private static JObject ProjectObject(JObject source, Field[] fields, string path, List<string> warnings)
        {
            var result = new JObject();
            foreach (var field in fields)
            {
                if (field.Kind == Kind.DisplayLabel)
                {
                    result.Add(field.Name, null); // placeholder keeps the key order; filled below
                    continue;
                }
                result.Add(field.Name, Coerce(source[field.Name], field, path + "." + field.Name, warnings));
            }
            if (result.ContainsKey("DisplayLabel"))
            {
                string? number = result["Number"]?.Type == JTokenType.String ? (string?)result["Number"] : null;
                string? name = result["Name"]?.Type == JTokenType.String ? (string?)result["Name"] : null;
                bool required = result["Required"]?.Type == JTokenType.Boolean && (bool)result["Required"]!;
                result["DisplayLabel"] = DisplayLabel(number, name, required);
            }
            return result;
        }

        private static JToken Coerce(JToken? token, Field field, string path, List<string> warnings)
        {
            if (token == null) return DefaultFor(field);
            if (token.Type == JTokenType.Null)
            {
                switch (field.Kind)
                {
                    case Kind.Int:
                    case Kind.Bool:
                    case Kind.Guid:
                    case Kind.Date:
                        // INSPECT cannot load null into a non-nullable member; keep the null and report it.
                        warnings.Add(path + ": null for a non-nullable INSPECT member");
                        break;
                }
                return JValue.CreateNull();
            }
            switch (field.Kind)
            {
                case Kind.String:
                    return token.Type switch
                    {
                        JTokenType.String => token.DeepClone(),
                        JTokenType.Integer or JTokenType.Float =>
                            new JValue(Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture)),
                        // JsonTextReader.ReadAsString returns the literal token text for true/false.
                        JTokenType.Boolean => new JValue((bool)token ? "true" : "false"),
                        _ => Unexpected(token, path, warnings)
                    };
                case Kind.Int:
                case Kind.NullableInt:
                    if (token.Type == JTokenType.Integer) return token.DeepClone();
                    if (token.Type == JTokenType.Float && decimal.TryParse(Convert.ToString(((JValue)token).Value,
                            CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d) &&
                        d == decimal.Truncate(d))
                        return new JValue((long)d);
                    if (token.Type == JTokenType.String &&
                        long.TryParse((string?)token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                        return new JValue(l);
                    return Unexpected(token, path, warnings);
                case Kind.Bool:
                case Kind.NullableBool:
                    if (token.Type == JTokenType.Boolean) return token.DeepClone();
                    if (token.Type == JTokenType.String && bool.TryParse((string?)token, out bool b)) return new JValue(b);
                    return Unexpected(token, path, warnings);
                case Kind.Guid:
                case Kind.NullableGuid:
                    if (token.Type == JTokenType.String && Guid.TryParse((string?)token, out Guid guid))
                        return new JValue(guid.ToString("D"));
                    return Unexpected(token, path, warnings);
                case Kind.Date:
                case Kind.NullableDate:
                    // B5: a date RED did not change is written verbatim (the reader keeps dates as strings).
                    return token.Type == JTokenType.String ? token.DeepClone() : Unexpected(token, path, warnings);
                case Kind.Bytes:
                    return token.Type == JTokenType.String ? NormalizeBase64((string)token!, path, warnings)
                        : Unexpected(token, path, warnings);
                case Kind.StringList:
                case Kind.IntList:
                    if (token is not JArray list) return Unexpected(token, path, warnings);
                    var element = new Field("", field.Kind == Kind.StringList ? Kind.String : Kind.Int);
                    return new JArray(list.Select((e, i) => Coerce(e, element, path + "[" + i + "]", warnings)));
                case Kind.ObjectList:
                    if (token is not JArray objects) return Unexpected(token, path, warnings);
                    return new JArray(objects.Select((e, i) => e switch
                    {
                        JObject o => (JToken)ProjectObject(o, field.Children!, path + "[" + i + "]", warnings),
                        { Type: JTokenType.Null } => JValue.CreateNull(),
                        _ => Unexpected(e, path + "[" + i + "]", warnings)
                    }));
                default:
                    return Unexpected(token, path, warnings);
            }
        }

        private static JToken DefaultFor(Field field)
        {
            if (field.Default != null) return field.Default.DeepClone();
            return field.Kind switch
            {
                Kind.Int => new JValue(0),
                Kind.Bool => new JValue(false),
                Kind.Guid => new JValue(EmptyGuid),
                Kind.Date => new JValue(MinDate),
                _ => JValue.CreateNull()
            };
        }

        private static JToken Unexpected(JToken token, string path, List<string> warnings)
        {
            warnings.Add(path + ": unexpected JSON type " + token.Type + "; copied unchanged");
            return token.DeepClone();
        }

        // byte[] round trip: INSPECT decodes and re-encodes, which yields standard padded base64 without line breaks.
        private static JToken NormalizeBase64(string value, string path, List<string> warnings)
        {
            if (value.Length % 4 == 0 && value.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '='))
                return new JValue(value);
            try { return new JValue(Convert.ToBase64String(Convert.FromBase64String(value))); }
            catch (FormatException)
            {
                warnings.Add(path + ": not valid base64; copied unchanged");
                return new JValue(value);
            }
        }
    }
}
