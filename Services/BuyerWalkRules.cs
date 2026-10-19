using System;
using System.Globalization;
using System.Linq;
using InspectionEditor.Models;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    // Job data the F-009 prefill reads (F-021 fills it from the Jobs cache / Jobs/Read).
    public sealed record BuyerWalkJob(
        string? CustomerName, string? CustomerPhone, string? CustomerPhone2, string? CustomerEmail,
        string? AgentName, string? AgentPhone, string? AgentEmail, DateTime? ClosingDate,
        string? Superintendent, string? SuperintendentPhone, DateTime? ScheduleDate);

    // F-009 BWT / New Home Orientation ("BWI") family rules that are not PDF work.
    public static class BuyerWalkRules
    {
        public const int BuyerWalkDfwFormId = 129;
        public const int NewHomeOrientationServiceId = 121;
        private const string FirstWalkPrefix = "Buyerwalk1";
        private const string SecondWalkPrefix = "Buyerwalk2";
        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

        // W1 (InspectionWindow::ChangeSectionsFor<BLDR-B>DfwBuyerWalk, FormId 129 only): a reinspection loses its
        // "Scheduling" section and its "Sign" section becomes the second-walk control; a first walk keeps the
        // template's first-walk control. The second-walk name is derived from the template value (W1), never typed here.
        // Applied in memory on open (W2); written at the next save. Returns true when anything changed.
        public static bool ApplyFormId129Swap(InspectionFile inspection, SurgicalSaveService saveService)
        {
            if (inspection.FormId != BuyerWalkDfwFormId || inspection.Sections == null)
                return false;
            bool reinspection = IsReinspection(inspection);
            bool changed = false;
            var sign = inspection.Sections.FirstOrDefault(s => s != null && s.Name == "Sign");
            if (reinspection)
            {
                var scheduling = inspection.Sections.FirstOrDefault(s => s != null && s.Name == "Scheduling");
                if (scheduling != null)
                    changed |= saveService.RemoveSection(inspection, scheduling);
                string? current = sign?.ControlName;
                if (sign != null && current != null && current.StartsWith(FirstWalkPrefix, StringComparison.Ordinal))
                    changed |= saveService.SetSectionControlName(inspection, sign, SecondWalkPrefix + current.Substring(FirstWalkPrefix.Length));
            }
            else
            {
                // First walk: the template's "Sign" ControlName already is the first-walk identifier. A file that still
                // carries the second-walk one (its reinspection flag was cleared) goes back, as INSPECT sets Buyerwalk1.
                string? current = sign?.ControlName;
                if (sign != null && current != null && current.StartsWith(SecondWalkPrefix, StringComparison.Ordinal))
                    changed |= saveService.SetSectionControlName(inspection, sign, FirstWalkPrefix + current.Substring(SecondWalkPrefix.Length));
            }
            return changed;
        }

        private static bool IsReinspection(InspectionFile inspection)
        {
            if (inspection.ExtensionData == null || !inspection.ExtensionData.TryGetValue("IsReinspection", out var raw) || raw == null)
                return false;
            return raw is JValue value ? value.Type == JTokenType.Boolean && (bool)value : bool.TryParse(raw.ToString(), out bool b) && b;
        }

        // RTC/BW AsOrdinal: n % 100 in 11..13 -> th; else n % 10 = 1 st, 2 nd, 3 rd; else th.
        public static string Ordinal(int n)
        {
            int lastTwo = n % 100;
            if (lastTwo is >= 11 and <= 13) return n + "th";
            return (n % 10) switch { 1 => n + "st", 2 => n + "nd", 3 => n + "rd", _ => n + "th" };
        }

        // §6 prefill (Inspection::BuyerWalkInspection, ServiceId 121; used only by F-021's Add): items by exact Name, the
        // superintendent, and when scheduled StatusId 1, DateInspected and the Requests prefix
        // "Scheduled for {MMM} {nth} at {h:mm tt}." + " House closes on {MMM} {nth}." + " Super phone: {phone}." then
        // " " + the existing Requests, trimmed (R-mylist-add-manual-inspection item 47).
        public static void Prefill(JObject form, BuyerWalkJob job)
        {
            if (HeaderRules.Int(form["ServiceId"]) != NewHomeOrientationServiceId)
                return;
            void SetItem(string name, string? value, bool ignoreCase = false)
            {
                var item = (form["Sections"] as JArray)?.OfType<JObject>()
                    .SelectMany(s => (s["Items"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    .FirstOrDefault(i => string.Equals((string?)i["Name"], name, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                if (item != null)
                    item["Value"] = value == null ? JValue.CreateNull() : new JValue(value);
            }

            SetItem("Customer Name", job.CustomerName);
            SetItem("Customer Phone", job.CustomerPhone);
            SetItem("Customer Phone 2", job.CustomerPhone2);
            SetItem("Customer Email", job.CustomerEmail);
            SetItem("Sales Agent Name", job.AgentName);
            SetItem("Sales Agent Phone", job.AgentPhone);
            SetItem("Sales Agent Email", job.AgentEmail);
            // Nullable<DateTime>.ToString() in INSPECT's culture; "" when null.
            SetItem("Projected Closing Date", job.ClosingDate?.ToString(EnUs) ?? "");
            SetItem("Schedule follow-up inspection", null);
            if (!string.IsNullOrWhiteSpace(job.Superintendent))
            {
                form["SuperintendentName"] = job.Superintendent;
                SetItem("Superintendent", job.Superintendent, ignoreCase: true); // INSPECT: Name upper-cased == SUPERINTENDENT
            }

            string prefix = "";
            if (job.ScheduleDate.HasValue)
            {
                var at = job.ScheduleDate.Value;
                prefix = $"Scheduled for {at.ToString("MMM", EnUs)} {Ordinal(at.Day)} at {at.ToString("h:mm tt", EnUs)}.";
                form["StatusId"] = 1;
                form["DateInspected"] = HeaderRules.FormatDate(at);
            }
            if (job.ClosingDate.HasValue)
                prefix += $" House closes on {job.ClosingDate.Value.ToString("MMM", EnUs)} {Ordinal(job.ClosingDate.Value.Day)}.";
            if (!string.IsNullOrWhiteSpace(job.SuperintendentPhone))
                prefix += $" Super phone: {job.SuperintendentPhone}.";
            string existing = HeaderRules.Text(form["Requests"]) ?? "";
            form["Requests"] = (prefix + " " + existing).Trim();
        }
    }
}
