using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace InspectionEditor.Services
{
    internal sealed record NumberpadDefaultProfile(
        double Minimum,
        double Maximum,
        double Increment,
        bool CameraByDefault = false);

    /// <summary>
    /// Product defaults learned from the completed INS archive. These are deliberately
    /// keyed by inspection code and normalized prompt text, matching RED's remembered-tool
    /// keys instead of unstable checklist item numbers or template-specific item IDs.
    /// </summary>
    internal static class NumberpadDefaultService
    {
        public const int MigrationVersion = 1;

        public static NumberpadDefaultProfile? Get(string? inspectionCode, string? prompt)
        {
            string code = (inspectionCode ?? "").Trim().ToUpperInvariant();
            string text = NormalizePrompt(prompt ?? "");

            if (code is "CPP" or "SRP")
                return GetPrePourProfile(text);

            if (code == "CPR")
            {
                if (IsTendonQuantity(text)) return Whole(0, 40);
                if (text.Contains("approximate temperature fahrenheit")) return Whole(50, 120);
                if (text.Contains("28 day design compressive strength psi")) return Whole(2500, 5000, 100);
                if (Regex.IsMatch(text, @"^truck \d+ gallons$")) return Whole(0, 25);
                if (Regex.IsMatch(text, @"^truck \d+ slump$")) return Quarter(0, 12);
                if (Regex.IsMatch(text, @"^truck \d+ temp$")) return Whole(50, 120);
            }

            if (code == "AFI")
            {
                if (text.Contains("number of returns")) return Whole(0, 10);
                if (text.Contains("conditioned floor area")) return Whole(500, 5000);
                if (text.Contains("design airflow")) return Whole(500, 2000);
                if (text.Contains("elevation above sea level")) return Whole(0, 2000);
                if (text.Contains("blower fan airflow from oem pressure table")) return Whole(500, 2000);
            }

            if (code == "HEF")
            {
                if (text == "quantity") return Whole(0, 10);
                if (text.Contains("number of bedrooms")) return Whole(0, 10);
                if (text.Contains("number of ceiling fans")) return Whole(0, 10, camera: true);
                if (text.Contains("cfl percentage")) return Whole(0, 100);
                if (ContainsAny(text, "square footage")) return Whole(500, 5000);
                if (ContainsAny(text, "home volume", "ac volume")) return Whole(5000, 60000);
                if (ContainsAny(text, "blower doot target", "blower door target", "blower door max cfm")) return Whole(0, 3000);
                if (ContainsAny(text, "mechanical ventilation cfm", "target fresh air cfm", "measured fresh air cfm")) return Whole(0, 300, camera: true);
                if (text.Contains("mechanical vent fan watts")) return Whole(0, 500);
            }

            if (code == "HER")
            {
                if (text.Contains("number of returns")) return Whole(0, 10);
                if (text.Contains("total duct leakage max cfm")) return Whole(0, 200);
                if (text.Contains("total square footage")) return Whole(500, 5000);
                if (text.Contains("total volume")) return Whole(5000, 60000);
            }

            if (code == "HET")
            {
                if (text.Contains("number of returns")) return Whole(0, 10);
                if (Regex.IsMatch(text, @"blower door test 1 (20|30|40) pa cfm")) return Whole(0, 2000, camera: true);
                if (text.Contains("blower door target")) return Whole(0, 3000);
                if (text.Contains("total square footage")) return Whole(500, 5000);
                if (text.Contains("cfm runtime target")) return Quarter(0, 24);
            }

            if (code == "IEF")
            {
                if (text.Contains("number of bedroom")) return Whole(0, 10);
                if (text.Contains("number of ceiling fans")) return Whole(0, 10, camera: true);
                if (text.Contains("ac square footage")) return Whole(500, 5000);
                if (text.Contains("ac volume")) return Whole(5000, 60000);
                if (text.Contains("blower door max cfm")) return Whole(0, 3000);
                if (text.Contains("blower door test 1 50 pa cfm")) return Whole(0, 3000, camera: true);
                if (ContainsAny(text, "target fresh air cfm", "measured fresh air cfm")) return Whole(0, 300, camera: true);
            }

            if (code == "IER")
            {
                if (text.Contains("number of returns")) return Whole(0, 10);
                if (text.Contains("ac square footage")) return Whole(500, 5000);
            }

            return null;
        }

        public static NumberpadDefaultProfile? GetFromPreferenceKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            int separator = key.IndexOf('|');
            if (separator <= 0 || separator >= key.Length - 1) return null;
            return Get(key[..separator], key[(separator + 1)..]);
        }

        public static string NormalizePrompt(string prompt)
        {
            string normalized = Regex.Replace(prompt.ToLowerInvariant(), @"\s+", " ").Trim();
            return Regex.Replace(normalized, @"[^\p{L}\p{Nd}\s?]", "");
        }

        private static NumberpadDefaultProfile? GetPrePourProfile(string text)
        {
            if (IsTendonQuantity(text)) return Whole(0, 40);
            if (text.Contains("corner tof to tog")) return Quarter(0, 30, camera: true);
            if (text.Contains("corner tof to bob")) return Quarter(20, 40, camera: true);
            if ((text.Contains("corner bw") || text.Contains("interior measurements location") && text.EndsWith(" bw")))
                return Quarter(0, 15);
            if (text.Contains("interior measurements location") && text.EndsWith(" sd")) return Quarter(0, 12);
            if (text.Contains("interior measurements location") && text.EndsWith(" bd")) return Quarter(0, 36);
            return null;
        }

        private static bool IsTendonQuantity(string text) =>
            text.Contains("front to back quantity") || text.Contains("side to side quantity");

        private static bool ContainsAny(string text, params string[] values)
        {
            foreach (string value in values)
                if (text.Contains(value)) return true;
            return false;
        }

        private static NumberpadDefaultProfile Whole(double min, double max, double increment = 1, bool camera = false) =>
            new(min, max, increment, camera);

        private static NumberpadDefaultProfile Quarter(double min, double max, bool camera = false) =>
            new(min, max, 0.25, camera);
    }
}
