using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using InspectionEditor.Models;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    public sealed record CompletionProblem(string Rule, string Text, string? HeaderKey, Item? Item, bool Blocking);

    public sealed class CompletionResult
    {
        public List<CompletionProblem> Problems { get; } = new();
        public bool FeeRuleSkipped { get; set; }
        public bool IsValid => !Problems.Any(p => p.Blocking);
        public IEnumerable<CompletionProblem> Blocking => Problems.Where(p => p.Blocking);
    }

    // F-012: INSPECT's completion check (Inspection::Validate and Item::Validate, owner Q-10: exactly INSPECT's rules).
    // RED blocks exactly what INSPECT blocks; the only differences are the JobNumber crash (reported as "S2E Number
    // required.") and a fee rule that cannot be evaluated (shown, never silently passed or failed).
    public static class InspectionValidator
    {
        public const string SubmitRefused = "Cannot submit inspection. You need to fix one or more problems with the inspection.";
        public const string ApproveRefused = "Cannot approve inspection. You need to fix one or more problems with the inspection.";
        public const string PrintRefused = "Cannot print inspection. You need to fix one or more problems with the inspection.";
        public const string FeeReasonText = "Must provide a reason for Other Fee.";
        public const string FeeRuleSkippedText = "Fee rule not checked on this device";

        private static readonly Dictionary<string, string> HeaderRuleIds = new(StringComparer.Ordinal)
        {
            ["Inspection Date required."] = "I1", ["Status cannot be Ordered."] = "I2/I3", ["Builder required."] = "I4",
            ["Project required."] = "I5", ["Address required."] = "I6", ["City required."] = "I7", ["State required."] = "I8",
            ["S2E Number required."] = "I9/I14/I15", ["Plan Number required."] = "I10", ["Inspector required."] = "I11",
            ["Attempt must be 1 or greater."] = "I13", ["Purchase Order or Requisition Number required."] = "I16",
            [HeaderRules.NotifyPair] = "I17", ["Next action is required."] = "I18", ["Signature required."] = "I20",
            ["Lot required."] = "Lot", ["Block required."] = "Block"
        };

        // decryptFee: the plain amount text of a *_Secure value (Windows: RED's InspectCipher), or null when it cannot be
        // read on this device. Amounts are evaluated here only; they are never shown, logged or written.
        public static CompletionResult Validate(InspectionFile inspection, Func<string, JToken?> header, Func<string, string?>? decryptFee = null)
        {
            var result = new CompletionResult();
            foreach (var marker in HeaderRules.Markers(header))
            {
                string rule = HeaderRuleIds.TryGetValue(marker.Text, out var id) ? id : "header";
                result.Problems.Add(new CompletionProblem(rule, marker.Text, marker.Key, null, marker.Blocking));
            }

            // I12: a non-zero Other Fee needs its reason.
            decryptFee ??= DecryptWithInspectCipher;
            foreach (var (feeKey, reasonKey) in new[] { ("InspectorOtherFee1_Secure", "OtherFeeDescription1"), ("InspectorOtherFee2_Secure", "OtherFeeDescription2") })
            {
                string? cipher = HeaderRules.Text(header(feeKey));
                if (string.IsNullOrWhiteSpace(cipher))
                    continue; // no fee stored: INSPECT's amount is 0
                string? plain = decryptFee(cipher);
                if (plain == null || !decimal.TryParse(plain, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal amount) &&
                    !decimal.TryParse(plain, NumberStyles.Currency, CultureInfo.GetCultureInfo("en-US"), out amount))
                {
                    result.FeeRuleSkipped = true;
                    continue;
                }
                if (amount != 0 && HeaderRules.Blank(header(reasonKey)))
                {
                    result.Problems.Add(new CompletionProblem("I12", FeeReasonText, reasonKey, null, true));
                    break; // one message for the rule
                }
            }

            // I19: every item of every section.
            foreach (var section in inspection.Sections ?? new List<Section>())
                foreach (var item in section.Items ?? new List<Item>())
                {
                    if (item == null) continue;
                    string? problem = ItemProblem(item, inspection);
                    if (problem != null)
                        result.Problems.Add(new CompletionProblem("I19", problem, null, item, true));
                }
            return result;
        }

        // Item::Validate rules 1-4, in INSPECT's order.
        public static string? ItemProblem(Item item, InspectionFile inspection)
        {
            if (IsHidden(item) || !item.Required)
                return null;                                                         // rule 1
            if (item.ControlName == "DocumentButton" && FindDocument(item, inspection) == null)
                return $"Document is required for item {item.Number}";               // rule 2
            if (item.IsPictureRequired && (item.Pictures?.Count ?? 0) == 0)
                return $"Picture is required for item {item.Number}";                // rule 3
            string? value = item.Value?.ToString();
            // F-006 S7: INSPECT clears a picture-less ink item on load, so it validates as missing.
            if (InkItemService.KindOf(item) != InkControlKind.None && !InkItemService.HasInkPicture(item))
                value = null;
            if (string.IsNullOrWhiteSpace(value))
                return $"Value is required for item {item.Number}";                  // rule 4
            return null;
        }

        // Item::GetAttachment: the first attachment whose Filename starts with the Template's name without extension
        // (culture-sensitive StartsWith, as .NET's default). A blank Template never matches.
        public static JObject? FindDocument(Item item, InspectionFile inspection)
        {
            string? template = ExtensionString(item, "Template");
            if (string.IsNullOrWhiteSpace(template))
                return null;
            string stem = System.IO.Path.GetFileNameWithoutExtension(template);
            foreach (var attachment in inspection.Attachments ?? new List<object>())
            {
                var json = attachment as JObject ?? (attachment == null ? null : JObject.FromObject(attachment));
                string? name = json?["Filename"]?.Type == JTokenType.String ? (string?)json["Filename"] : null;
                if (name != null && name.StartsWith(stem, StringComparison.CurrentCulture))
                    return json;
            }
            return null;
        }

        // One Hide rule for the checklist, the filters and the completion check (F-004's).
        public static bool IsHidden(Item item) => ItemControls.IsHidden(item);

        private static string? ExtensionString(Item item, string key)
        {
            if (item.ExtensionData == null || !item.ExtensionData.TryGetValue(key, out var raw) || raw == null)
                return null;
            return raw is JValue value ? (value.Type == JTokenType.Null ? null : value.Value?.ToString()) : raw.ToString();
        }

        private static string? DecryptWithInspectCipher(string cipher)
        {
            try { return InspectCipher.Decrypt(cipher); }
            catch (Exception) { return null; }
        }

        // §4b L1: a BWT inspection is read-only when it has been saved before, has a top-level Signature and the last
        // validation was valid. Exactly "BWT" (L4: a BWI-coded file is never locked). Read-only never blocks Submit (L2).
        public static bool IsBwtLocked(InspectionFile inspection, Func<string, JToken?> header, bool hasSourcePath, CompletionResult lastResult)
        {
            if (!hasSourcePath)
                return false;
            if (!string.Equals(HeaderRules.Text(header("InspectionCode")) ?? inspection.InspectionCode, "BWT", StringComparison.Ordinal))
                return false;
            var signature = header("Signature");
            return signature != null && signature.Type != JTokenType.Null && lastResult.IsValid;
        }

        public static string RuleIdOf(CompletionProblem problem) => problem.Rule;
    }
}
