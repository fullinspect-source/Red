using InspectionEditor.Models;
using System;

namespace InspectionEditor.Services
{
    /// <summary>
    /// Interprets INS Required according to the control that Inspect2022 persists.
    /// Memo controls persist their answer in Comments, not Value.
    /// </summary>
    public static class ItemRequirementService
    {
        public static bool RequiresComment(Item? item) =>
            item?.Required == true &&
            string.Equals(item.ControlName?.Trim(), "Memo", StringComparison.OrdinalIgnoreCase);

        public static bool RequiresValue(Item? item) =>
            item?.Required == true && !RequiresComment(item);

        public static bool IsPrimaryRequirementMissing(Item? item)
        {
            if (item == null || !item.Required) return false;
            return RequiresComment(item)
                ? string.IsNullOrWhiteSpace(item.Comments)
                : string.IsNullOrWhiteSpace(item.Value?.ToString());
        }

        public static bool IsPrimaryRequirementSatisfied(Item? item) =>
            item?.Required == true && !IsPrimaryRequirementMissing(item);
    }
}
