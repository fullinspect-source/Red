using System;
using System.Collections.Generic;
using System.Linq;

namespace InspectionEditor.Services
{
    public sealed record ResultDisplay(string Text, string Background, string Foreground, bool HasResult);

    /// <summary>
    /// Owner 2026-10-19 (fix list item 14): the editor's result button shows the current result.
    /// <list type="bullet">
    /// <item>"NO RESULT" for StatusId 0 or 1 (Ordered, Scheduled).</item>
    /// <item>Failed (3) as "FAIL" plus the F-007 E2 next action in brackets: 1 next inspection, 2 PO required, 3 reinspection.</item>
    /// <item>Any other status: its name from the file's own StatusList in capitals; a status missing from the list says so.</item>
    /// </list>
    /// Colours follow the result picker: green pass family, orange Correct &amp; Proceed, purple fail-at-next-phase, red
    /// fail, blue engineer review, neutral when there is no result.
    /// </summary>
    public static class ResultLabel
    {
        public const string NoResult = "NO RESULT";
        public const string Neutral = "#E2E8F0", NeutralText = "#1F2933";
        public const string Green = "#1B5E20", Orange = "#E65100", Purple = "#7B1FA2", Red = "#B71C1C", Blue = "#0D47A1", Slate = "#475569";

        private static readonly HashSet<int> PassFamily = new() { 2, 7, 15, 23 }; // Passed, Complete, Pass w/ex, Field Complete
        private static readonly HashSet<int> FailFamily = new() { 3, 18, 25, 26 }; // Failed, Fail DNI, Review Fail, Field Error

        public static ResultDisplay For(int statusId, int nextActionId, IReadOnlyList<StatusOption>? statusList)
        {
            if (statusId is 0 or 1)
                return new ResultDisplay(NoResult, Neutral, NeutralText, false);

            if (statusId == HeaderRules.FailedStatusId)
            {
                string? reason = nextActionId switch
                {
                    1 => "next inspection",
                    2 => "PO required",
                    3 => "reinspection",
                    _ => null
                };
                return new ResultDisplay(reason == null ? "FAIL" : $"FAIL ({reason})", nextActionId == 1 ? Purple : Red, "#FFFFFF", true);
            }

            string? name = statusList?.FirstOrDefault(s => s.Id == statusId)?.Name?.Trim();
            string text = string.IsNullOrEmpty(name) ? $"STATUS {statusId}" : name.ToUpperInvariant();
            string background = PassFamily.Contains(statusId) ? Green
                : statusId == 5 ? Orange
                : statusId == 24 ? Blue
                : FailFamily.Contains(statusId) ? Red
                : Slate;
            return new ResultDisplay(text, background, "#FFFFFF", true);
        }
    }
}
