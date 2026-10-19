using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.IO;

namespace InspectionEditor.Services
{
    // INSPECT values that RED reads live from {IF}\config.json each time and never keeps a copy of (F-003 O2), so RED and
    // INSPECT always use the same number. Read only, with shared access (S3).
    public static class InspectLiveSettings
    {
        // INSPECT's FormConfig constructor default (SET §4).
        public const int DefaultArchiveRetention = 40;

        // F-015 N1: {IF}\config.json, then config.json.bak, then 40. A file without the key also gives 40. A value that
        // is not a whole number makes that file unreadable, because INSPECT's whole-file deserialization fails then.
        public static int ReadArchiveRetention(string? inspectionsFolder) =>
            ReadInt(inspectionsFolder, "ArchiveRetention", DefaultArchiveRetention);

        // F-015 N4, shown in full before the cleanup exists (owner, 2026-10-18). The cutoff date is today - N. With 0 or
        // fewer days (N2: RED does not clean) or a cutoff before year 1, only the first sentence is shown.
        public static string ArchiveRetentionLine(int days, DateTime today)
        {
            string line = $"Archive retention: {days} days (from INSPECT's settings).";
            if (days <= 0 || days > (today.Date - DateTime.MinValue).Days)
                return line;
            string cutoff = today.Date.AddDays(-days).ToString("M/d/yyyy", CultureInfo.InvariantCulture);
            return line + $" At each start RED deletes archived inspections last changed on or before {cutoff}, as INSPECT does." +
                   " They are removed from every device that shares this Dropbox folder.";
        }

        // F-015 N2: RED refuses 0 or negative days; there is no upper bound.
        public static string? ArchiveRetentionRefusal(int days) =>
            days <= 0 ? $"INSPECT's Archive retention is {days} days. RED does not clean the Archive until it is 1 or more." : null;

        private static int ReadInt(string? inspectionsFolder, string key, int fallback)
        {
            if (string.IsNullOrWhiteSpace(inspectionsFolder))
                return fallback;
            string configPath = Path.Combine(inspectionsFolder, "config.json");
            foreach (string path in new[] { configPath, configPath + ".bak" })
            {
                if (StrandCredentialService.TryReadInspectConfig(path, out var config) && TryGetInt(config!, key, fallback, out int value))
                    return value;
            }
            return fallback;
        }

        // As Newtonsoft fills an int member: the key matched exactly, then ignoring case; an integer token, or a string
        // holding one. Anything else (null, a fraction, out of range) fails the whole file.
        private static bool TryGetInt(JObject config, string key, int fallback, out int value)
        {
            value = fallback;
            var property = config.Property(key, StringComparison.OrdinalIgnoreCase);
            if (property == null)
                return true;
            if (property.Value is JValue { Value: long number } && number is >= int.MinValue and <= int.MaxValue)
            {
                value = (int)number;
                return true;
            }
            return property.Value.Type == JTokenType.String &&
                   int.TryParse(property.Value.Value<string>(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }
    }
}
