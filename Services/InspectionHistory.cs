using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services
{
    /// <summary>
    /// F-026 H1-H2: INSPECT's "Inspection History" rows from the Events array, read only. RED writes no events itself
    /// except F-021's creation event (R1-R3).
    /// </summary>
    public static class InspectionHistory
    {
        public const string Title = "Inspection History";
        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

        public sealed record Row(string Date, string Description, string User);

        // H2.
        public static string Description(int eventTypeId, string? additionalInfo) => eventTypeId switch
        {
            30 => "Created",
            31 => "Submitted",
            32 => "Uploaded to web site",
            33 => "Printed",
            34 => $"Moved to {additionalInfo}",
            35 => $"Transferred to {additionalInfo}",
            36 => "Approved",
            _ => ""
        };

        // H1: MM/dd/yyyy hh:mm tt in the device's time zone (a date with an offset is converted; one without is shown as
        // written). Tests pass their own zone.
        public static string DateText(JToken? date, TimeZoneInfo? zone = null)
        {
            string text = date == null || date.Type == JTokenType.Null ? "" : date.Type == JTokenType.Date ? ((DateTime)date).ToString("o", CultureInfo.InvariantCulture) : date.ToString();
            if (text.Length == 0) return "";
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset) && HasOffset(text))
                return TimeZoneInfo.ConvertTime(offset, zone ?? TimeZoneInfo.Local).DateTime.ToString("MM/dd/yyyy hh:mm tt", EnUs);
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var plain)
                ? plain.ToString("MM/dd/yyyy hh:mm tt", EnUs)
                : text;
        }

        private static bool HasOffset(string text) =>
            text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d\d:?\d\d$");

        public static List<Row> Rows(JToken? events, TimeZoneInfo? zone = null)
        {
            if (events is not JArray list) return new List<Row>();
            return list.OfType<JObject>().Select(e =>
            {
                int type = int.TryParse(e["EventTypeId"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int t) ? t : 0;
                string? info = e["AdditionalInfo"]?.Type == JTokenType.Null ? null : e["AdditionalInfo"]?.ToString();
                string user = e["UserDisplayName"]?.Type == JTokenType.Null ? "" : e["UserDisplayName"]?.ToString() ?? "";
                return new Row(DateText(e["Date"], zone), Description(type, info), user);
            }).ToList();
        }
    }
}
