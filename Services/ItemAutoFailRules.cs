using InspectionEditor.Models;
using System.Collections.Generic;

namespace InspectionEditor.Services
{
    /// <summary>
    /// RED's four automatic Fail rules (F-004 SPEC V5a-V5d, owner Q-15: keep them). INSPECT has none of them; the iPad
    /// implements the same four. They act only on items whose ControlName contains "passfail" and only ever write "Fail".
    /// Callers never run them while an item is loading or the report is read-only.
    /// </summary>
    public static class ItemAutoFailRules
    {
        // F-005 G2: grid cells ignore their per-item ControlName (CPR truck cells carry "PassFail"), so none of the four
        // rules ever touches them.
        public static bool IsPassFailControl(Item item) =>
            (item.ControlName?.ToLower() ?? "").Contains("passfail") && !ItemControls.IsGroupMember(item);

        /// <summary>V5a: a non-blank comment sets Fail unless the value already equals "fail" (any case, not trimmed).</summary>
        public static bool ApplyCommentRule(Item item, string? commentText)
        {
            if (!IsPassFailControl(item) || string.IsNullOrWhiteSpace(commentText)) return false;
            if ((item.Value?.ToString()?.ToLower() ?? "") == "fail") return false;
            item.Value = "Fail";
            return true;
        }

        /// <summary>
        /// V5b: a photo added while the item's comment text is not blank sets Fail, unless the value is ni, na or n/a (any
        /// case, not trimmed). Returns true when the rule applied (the value is now "Fail").
        /// </summary>
        public static bool ApplyPhotoRule(Item item, string? commentText)
        {
            if (!IsPassFailControl(item) || string.IsNullOrWhiteSpace(commentText)) return false;
            string oldValue = item.Value?.ToString()?.ToLower() ?? "";
            if (oldValue == "ni" || oldValue == "na" || oldValue == "n/a") return false;
            item.Value = "Fail";
            return true;
        }

        /// <summary>V5c (Save command): a blank value with a non-blank comment or at least one picture becomes Fail.</summary>
        public static List<Item> ApplySaveCommandSweep(InspectionFile inspection)
        {
            var changed = new List<Item>();
            foreach (var section in inspection.Sections)
            {
                foreach (var item in section.Items)
                {
                    if (!IsPassFailControl(item)) continue;
                    bool hasComment = !string.IsNullOrWhiteSpace(item.Comments);
                    bool hasPhotos = item.Pictures != null && item.Pictures.Count > 0;
                    if ((hasComment || hasPhotos) && string.IsNullOrEmpty(item.Value?.ToString()?.Trim() ?? ""))
                    {
                        item.Value = "Fail";
                        changed.Add(item);
                    }
                }
            }
            return changed;
        }

        /// <summary>V5d (every save): a blank value with a comment that starts with "[" after leading whitespace becomes Fail.</summary>
        public static List<Item> ApplyAutoSaveSweep(InspectionFile inspection)
        {
            var changed = new List<Item>();
            foreach (var section in inspection.Sections)
            {
                foreach (var item in section.Items)
                {
                    if (string.IsNullOrEmpty(item.Comments) || !item.Comments.TrimStart().StartsWith("[")) continue;
                    if (IsPassFailControl(item) && string.IsNullOrEmpty(item.Value?.ToString()?.Trim() ?? ""))
                    {
                        item.Value = "Fail";
                        changed.Add(item);
                    }
                }
            }
            return changed;
        }
    }
}
